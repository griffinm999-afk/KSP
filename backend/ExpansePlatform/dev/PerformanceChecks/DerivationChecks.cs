using System;
using System.Linq;
using System.Threading.Tasks;
using WB = Expanse.WorldBridge;

internal static class DerivationChecks
{
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    internal static void Run()
    {
        var id = Guid.NewGuid(); var ids = new[] { id };
        var raw = new WB.ColonySnapshot { Status = "observed", ObservedUt = 100 };
        var row = new WB.ColonyVessel { VesselId = id.ToString("D"), Longitude = 363, Crew = 1, CrewRosterComplete = true, PhysicalCrewCapacity = 2 };
        row.CrewRoster.Add(new WB.ColonyCrewMember { Name = "Val", Profession = "Pilot" });
        row.Converters.Add(new WB.ColonyConverter { Inputs = Enumerable.Repeat("Water", 25).Concat(new[] { "Fertilizer" }).ToArray(), Outputs = new[] { "Supplies", "Supplies" } });
        row.Tanks.Add(new WB.ColonyTank { Resource = "Water", Role = "storage", Amount = 2, Capacity = 10, WarehousePresent = true, LocalWarehouseOn = true, FlowEnabled = true });
        row.Tanks.Add(new WB.ColonyTank { Resource = "Water", Role = "storage", Amount = 3, Capacity = 20, WarehousePresent = true, LocalWarehouseOn = true, FlowEnabled = true });
        row.Tanks.Add(new WB.ColonyTank { Resource = "Water", Role = "storage", Amount = 4, Capacity = 30, WarehousePresent = true, LocalWarehouseOn = false, FlowEnabled = true });
        string prefix = new string('R', 50);
        row.Tanks.Add(new WB.ColonyTank { Resource = prefix + "A", Role = "other", Amount = 7, Capacity = 9 });
        row.Tanks.Add(new WB.ColonyTank { Resource = prefix + "B", Role = "other", Amount = 8, Capacity = 9 });
        row.Production = new WB.ColonyProductionTelemetry();
        row.Production.Modules.Add(new WB.ColonyProductionModuleTelemetry());
        raw.Vessels.Add(row);
        var tracker = new WB.VesselCensusTracker();
        var capture = new WB.ColonyCapture(raw, "epoch", "FLIGHT", 1, ids, ids);
        int captureThread = Environment.CurrentManagedThreadId;
        var result = Task.Run(() => {
            Check(Environment.CurrentManagedThreadId != captureThread, "Derivation must execute on a different thread");
            return capture.Derive(tracker);
        }).GetAwaiter().GetResult();
        Check(result.Vessels[0].Converters[0].Inputs.SequenceEqual(new[] { "Water", "Fertilizer" }), "Deduplication occurs before distinct-resource cap");
        Check(result.Vessels[0].Tanks.Count == 4, "Grouping retains accessibility and full resource identity");
        Check(result.Vessels[0].Tanks.Single(t => t.Resource == "Water" && t.LocalWarehouseOn == true).Amount == 5, "Tank sums");
        Check(result.Vessels[0].Tanks.Where(t => t.Resource == prefix).Select(t => t.Amount).SequenceEqual(new[] { 7d, 8d }), "Equal display names retain stable independent quantities");
        Check(row.Production.Modules.Count == 1 && row.Tanks.Count == 5 && row.Converters[0].Inputs.Length == 26, "Derivation never mutates capture");
        Check(result.Vessels[0].CrewRosterComplete && result.Vessels[0].PhysicalCrewCapacity == 2, "Roster/capacity preserved");
        Check(result.VesselCensus.Status == "unavailable" && result.VesselCensus.ObservationSequence == 1, "First census attempt");
        Check(result.Vessels[0].Production.Modules.Count == 1, "Capture must survive until actual wire budgeting");
        var skipped = new WB.ColonyCapture(raw, "epoch", "FLIGHT", 3, ids, ids).Derive(tracker);
        Check(skipped.VesselCensus.Status == "unavailable" && skipped.VesselCensus.ObservationSequence == 3, "Skipped invalid attempt breaks stability");
        var stable = new WB.ColonyCapture(raw, "epoch", "FLIGHT", 4, ids, ids).Derive(tracker);
        Check(stable.VesselCensus.Status == "complete", "Consecutive independent captures establish completeness");
        tracker.Invalidate();
        var transition = new WB.ColonyCapture(raw, "epoch", "FLIGHT", 5, ids, ids).Derive(tracker);
        Check(transition.VesselCensus.Status == "unavailable" && transition.VesselCensus.ObservationSequence == 5, "Hidden scene transition invalidates reliability without resetting sequence");
        var power = new WB.CapturedPowerEstimate(new[] { new[] { 2d, double.NaN, -1d }, new[] { 3d } }, new[] { new[] { 10d }, new double[0] }).Derive();
        Check(power.ConsumptionEcPerSecond == 5 && power.GenerationEcPerSecond == 10 && power.ModuleCount == 2, "Power estimates preserve module boundaries and finite positive filtering");
        Check(new WB.CapturedPowerEstimate(new[] { new[] { 1e9, 1d } }, new[] { new double[0] }).Derive() == null, "Power overflow rejects estimate");
        Check(new WB.CapturedPowerEstimate(Enumerable.Repeat(new[] { 1d }, 161).ToArray(), Enumerable.Repeat(new double[0], 161).ToArray()).Derive() == null, "Power contributor bound");
        Check(result.Vessels[0].Longitude == 3 && row.Longitude == 363, "Longitude normalized only on worker result");
        var flow = new WB.CapturedPowerFlow(null, 100, 3, 30, 12).Derive();
        Check(flow.GenerationEcPerSecond == 10 && flow.ConsumptionEcPerSecond == 4 && flow.NetEcPerSecond == 6, "Observed power flow arithmetic");
        Check(new WB.CapturedPowerFlow("unloaded", 100).Derive().GenerationEcPerSecond == null, "Unavailable power never invents zero flow");
        var timing = new WB.ObservationTiming();
        Parallel.For(0, 1000, _ => timing.Record(1));
        Check(timing.Drain("test").Contains("count=1000"), "Concurrent timing counter count");
        Check(timing.Drain("test").Contains("count=0"), "Timing interval drain");
        timing.Record(System.Diagnostics.Stopwatch.Frequency);
        timing.Record(2 * System.Diagnostics.Stopwatch.Frequency);
        var measured = timing.Drain("measured");
        Check(measured.Contains("count=2") && measured.Contains("totalMs=3000.000") && measured.Contains("maxMs=2000.000"), "Timing totals and maximum use real stopwatch frequency");
        AssertPrimitiveGraph(typeof(WB.ColonyCapture), new System.Collections.Generic.HashSet<Type>());
        Console.WriteLine("Worker derivation, ownership, tank identity, census gaps/transitions, power bounds and timing checks passed");
    }
    static void AssertPrimitiveGraph(Type type, System.Collections.Generic.HashSet<Type> seen)
    {
        if (!seen.Add(type) || type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(Guid)) return;
        if (type.IsArray) { AssertPrimitiveGraph(type.GetElementType()!,seen);return; }
        if (type.IsGenericType) {
            var definition = type.GetGenericTypeDefinition();
            Check(definition == typeof(System.Collections.Generic.List<>) || definition == typeof(Nullable<>), "Unexpected capture container: " + type.FullName);
            foreach(var argument in type.GetGenericArguments()) AssertPrimitiveGraph(argument,seen);return;
        }
        Check(type.Namespace == "Expanse.WorldBridge", "Capture graph contains an external/live object type: " + type.FullName);
        foreach(var field in type.GetFields(System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic))
            AssertPrimitiveGraph(field.FieldType,seen);
    }
}
