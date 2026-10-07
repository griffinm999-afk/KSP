using System;
using System.Collections.Generic;
using System.Linq;

namespace Expanse.Domain.Colonies
{
    // An observed warehouse quantity is an acquisition option, not a forecast
    // manufacturing rate. WOLF capacity points never become physical commodity.
    public sealed class ColonyMakeImportComparison
    {
        public string Resource { get; set; } = "";
        public long ReviewedImportAmount { get; set; }
        public long ReviewedMaximumImportFunds { get; set; }
        public long AccessibleLocalAmount { get; set; }
        public long LocalTransferFunds { get; set; }
        public double SupplierTravelSeconds { get; set; }
        public double ConservativeImportSeconds { get; set; }
        public double ImportMassTonnes { get; set; }
        public double ImportVolumeLiters { get; set; }
        public bool ProductionInvestmentSupported { get; set; }
        public string UnsupportedReason { get; set; } = "";
        public string Rationale { get; set; } = "";
    }
    public static partial class ColonyEngine
    {
        static void QuoteMakeImportComparisons(ColonyPlanningQuote q,ColonyState state,ColonyRecord colony,ColonyEnvironment env)
        {
            var alternative=q.FoundingIntent?.Production?.Mode=="compare" ? QuoteProductionAlternative(state,colony.Id,env,q.FoundingIntent) : null;
            var investments=alternative?.ProductionInvestments ?? q.ProductionInvestments;
            // Read-only alternatives do not reduce the immutable purchase maximum.
            // Actual transfer receipts alone can release a saved import commitment.
            foreach(var group in q.Imports.GroupBy(i=>i.Resource).OrderBy(g=>g.Key,StringComparer.Ordinal))
            {
                var stock=colony.Stock.SingleOrDefault(s=>s.Resource==group.Key)??env.EconomyPolicies.Where(p=>p.Id==q.StagingPolicyId).SelectMany(p=>p.Stores).Single(s=>s.Resource==group.Key);
                long local=env.Planning.LocalStocks.Where(s=>s.Resource==group.Key&&AccessibleLocal(s,colony,env)&&
                    !state.PhysicalTransfers.Any(o=>PhysicalPending(o)&&o.PartId==s.PartId&&o.Resource==s.Resource)&&
                    !state.ServiceOperations.Any(o=>(o.State=="reserved"||o.State=="held")&&o.PartId==s.PartId&&o.DestinationResource==s.Resource))
                    .Sum(s=>Math.Max(0,s.Amount-s.PhysicalReserve));
                long amount=group.Sum(i=>i.Amount);
                q.MakeImportComparisons.Add(new ColonyMakeImportComparison{Resource=group.Key,ReviewedImportAmount=amount,
                    ReviewedMaximumImportFunds=group.Sum(i=>i.Funds),AccessibleLocalAmount=Math.Min(amount,local),LocalTransferFunds=0,
                    SupplierTravelSeconds=group.Max(i=>i.TravelSeconds),ConservativeImportSeconds=(q.Imports.Sum(i=>i.TravelSeconds)+
                        state.Plans.Where(PlanningActive).SelectMany(p=>p.Imports.Where(i=>i.ShipmentId.Length==0&&PlanningImportAmount(i)>0)).Sum(i=>i.TravelSeconds)+
                        state.Shipments.Where(s=>s.Kind=="import"&&(s.State=="reserved"||s.State=="inTransit")).Sum(s=>s.State=="reserved"?s.TravelSeconds:Math.Max(0,s.ArrivalUt-env.Ut)))*env.Planning.ImportDelayFactor,
                    ImportMassTonnes=(double)amount/ColonyLimits.Units*stock.UnitMassMicroTonnes/ColonyLimits.Units,
                    ImportVolumeLiters=(double)amount/ColonyLimits.Units*stock.UnitVolumeMilliLiters/1000,
                    ProductionInvestmentSupported=investments.Any(i=>i.Recipe.Outputs.Any(o=>o.Resource==group.Key&&o.UnitsPerSecond>0)),
                    UnsupportedReason=investments.Any(i=>i.Recipe.Outputs.Any(o=>o.Resource==group.Key&&o.UnitsPerSecond>0))
                        ? "Costed native investment available; output rate is an installed nominal estimate until physical delivery. "+(alternative?.Blockers.Count>0 ? string.Join("; ",alternative.Blockers) : "Actual native power/input/specialist activation and physical receipts remain required.")
                        : "No costed native investment was selected for this commodity. WOLF abstract points do not establish physical stock output or payback.",
                    Rationale=(local>0?"Observed accessible stock may replace paid imports after exact transfer readback; full import cost remains reserved.":"No qualified accessible warehouse stock above native buffers; paid imports cover the shortage.")+" Transit scenario serializes all current quoted/committed loads with delayed imports; uncertain funding/provider holds have no finite completion guarantee."});
            }
            q.MakeOrImport=investments.Count>0
                ? (alternative!=null ? "Comparison only: this approval commits imports. Alternative full startup bill "+alternative.TotalFunds+" funds (+"+(alternative.TotalFunds-q.TotalFunds)+"); "+alternative.LaborSeconds+" construction labor seconds. Choose local investment and re-review to commit hardware. " : "Paid cultivation/feed buildings and one compound WOLF dependency purchase included. ")+
                    "Installed estimate "+investments.Sum(i=>i.NominalSuppliesPerDay).ToString("0.######",System.Globalization.CultureInfo.InvariantCulture)+" Supplies/day before native efficiency. Actual Scientist, feeds, Fertilizer, fuel/power and service required. Full import downside remains reserved; stock needs physical receipts."
                : "Actual local stock and finite paid imports compared. No new production hardware is included. Explicit local investment requires installed packages and paid dependencies; estimated output never reduces the import downside. Native MKS/WOLF owns production.";
            if(q.MakeOrImport.Length>512)q.MakeOrImport=q.MakeOrImport.Substring(0,512);
            foreach(var comparison in q.MakeImportComparisons)if(comparison.UnsupportedReason.Length>512)comparison.UnsupportedReason=comparison.UnsupportedReason.Substring(0,512);
        }
    }
}
