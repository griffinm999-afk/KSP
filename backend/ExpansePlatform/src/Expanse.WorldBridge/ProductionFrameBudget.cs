using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Expanse.WorldBridge
{
    public sealed partial class WorldBridgeAddon
    {
        private const string ProductionOmitted = "Production rows omitted for the bounded frame; totals are incomplete.";
        private static string FitProductionColony(string prefix, ColonySnapshot source, string wolfJson, bool includeCrew)
        {
            var result = new ColonySnapshot { Status = source.Status, Reason = source.Reason, ObservedUt = source.ObservedUt,
                Wolf = source.Wolf, VesselCensus = source.VesselCensus };
            long turn = source.VesselCensus == null ? (long)(Math.Abs(source.ObservedUt ?? 0) / 2) : source.VesselCensus.ObservationSequence;
            foreach (var v in source.Vessels)
            {
                var copy = new ColonyVessel { VesselId = v.VesselId, Name = v.Name, Body = v.Body, Biome = v.Biome,
                    Latitude = v.Latitude, Longitude = v.Longitude, ObservationBasis = v.ObservationBasis,
                    Crew = v.Crew, CrewRosterComplete = v.CrewRosterComplete, PhysicalCrewCapacity = v.PhysicalCrewCapacity,
                    Power = v.Power, PowerEstimate = v.PowerEstimate };
                copy.Tanks.AddRange(v.Tanks); copy.Converters.AddRange(v.Converters); copy.CrewRoster.AddRange(v.CrewRoster);
                if (v.Production != null)
                {
                    copy.Production = EmptyProduction(v.Production, v.Production.Modules.Count != 0);
                    if(v.Production.Modules.Count != 0)copy.Production.BudgetSelectionSequence=turn;
                }
                result.Vessels.Add(copy);
            }
            string empty = prefix + ColonyJson(result, wolfJson, includeCrew) + "}";
            int remaining = ClockMaxFrameBytes - Encoding.UTF8.GetByteCount(empty);
            if (remaining < 0) return null;
            var candidates = source.Vessels.Select(v => v.Production == null ? new ColonyProductionModuleTelemetry[0] :
                v.Production.Modules.OrderBy(ProductionPriority).ToArray()).ToArray();
            var buckets = candidates.Select(rows => Enumerable.Range(0, 4).Select(p => rows.Where(r => ProductionPriority(r) == p).ToArray()).ToArray()).ToArray();
            var rowBytes = new Dictionary<ColonyProductionModuleTelemetry, int>();
            var single = new ColonyProductionTelemetry();
            int singleBase = Encoding.UTF8.GetByteCount(ProductionTelemetryJson(single));
            foreach (var row in candidates.SelectMany(rows => rows))
            {
                single.Modules.Clear(); single.Modules.Add(row);
                rowBytes[row] = Encoding.UTF8.GetByteCount(ProductionTelemetryJson(single)) - singleBase;
            }
            for (int i = 0; i < result.Vessels.Count; i++)
                if (source.Vessels[i].Production != null)
                    remaining -= Math.Max(0, Encoding.UTF8.GetByteCount(ProductionTelemetryJson(EmptyProduction(source.Vessels[i].Production, false)))
                        - Encoding.UTF8.GetByteCount(ProductionTelemetryJson(result.Vessels[i].Production)));
            int count = result.Vessels.Count;
            int start = count == 0 ? 0 : (int)(turn % count);
            for (int priority = 0; priority < 4; priority++)
                for (int round = 0; round < 32; round++)
                    for (int offset = 0; offset < count; offset++)
                    {
                        int i = (start + offset) % count;
                        var rows = buckets[i][priority];
                        if (round >= rows.Length) continue;
                        var production = result.Vessels[i].Production;
                        int cost = rowBytes[rows[round]] + (production.Modules.Count == 0 ? 0 : 1);
                        if (cost <= remaining) { production.Modules.Add(rows[round]); remaining -= cost; }
                    }
            for (int i = 0; i < count; i++)
                if (result.Vessels[i].Production != null)
                {
                    if(result.Vessels[i].Production.Modules.Count == candidates[i].Length)
                        result.Vessels[i].Production = source.Vessels[i].Production;
                    else result.Vessels[i].Production.BudgetOmittedModuleCount=source.Vessels[i].Production.BudgetOmittedModuleCount+
                        candidates[i].Length-result.Vessels[i].Production.Modules.Count;
                }
            string frame = prefix + ColonyJson(result, wolfJson, includeCrew) + "}";
            return Encoding.UTF8.GetByteCount(frame) <= ClockMaxFrameBytes ? frame : null;
        }
        private static ColonyProductionTelemetry EmptyProduction(ColonyProductionTelemetry source, bool omitted)
        {
            return new ColonyProductionTelemetry { Status = omitted ? "truncated" : source.Status,
                Reason = omitted ? ProductionOmitted : source.Reason, InventoryStatus = omitted ? "partial" : source.InventoryStatus,
                ObservedUt = source.ObservedUt, BudgetOmittedModuleCount=source.BudgetOmittedModuleCount+(omitted?source.Modules.Count:0),
                BudgetSelectionSequence=source.BudgetSelectionSequence };
        }
        private static int ProductionPriority(ColonyProductionModuleTelemetry row)
        { return row.Achieved != null || row.Background != null ? 0 : row.Prepared != null ? 1 : row.Activated == true ? 2 : 3; }
        private static ColonyProductionTelemetry OmitAllProduction(ColonyProductionTelemetry production, ColonySnapshot colony)
        {
            var empty=EmptyProduction(production,production.Modules.Count>0);
            if(production.Modules.Count>0)empty.BudgetSelectionSequence=colony.VesselCensus==null?
                (long)(Math.Abs(colony.ObservedUt??0)/2):colony.VesselCensus.ObservationSequence;
            return empty;
        }
    }
}
