using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Expanse.WorldBridge
{
    public sealed partial class ColonyRuntime
    {
        sealed class NonActiveHeatTick { internal string Context; internal float FixedTime; }
        static readonly Dictionary<object, NonActiveHeatTick> NonActiveHeatTicks = new Dictionary<object, NonActiveHeatTick>();
        static Type NonActiveHeatVesselType, NonActiveHeatSimulatorType, NonActiveHeatModuleType;
        static MethodInfo NonActiveHeatSimulate;

        static void RegisterNonActiveSystemHeat(Harmony patch, Assembly assembly)
        {
            var vesselType = assembly.GetType("SystemHeat.SystemHeatVessel", true);
            var simulatorType = assembly.GetType("SystemHeat.SystemHeatSimulator", true);
            var fixedUpdate = vesselType.GetMethod("FixedUpdate", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
            var simulate = simulatorType.GetMethod("Simulate", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
            if (fixedUpdate == null || simulate == null) throw new MissingMethodException("Reviewed SystemHeat native update methods are unavailable.");
            NonActiveHeatVesselType = vesselType; NonActiveHeatSimulatorType = simulatorType;
            NonActiveHeatModuleType = assembly.GetType("SystemHeat.ModuleSystemHeat", true); NonActiveHeatSimulate = simulate;
            patch.Patch(simulate, prefix: new HarmonyMethod(typeof(ColonyRuntime).GetMethod(nameof(RecordNativeSystemHeatSimulation), BindingFlags.Static | BindingFlags.NonPublic)));
            patch.Patch(fixedUpdate, postfix: new HarmonyMethod(typeof(ColonyRuntime).GetMethod(nameof(AdvanceNonActiveSystemHeat), BindingFlags.Static | BindingFlags.NonPublic)));
        }

        static void StopNonActiveSystemHeat()
        {
            NonActiveHeatTicks.Clear(); NonActiveHeatVesselType = NonActiveHeatSimulatorType = NonActiveHeatModuleType = null; NonActiveHeatSimulate = null;
        }

        // Stamp before native entry: even a partial/throwing native simulation
        // must not be repeated by the supplementary callback in this fixed tick.
        static void RecordNativeSystemHeatSimulation(object __instance)
        {
            try
            {
                if (Current == null || __instance == null || __instance.GetType() != NonActiveHeatSimulatorType || !Finite(Time.fixedTime)) return;
                MarkNonActiveHeatTick(__instance, Current.ContextKey, Time.fixedTime);
            }
            catch { /* Never interrupt an existing native update. */ }
        }

        static bool MarkNonActiveHeatTick(object simulator, string context, float fixedTime)
        {
            NonActiveHeatTick tick;
            if (NonActiveHeatTicks.TryGetValue(simulator, out tick))
            {
                if (tick.Context == context && tick.FixedTime == fixedTime) return false;
            }
            else
            {
                if (NonActiveHeatTicks.Count >= 4096) return false;
                tick = new NonActiveHeatTick(); NonActiveHeatTicks.Add(simulator, tick);
            }
            tick.Context = context; tick.FixedTime = fixedTime; return true;
        }

        static bool OwnsNonActiveHeatVessel(ColonyRuntime runtime, Vessel vessel)
        {
            if (runtime == null || !runtime.Ready || runtime.state == null || HighLogic.CurrentGame == null ||
                !ReferenceEquals(runtime.selectedGame, HighLogic.CurrentGame) || HighLogic.CurrentGame.scenarios == null ||
                HighLogic.CurrentGame.scenarios.Count(s => ReferenceEquals(s.moduleRef, runtime)) != 1 ||
                vessel == null || !vessel.loaded || !vessel.LandedOrSplashed || vessel.isEVA || vessel.mainBody == null ||
                FlightGlobals.Vessels == null || FlightGlobals.Vessels.Count > 4096 ||
                FlightGlobals.Vessels.Count(v => v != null && v.id == vessel.id) != 1 || !FlightGlobals.Vessels.Contains(vessel) ||
                vessel.parts == null || vessel.parts.Count == 0 || vessel.parts.Count > 256 ||
                vessel.parts.Any(p => p == null || p.vessel != vessel || p.persistentId == 0)) return false;
            var facilities = runtime.state.Colonies.SelectMany(c => c.Facilities).Where(f => f.VesselId == vessel.id.ToString("D")).Take(2).ToArray();
            if (facilities.Length != 1 || facilities[0].ProductionOwner != "physical") return false;
            var ids = facilities[0].PartIds;
            return ids != null && ids.Count == vessel.parts.Count && ids.All(id => id != 0) && ids.Distinct().Count() == ids.Count &&
                vessel.parts.Select(p => p.persistentId).Distinct().Count() == ids.Count && new HashSet<uint>(ids).SetEquals(vessel.parts.Select(p => p.persistentId));
        }

        static bool OwnsNonActiveHeatLoops(object simulator, Vessel vessel)
        {
            var loops = UtilityRead(simulator, "HeatLoops") as IList;
            if (loops == null || loops.Count == 0 || loops.Count > 64 || loops.Cast<object>().Any(l => l == null) || loops.Cast<object>().Distinct().Count() != loops.Count) return false;
            foreach (var loop in loops)
            {
                var members = UtilityRead(loop, "LoopModules") as IList;
                if (loop.GetType().Assembly != NonActiveHeatSimulatorType.Assembly || loop.GetType().FullName != "SystemHeat.HeatLoop" ||
                    members == null || members.Count == 0 || members.Count > 256 || members.Cast<object>().Distinct().Count() != members.Count) return false;
                foreach (var member in members)
                {
                    var heat = member as PartModule;
                    if (heat == null || heat.GetType() != NonActiveHeatModuleType || heat.vessel != vessel || heat.part == null ||
                        !vessel.parts.Contains(heat.part) || heat.part.Modules.Cast<PartModule>().Count(m => ReferenceEquals(m, heat)) != 1 ||
                        !ReferenceEquals(UtilityRead(heat, "Loop"), loop)) return false;
                }
            }
            return true;
        }

        // The installed 0.9.1 vessel callback simulates only ActiveVessel.
        // Run its genuine simulator after that callback for our nonactive,
        // loaded facilities; packing and native resource modules are unchanged.
        static void AdvanceNonActiveSystemHeat(object __instance)
        {
            try
            {
                if (Current == null || NonActiveHeatSimulate == null || __instance == null || __instance.GetType() != NonActiveHeatVesselType ||
                    !HighLogic.LoadedSceneIsFlight || !PositiveFinite(TimeWarp.CurrentRate) || FlightDriver.Pause || !PositiveFinite(Time.timeScale) ||
                    !Finite(Time.fixedTime) || !PositiveFinite(TimeWarp.fixedDeltaTime)) return;
                var vessel = UtilityRead(__instance, "Vessel") as Vessel;
                if (vessel == FlightGlobals.ActiveVessel || !OwnsNonActiveHeatVessel(Current, vessel) ||
                    vessel.GetComponents(NonActiveHeatVesselType).Length != 1 ||
                    !ReferenceEquals(vessel.GetComponents(NonActiveHeatVesselType)[0], __instance) ||
                    !Finite(vessel.altitude) || !Finite(vessel.speed) || vessel.speed < 0 || vessel.altitude > float.MaxValue || vessel.altitude < -float.MaxValue || vessel.speed > float.MaxValue) return;
                var simulator = UtilityRead(__instance, "Simulator");
                if (simulator == null || simulator.GetType() != NonActiveHeatSimulatorType || !OwnsNonActiveHeatLoops(simulator, vessel) ||
                    !MarkNonActiveHeatTick(simulator, Current.ContextKey, Time.fixedTime)) return;
                NonActiveHeatSimulatorType.GetProperty("SimulationBody").SetValue(simulator, vessel.mainBody, null);
                NonActiveHeatSimulatorType.GetProperty("SimulationAltitude").SetValue(simulator, (float)vessel.altitude, null);
                NonActiveHeatSimulatorType.GetProperty("SimulationSpeed").SetValue(simulator, (float)vessel.speed, null);
                NonActiveHeatSimulate.Invoke(simulator, null);
            }
            catch { /* Uncertain native state supplies no supplementary advancement. */ }
        }
    }
}
