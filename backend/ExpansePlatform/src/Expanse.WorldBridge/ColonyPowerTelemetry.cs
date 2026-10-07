using System;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using HarmonyLib;

namespace Expanse.WorldBridge
{
    // The postfix observes the amount KSP actually accepted. It never changes
    // a resource request or a part. Mods that write PartResource.amount directly
    // are outside this measurement, including USI's nearby PDU transfers.
    public sealed partial class WorldBridgeAddon
    {
        private sealed class PowerTotals
        {
            public double Generation, Consumption;
        }

        private sealed class PowerWindow
        {
            public bool Valid;
            public string Reason;
            public double? EndUt, Seconds;
            public Dictionary<Guid, PowerTotals> Totals;
            public HashSet<Guid> Eligible, Interrupted;
            public Dictionary<Guid,Vessel> Vessels;
        }

        private const string PowerHarmonyId = "Expanse.WorldBridge.ColonyPowerTelemetry";
        private static WorldBridgeAddon activePowerObserver;
        private readonly object powerLock = new object();
        private Dictionary<Guid, PowerTotals> powerTotals = new Dictionary<Guid, PowerTotals>();
        private Dictionary<Guid, Vessel> powerTrackedVessels = new Dictionary<Guid, Vessel>();
        private HashSet<Guid> powerInterruptedVessels = new HashSet<Guid>();
        private double? powerWindowStartUt;
        private bool powerWindowActive, powerWindowInvalid, powerOverflow;
        private string powerHookFailure;
        private Harmony powerHarmony;
        private MethodInfo powerTarget, powerPostfix;

        private void InstallPowerObserver()
        {
            activePowerObserver = this;
            try
            {
                powerTarget = typeof(Part).GetMethod("requestResource", BindingFlags.Instance | BindingFlags.NonPublic,
                    null, new[] { typeof(Part), typeof(int), typeof(ResourceFlowMode), typeof(double), typeof(bool) }, null);
                powerPostfix = typeof(WorldBridgeAddon).GetMethod("PowerRequestPostfix", BindingFlags.Static | BindingFlags.NonPublic);
                if (powerTarget == null || powerTarget.ReturnType != typeof(double) || powerPostfix == null)
                    throw new MissingMethodException("KSP resource request signature changed.");
                powerHarmony = new Harmony(PowerHarmonyId);
                powerHarmony.Patch(powerTarget, postfix: new HarmonyMethod(powerPostfix));
            }
            catch (Exception ex)
            {
                powerHookFailure = "Power request observer unavailable (" + ex.GetType().Name + ").";
                QueueWorkerLog(powerHookFailure);
                try { if (powerHarmony != null && powerTarget != null && powerPostfix != null) powerHarmony.Unpatch(powerTarget, powerPostfix); } catch { }
                powerHarmony = null;
            }
        }

        private void RemovePowerObserver()
        {
            if (ReferenceEquals(activePowerObserver, this)) activePowerObserver = null;
            ResetPowerTelemetry();
            try { if (powerHarmony != null && powerTarget != null && powerPostfix != null) powerHarmony.Unpatch(powerTarget, powerPostfix); }
            catch { }
            powerHarmony = null;
        }

        // Named parameters are the KSP private method's exact metadata names.
        // Only this inner method is patched: public overloads funnel through it,
        // so one fulfilled request is counted once.
        private static void PowerRequestPostfix(Part __instance, int resourceID, double demand, bool simulate, double __result)
        {
            WorldBridgeAddon observer = activePowerObserver;
            long timedAt = !ReferenceEquals(observer, null) && (System.Threading.Interlocked.Increment(ref observer.powerCallbackCount) & 63) == 0
                ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            try
            {
                if (observer == null || !observer.powerWindowActive) return;
                if (CheatOptions.InfiniteElectricity) { observer.MarkPowerWindowInterrupted(); return; }
                if (!EligiblePowerTransaction(resourceID, demand, simulate, __result, false) ||
                    __instance == null || __instance.vessel == null) return;
                Vessel vessel = __instance.vessel;
                if (!vessel.loaded || vessel.packed) return;
                observer.RecordPowerTransfer(vessel.id, __result);
            }
            catch
            {
                // A telemetry failure must never escape into KSP's resource path.
                try { if (activePowerObserver != null) activePowerObserver.MarkPowerWindowInterrupted(); } catch { }
            }
            finally
            {
                if (timedAt != 0) observer.powerCallbackTiming.Record(System.Diagnostics.Stopwatch.GetTimestamp() - timedAt);
            }
        }

        internal static bool EligiblePowerTransaction(int resourceId, double demand, bool simulate,
            double accepted, bool infiniteElectricity)
        {
            return resourceId == PartResourceLibrary.ElectricityHashcode && !simulate && !infiniteElectricity &&
                !Double.IsNaN(demand) && !Double.IsInfinity(demand) &&
                !Double.IsNaN(accepted) && !Double.IsInfinity(accepted) &&
                ((demand > 0 && accepted > 0) || (demand < 0 && accepted < 0));
        }

        private void RecordPowerTransfer(Guid vesselId, double accepted)
        {
            lock (powerLock)
            {
                if (!powerWindowActive || powerWindowInvalid) return;
                PowerTotals totals;
                if (!powerTotals.TryGetValue(vesselId, out totals))
                {
                    if (powerTotals.Count >= 128) { powerOverflow = true; powerWindowInvalid = true; return; }
                    totals = new PowerTotals();
                    powerTotals.Add(vesselId, totals);
                }
                if (accepted < 0) totals.Generation -= accepted;
                else totals.Consumption += accepted;
                if (Double.IsInfinity(totals.Generation) || Double.IsInfinity(totals.Consumption) ||
                    totals.Generation > 1e12 || totals.Consumption > 1e12) powerWindowInvalid = true;
            }
        }

        private void MarkPowerWindowInterrupted()
        {
            lock (powerLock) powerWindowInvalid = true;
        }

        private void ResetPowerTelemetry()
        {
            lock (powerLock)
            {
                powerWindowActive = false;
                powerWindowInvalid = false;
                powerOverflow = false;
                powerWindowStartUt = null;
                powerTotals.Clear();
                powerTrackedVessels.Clear();
                powerInterruptedVessels.Clear();
            }
        }

        private bool IsStandardPowerStep()
        {
            try { return HighLogic.LoadedSceneIsFlight && !loadUnresolved && !FlightDriver.Pause &&
                paused != true && !CheatOptions.InfiniteElectricity && TimeWarp.CurrentRateIndex == 0 &&
                Math.Abs(TimeWarp.CurrentRate - 1f) < 0.001f; }
            catch { return false; }
        }

        private void CheckPowerWindowConditions()
        {
            if (powerWindowActive && !IsStandardPowerStep()) MarkPowerWindowInterrupted();
            if (!powerWindowActive) return;
            lock (powerLock)
                foreach (KeyValuePair<Guid, Vessel> entry in powerTrackedVessels)
                    if (entry.Value == null || !entry.Value.loaded || entry.Value.packed)
                        powerInterruptedVessels.Add(entry.Key);
        }

        private void SetPowerEligibleVessels(ColonySnapshot snapshot)
        {
            lock (powerLock)
            {
                powerTrackedVessels.Clear();
                if (!powerWindowActive || FlightGlobals.Vessels == null) return;
                foreach (Vessel vessel in FlightGlobals.Vessels)
                    if (vessel != null && vessel.loaded && !vessel.packed &&
                        snapshot.Vessels.Exists(v => v.VesselId == vessel.id.ToString("D")))
                        powerTrackedVessels[vessel.id] = vessel;
            }
        }

        private PowerWindow ClosePowerWindow(double? ut)
        {
            bool standardStep = IsStandardPowerStep();
            lock (powerLock)
            {
                double seconds = powerWindowStartUt.HasValue && ut.HasValue ? ut.Value - powerWindowStartUt.Value : 0;
                PowerWindow window = new PowerWindow
                {
                    Valid = powerHarmony != null && powerWindowActive && !powerWindowInvalid && !powerOverflow &&
                            standardStep && seconds >= 0.3 && seconds <= 30,
                    Reason = powerHookFailure ?? (powerOverflow ? "The power observer reached its vessel limit." :
                        !standardStep ? "Power rates require active flight at normal speed." :
                        "Waiting for a complete normal-speed power interval."),
                    EndUt = ut, Seconds = seconds > 0 ? (double?)seconds : null,
                    Totals = powerTotals,
                    Eligible = new HashSet<Guid>(powerTrackedVessels.Keys),
                    Interrupted = powerInterruptedVessels
                    ,Vessels=powerTrackedVessels
                };
                powerTotals = new Dictionary<Guid, PowerTotals>();
                powerTrackedVessels = new Dictionary<Guid, Vessel>();
                powerInterruptedVessels = new HashSet<Guid>();
                powerWindowStartUt = standardStep ? ut : null;
                powerWindowActive = standardStep && powerHarmony != null && ut.HasValue;
                powerWindowInvalid = false;
                powerOverflow = false;
                try
                {
                if(window.Valid&&window.Seconds.HasValue&&window.EndUt.HasValue)
                    foreach(var vessel in window.Eligible.Where(id=>!window.Interrupted.Contains(id)))
                    {
                        if(!window.Vessels.TryGetValue(vessel,out var native)||!EnsureAverageMode(native)){powerAverage.MarkPartial();continue;}
                        window.Totals.TryGetValue(vessel,out var total);
                        powerAverage.Add(vessel.ToString("D"),window.EndUt.Value-window.Seconds.Value,window.EndUt.Value,total?.Generation??0,total?.Consumption??0,"fulfilled-part-requests",false,0);
                    }
                if(!window.Valid||window.Interrupted.Count>0)powerAverage.MarkPartial();
                if(ut.HasValue)powerAverage.Tick(ProductionContext,RealSeconds,ut.Value);
                }
                catch{powerAverage.Reset(ProductionContext);}
                return window;
            }
        }

        private static CapturedPowerFlow CapturePowerFlow(Vessel vessel, PowerWindow window)
        {
            if (!vessel.loaded || vessel.parts == null)
                return new CapturedPowerFlow("Facility is unloaded; live power flow is unavailable.", window.EndUt);
            if (vessel.packed)
                return new CapturedPowerFlow("Facility is on rails; live power flow is unavailable.", window.EndUt);
            if (!window.Valid || !window.Seconds.HasValue)
                return new CapturedPowerFlow(window.Reason, window.EndUt);
            if (!window.Eligible.Contains(vessel.id) || window.Interrupted.Contains(vessel.id))
                return new CapturedPowerFlow("Waiting for a complete loaded facility interval.", window.EndUt);
            PowerTotals totals;
            window.Totals.TryGetValue(vessel.id, out totals);
            return new CapturedPowerFlow(null, window.EndUt, window.Seconds,
                totals == null ? 0 : totals.Generation, totals == null ? 0 : totals.Consumption);
        }
    }
}
