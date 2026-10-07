using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;

namespace Expanse.WorldBridge
{
    // Ownership boundary: constructed from a fresh, exclusively owned primitive
    // DTO graph. The producer never touches that graph again. The private graph
    // is read-only after capture; Derive builds a separate worker-owned result.
    internal sealed class ColonyCapture
    {
        private readonly ColonySnapshot data;
        private readonly Guid[] worldIds, saveIds;
        internal readonly string EpochContext, Scene;
        internal readonly long Attempt;
        internal ColonyCapture(ColonySnapshot ownedData, string epoch, string scene, long attempt, Guid[] world, Guid[] saved)
        { data = ownedData; EpochContext = epoch; Scene = scene; Attempt = attempt; worldIds = world; saveIds = saved; }

        internal ColonySnapshot Derive(VesselCensusTracker tracker)
        {
            var result = new ColonySnapshot { Status = data.Status, Reason = data.Reason, ObservedUt = data.ObservedUt, Wolf = data.Wolf };
            int people = 0, rosterBytes = 0;
            foreach (var source in data.Vessels)
            {
                var row = new ColonyVessel { VesselId = source.VesselId, Name = source.Name, Body = source.Body, Biome = source.Biome,
                    ObservationBasis = source.ObservationBasis, Latitude = source.Latitude, Longitude = ((source.Longitude + 180d) % 360d + 360d) % 360d - 180d,
                    Crew = source.Crew, CrewRosterComplete = source.CrewRosterComplete, PhysicalCrewCapacity = source.PhysicalCrewCapacity,
                    Power = source.CapturedFlow == null ? source.Power : source.CapturedFlow.Derive(), PowerEstimate = source.CapturedPower == null ? null : source.CapturedPower.Derive(),
                    LifeSupport=source.LifeSupport,PowerAverage=source.PowerAverage };
                row.CrewRoster.AddRange(source.CrewRoster);
                int bytes = row.CrewRoster.Sum(p => Encoding.UTF8.GetByteCount(p.Name) + Encoding.UTF8.GetByteCount(p.Profession) + 48);
                if (row.CrewRosterComplete && (people + row.CrewRoster.Count > 96 || rosterBytes + bytes > 8192))
                { row.CrewRoster.Clear(); row.CrewRosterComplete = false; }
                else if (row.CrewRosterComplete) { people += row.CrewRoster.Count; rosterBytes += bytes; }
                foreach (var converter in source.Converters)
                    row.Converters.Add(new ColonyConverter { PartName = converter.PartName, Recipe = converter.Recipe, Running = converter.Running,
                        Inputs = converter.Inputs.Distinct().Take(16).ToArray(), Outputs = converter.Outputs.Distinct().Take(16).ToArray() });
                var tanks = new Dictionary<string, ColonyTank>(StringComparer.Ordinal);
                foreach (var tank in source.Tanks)
                {
                    if (String.IsNullOrWhiteSpace(tank.Resource) || !Finite(tank.Amount) || !Finite(tank.Capacity) || tank.Amount < 0 || tank.Capacity < tank.Amount) continue;
                    string key = tank.Resource + "\0" + tank.Role + "\0" + Flag(tank.WarehousePresent) + Flag(tank.LocalWarehouseOn) + Flag(tank.FlowEnabled);
                    ColonyTank total;
                    if (!tanks.TryGetValue(key, out total))
                    {
                        total = new ColonyTank { Resource = tank.Resource.Length <= 50 ? tank.Resource : tank.Resource.Substring(0, 50), Role = tank.Role, WarehousePresent = tank.WarehousePresent,
                            LocalWarehouseOn = tank.LocalWarehouseOn, FlowEnabled = tank.FlowEnabled };
                        tanks.Add(key, total);
                    }
                    total.Amount += tank.Amount; total.Capacity += tank.Capacity;
                }
                if (tanks.Count > 24) { result.Status = "truncated"; result.Reason = "A settlement vessel has more than 24 distinct tank states."; }
                row.Tanks.AddRange(tanks.Values.OrderBy(t => t.Resource, StringComparer.Ordinal).Take(24));
                if (source.Production != null)
                {
                    row.Production = new ColonyProductionTelemetry { Status = source.Production.Status, Reason = source.Production.Reason,
                        InventoryStatus = source.Production.InventoryStatus, ObservedUt = source.Production.ObservedUt,
                        BudgetOmittedModuleCount=source.Production.BudgetOmittedModuleCount,BudgetSelectionSequence=source.Production.BudgetSelectionSequence };
                    row.Production.Modules.AddRange(source.Production.Modules);
                    // Keep the bounded capture intact. Wire serialization allocates
                    // against the actual frame size, rather than spending a fixed
                    // budget in vessel order before the frame is known.
                }
                result.Vessels.Add(row);
            }
            result.VesselCensus = tracker.Observe(EpochContext, Scene,
                result.Status == "observed" ? worldIds : null, result.Status == "observed" ? saveIds : null,
                result.Vessels.Select(v => v.VesselId), Attempt);
            return result;
        }
        private static bool Finite(double n) { return !Double.IsNaN(n) && !Double.IsInfinity(n); }
        private static string Flag(bool? v) { return !v.HasValue ? "null" : v.Value ? "true" : "false"; }
    }

    internal sealed class CapturedPowerFlow
    {
        private readonly string unavailableReason;
        private readonly double? sampleUt, seconds;
        private readonly double generation, consumption;
        internal CapturedPowerFlow(string reason, double? ut, double? interval = null, double generated = 0, double consumed = 0)
        { unavailableReason = reason; sampleUt = ut; seconds = interval; generation = generated; consumption = consumed; }
        internal ColonyPowerRate Derive()
        {
            if (unavailableReason != null || !seconds.HasValue)
                return new ColonyPowerRate { Status = "unavailable", Reason = unavailableReason, SampleUt = sampleUt };
            double produced = generation / seconds.Value, used = consumption / seconds.Value;
            return new ColonyPowerRate { Status = "partial", SampleUt = sampleUt, WindowSeconds = seconds,
                Reason = "Fulfilled EC resource requests only; direct resource changes and nearby transfers are excluded.",
                GenerationEcPerSecond = produced, ConsumptionEcPerSecond = used, NetEcPerSecond = produced - used };
        }
    }

    internal sealed class CapturedPowerEstimate
    {
        private readonly double[][] inputs, outputs;
        internal CapturedPowerEstimate(double[][] ownedInputs, double[][] ownedOutputs) { inputs = ownedInputs; outputs = ownedOutputs; }
        internal ColonyPowerEstimate Derive()
        {
            double generation = 0, consumption = 0; int modules = 0;
            for (int i = 0; i < inputs.Length; i++)
            {
                double input = inputs[i].Where(Positive).Sum(), output = outputs[i].Where(Positive).Sum();
                if (input == 0 && output == 0) continue;
                if (++modules > 160 || Double.IsInfinity(input) || Double.IsInfinity(output) || generation + output > 1e9 || consumption + input > 1e9) return null;
                generation += output; consumption += input;
            }
            return modules == 0 ? null : new ColonyPowerEstimate { Status = "nominal", ModuleCount = modules,
                GenerationEcPerSecond = generation, ConsumptionEcPerSecond = consumption,
                Reason = "Active converter recipe ratios only. Actual output can differ with efficiency, fuel, heat, storage, and transfers; other power modules are excluded." };
        }
        private static bool Positive(double value) { return value > 0 && !Double.IsNaN(value) && !Double.IsInfinity(value); }
    }
}
