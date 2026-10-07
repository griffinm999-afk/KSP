using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;

namespace Expanse.WorldBridge
{
    public sealed partial class WorldBridgeAddon
    {
        private string attemptContext, clockContext;
        private long colonyAttempt, clockContextGeneration, powerCallbackCount;
        private long workerContextGeneration;
        private ColonyCapture completedCapture;
        private ColonySnapshot completedColony;
        private readonly ObservationTiming captureTiming = new ObservationTiming(), derivationTiming = new ObservationTiming(),
            sampleTiming = new ObservationTiming(), powerCallbackTiming = new ObservationTiming(),
            serializationTiming = new ObservationTiming(), transportTiming = new ObservationTiming();
        private readonly ObservationCount captureParts = new ObservationCount(), captureVessels = new ObservationCount(), replacedSamples = new ObservationCount();
        private long nextPerformanceReport = Stopwatch.GetTimestamp() + 60L * Stopwatch.Frequency;

        private long NextColonyAttempt(string context)
        {
            if (attemptContext != context) { attemptContext = context; colonyAttempt = 0; }
            return ++colonyAttempt;
        }
        private void PublishLatestSample(ClockSample sample)
        {
            string context = sample.SessionId + "/" + sample.LoadEpoch + "/" + sample.WorldId + "/" + sample.SaveFolder + "/" + sample.Scene + "/" + sample.ActiveWorld + "/" + (sample.Capture != null);
            if (context != clockContext) { clockContext = context; Interlocked.Increment(ref clockContextGeneration); }
            sample.ContextGeneration = Interlocked.Read(ref clockContextGeneration);
            if (Interlocked.Exchange(ref latest, sample) != null) replacedSamples.Add(1);
        }
        private ClockSample TakeLatestSample() { return Interlocked.Exchange(ref latest, null); }

        // Called only by the existing publisher worker. Cached captures never
        // advance the census tracker twice or repeat mutable budget application.
        private void CompleteColonySample(ClockSample sample)
        {
            if (sample.ContextGeneration != Interlocked.Read(ref clockContextGeneration)) return;
            if (sample.ContextGeneration != workerContextGeneration)
            {
                workerContextGeneration = sample.ContextGeneration;
                completedCapture = null; completedColony = null;
                vesselCensusTracker.Invalidate();
            }
            if (sample.Capture == null) { completedCapture = null; completedColony = null; vesselCensusTracker.Invalidate(); return; }
            string expected = sample.SessionId + "/" + sample.LoadEpoch + "/" + (sample.WorldId ?? sample.SaveFolder);
            if (sample.Capture.EpochContext != expected || sample.Capture.Scene != sample.Scene)
            { completedCapture = null; completedColony = null; vesselCensusTracker.Invalidate(); return; }
            if (!ReferenceEquals(completedCapture, sample.Capture))
            {
                long start = Stopwatch.GetTimestamp();
                try { completedColony = sample.Capture.Derive(vesselCensusTracker); }
                catch { completedColony = new ColonySnapshot { Status = "unavailable", Reason = "Background colony derivation failed.", ObservedUt = sample.UtSeconds,
                    VesselCensus = vesselCensusTracker.Observe(expected, sample.Scene, null, null, new string[0], sample.Capture.Attempt) }; }
                finally { derivationTiming.Record(Stopwatch.GetTimestamp() - start); }
                completedCapture = sample.Capture;
            }
            sample.Colony = completedColony;
        }
        private void ReportPerformance()
        {
            long now = Stopwatch.GetTimestamp(); if (now < nextPerformanceReport) return;
            nextPerformanceReport = now + 60L * Stopwatch.Frequency;
            string report = "Observation performance (~60s): " + sampleTiming.Drain("mainSample") + "; " + captureTiming.Drain("mainCapture") +
                "; " + derivationTiming.Drain("workerDerivation") + "; " + serializationTiming.Drain("workerJson") +
                "; " + transportTiming.Drain("workerTransport") + "; " + powerCallbackTiming.Drain("powerCallbackSampled1in64") +
                "; powerCallbacks=" + Interlocked.Exchange(ref powerCallbackCount, 0).ToString(CultureInfo.InvariantCulture) +
                "; inspectedParts=" + captureParts.Drain() + "; inspectedVessels=" + captureVessels.Drain() + "; replacedClockSamples=" + replacedSamples.Drain();
            WorkerLog(report);
            Interlocked.Exchange(ref pendingTimingLog, report + "; diagnosticPath=" + Interlocked.CompareExchange(ref workerDiagnosticLogPath, null, null));
        }
        private static Guid[] CaptureVesselIds(IList<Vessel> vessels)
        {
            if (vessels == null) return null;
            var ids = new Guid[Math.Min(513, vessels.Count)];
            for (int i = 0; i < ids.Length; i++) ids[i] = vessels[i] == null ? Guid.Empty : vessels[i].id;
            return ids;
        }
        // Stable two-pass priority selection avoids sorting live Unity objects.
        // This iterator is consumed synchronously by main-thread capture only.
        private static IEnumerable<Vessel> ColonyCandidates()
        {
            var vessels = FlightGlobals.Vessels;
            for (int priority = 0; priority < 2; priority++)
                for (int i = 0; i < vessels.Count; i++)
                {
                    var vessel = vessels[i];
                    if (vessel == null) continue;
                    bool minmus = vessel.mainBody != null && vessel.mainBody.bodyName == "Minmus";
                    if (minmus == (priority == 0)) yield return vessel;
                }
        }
        private static Guid[] CaptureProtoIds(IList<ProtoVessel> vessels)
        {
            if (vessels == null) return null;
            var ids = new Guid[Math.Min(513, vessels.Count)];
            for (int i = 0; i < ids.Length; i++) ids[i] = vessels[i] == null ? Guid.Empty : vessels[i].vesselID;
            return ids;
        }
        private static string[] CaptureConverterNames(IList<ResourceRatio> rows, ColonySnapshot snapshot)
        {
            const int maximum = 128;
            if (rows.Count > maximum) MarkTruncated(snapshot, "Converter resource capture exceeded 128 entries.");
            var result = new string[Math.Min(rows.Count, maximum)];
            for (int i = 0; i < result.Length; i++) result[i] = Bound(rows[i].ResourceName, 50);
            return result;
        }
        private static void CaptureTank(ColonySnapshot snapshot, List<ColonyTank> tanks, string resource, double amount, double capacity,
            bool warehouse, bool? local, bool? flow, string role)
        {
            if (tanks.Count >= 4096) { MarkTruncated(snapshot, "Tank capture exceeded 4096 entries per vessel."); return; }
            if (resource != null && resource.Length > 1024) { MarkTruncated(snapshot, "Tank resource name exceeded its capture bound."); return; }
            tanks.Add(new ColonyTank { Resource = resource, Amount = amount, Capacity = capacity,
                WarehousePresent = warehouse, LocalWarehouseOn = local, FlowEnabled = flow, Role = role });
        }
        private static CapturedPowerEstimate CapturePowerEstimate(Vessel vessel)
        {
            if (!vessel.loaded || !vessel.packed || vessel.parts == null) return null;
            var inputs = new List<double[]>(); var outputs = new List<double[]>();
            foreach (Part part in vessel.parts)
            {
                if (part == null) continue;
                foreach (PartModule module in part.Modules)
                {
                    var converter = module as ModuleResourceConverter;
                    if (converter == null || !converter.IsActivated) continue;
                    // Capture at most the bounded recipe vector, preserving module
                    // boundaries. Pure positivity/finite checks and sums run later.
                    if (inputs.Count >= 512 || converter.inputList != null && converter.inputList.Count > 128 || converter.outputList != null && converter.outputList.Count > 128) return null;
                    inputs.Add(converter.inputList == null ? new double[0] : converter.inputList.Where(r => r.ResourceName == "ElectricCharge").Select(r => r.Ratio).ToArray());
                    outputs.Add(converter.outputList == null ? new double[0] : converter.outputList.Where(r => r.ResourceName == "ElectricCharge").Select(r => r.Ratio).ToArray());
                }
            }
            return new CapturedPowerEstimate(inputs.ToArray(), outputs.ToArray());
        }
        private static ColonyProductionPotential CopyProductionPotential(ColonyProductionPotential value)
        {
            if (value == null) return null;
            return new ColonyProductionPotential { SampleUt = value.SampleUt, EfficiencyMultiplier = value.EfficiencyMultiplier,
                RequirementMultiplier = value.RequirementMultiplier, Rates = new ColonyProductionVector {
                    Inputs = CopyRates(value.Rates.Inputs), Outputs = CopyRates(value.Rates.Outputs),
                    Requirements = value.Rates.Requirements.Select(r => new ColonyProductionRequirementTelemetry { Resource = r.Resource, Amount = r.Amount }).ToArray() } };
        }
        private static ColonyProductionRateTelemetry[] CopyRates(ColonyProductionRateTelemetry[] rows)
        { return rows.Select(r => new ColonyProductionRateTelemetry { Resource = r.Resource, FlowMode = r.FlowMode, UnitsPerSecond = r.UnitsPerSecond, DumpExcess = r.DumpExcess }).ToArray(); }
    }
}
