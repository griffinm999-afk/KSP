using System.Text.Json;

namespace Expanse.Clock.Core;

public static partial class ClockProtocol
{
    const string ProductionOmitted = "Production rows omitted for the bounded frame; totals are incomplete.";
    // Allocate whole module observations; never trim vectors, merge bays, or
    // replace unknown/dropped observations with zero. No mutation of Host state.
    static ColonySnapshot FitProductionView(ClockView view)
    {
        var source = view.Colony!;
        long turn = source.VesselCensus?.ObservationSequence ?? (long)(Math.Abs(source.ObservedUt ?? 0) / 2);
        var vessels = source.Vessels.Select(v => v with { Production = v.Production is null ? null :
            new ColonyProductionTelemetry(v.Production.Modules.Length == 0 ? v.Production.Status : "truncated",
                v.Production.Modules.Length == 0 ? v.Production.Reason : ProductionOmitted,
                v.Production.ObservedUt, Array.Empty<ColonyProductionModuleTelemetry>(),
                v.Production.Modules.Length == 0 ? v.Production.InventoryStatus : "partial",
                v.Production.BudgetOmittedModuleCount+v.Production.Modules.Length,
                v.Production.Modules.Length == 0 ? v.Production.BudgetSelectionSequence : turn) }).ToArray();
        var result = source with { Vessels = vessels };
        int remaining = MaxFrameBytes - JsonSerializer.SerializeToUtf8Bytes(view with { Colony = result }, JsonOptions).Length;
        var candidates = source.Vessels.Select(v => v.Production?.Modules.OrderBy(ProductionPriority).ToArray()
            ?? Array.Empty<ColonyProductionModuleTelemetry>()).ToArray();
        var buckets = candidates.Select(rows => Enumerable.Range(0, 4).Select(p => rows.Where(r => ProductionPriority(r) == p).ToArray()).ToArray()).ToArray();
        // Reserve enough metadata for restoring a fully included inventory.
        for (int i = 0; i < vessels.Length; i++)
            if (source.Vessels[i].Production is {} p && vessels[i].Production is {} empty)
                remaining -= Math.Max(0, JsonSerializer.SerializeToUtf8Bytes(p with { Modules = Array.Empty<ColonyProductionModuleTelemetry>() }, JsonOptions).Length
                    - JsonSerializer.SerializeToUtf8Bytes(empty, JsonOptions).Length);
        var selected = vessels.Select(_ => new List<ColonyProductionModuleTelemetry>()).ToArray();
        // Rotate ties by capture sequence so a persistent crowded frame cannot
        // permanently starve the same tail vessels. Priority never uses rates.
        int start = vessels.Length == 0 ? 0 : (int)(turn % vessels.Length);
        for (int priority = 0; priority < 4; priority++)
            for (int round = 0; round < 32; round++)
                for (int offset = 0; offset < vessels.Length; offset++)
                {
                    int i = (start + offset) % vessels.Length;
                    var rows = buckets[i][priority];
                    if (round >= rows.Length) continue;
                    var row = rows[round];
                    int cost = JsonSerializer.SerializeToUtf8Bytes(row, JsonOptions).Length + (selected[i].Count == 0 ? 0 : 1);
                    if (cost > remaining) continue;
                    selected[i].Add(row); remaining -= cost;
                }
        for (int i = 0; i < vessels.Length; i++)
            if (vessels[i].Production is {} p)
                vessels[i] = vessels[i] with { Production = selected[i].Count == candidates[i].Length
                    ? source.Vessels[i].Production : p with { Modules = selected[i].ToArray(),
                        BudgetOmittedModuleCount=source.Vessels[i].Production!.BudgetOmittedModuleCount+candidates[i].Length-selected[i].Count,
                        BudgetSelectionSequence=turn } };
        return result;
    }
    static int ProductionPriority(ColonyProductionModuleTelemetry row) =>
        row.Achieved is not null || row.Background is not null ? 0 : row.Prepared is not null ? 1 : row.Activated == true ? 2 : 3;
}
