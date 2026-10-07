using System.Globalization;
using Expanse.Domain.Colonies;
namespace Expanse.Clock.Manager;
public partial class MainWindow
{
    private ColonyPlanningQuote? managementPlanningQuote;
    private static ColonyEnvironment PlanningEnvironment(ColonyManagementSnapshot value)=>new()
    {
        WorldId=value.State!.WorldId,ContextKey=value.ContextKey,Ut=Math.Max(value.State.SimulatedUt,value.ObservedUt),AvailableFunds=value.AvailableFunds ?? 0,
        Templates=value.Templates,UnlockedTech=value.UnlockedTech,DevelopmentMode=value.DevelopmentMode,Planning=value.Planning,
        People=value.People,Services=value.Services,Support=value.Support,EconomyPolicies=value.EconomyPolicies,Wolf=value.Wolf,BodyRadiiMeters=value.BodyRadiiMeters,Production=value.Production
    };
    private static string PlanningReview(ColonyPlanningQuote quote)
    {
        string Units(long amount)=>(amount/(decimal)ColonyLimits.Units).ToString("N6",CultureInfo.CurrentCulture).TrimEnd('0').TrimEnd(CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator.ToCharArray());
        var lines=new List<string>{(quote.Kind=="founding" ? "COLONY STARTUP" : "COLONY EXPANSION")+" · "+quote.TotalFunds.ToString("N0")+" funds total commitments",quote.Rationale};
        lines.AddRange(ProductionReviewLines(quote));
        lines.AddRange(quote.MakeImportComparisons.Select(c=>c.Resource+" alternatives: "+c.Rationale+" "+c.UnsupportedReason));
        if(quote.Blockers.Count>0)lines.Add("Needs attention: "+string.Join("; ",quote.Blockers));
        if(quote.ExistingAssets.Count>0)lines.Add("Preserved assets: "+string.Join("; ",quote.ExistingAssets));
        lines.AddRange(quote.Buildings.Select(b=>b.Role+": "+b.Name+" · "+b.Funds.ToString("N0")+" funds · "+b.Homes+" homes · "+b.LaborSeconds.ToString("N0")+" labor seconds · "+(b.PlotId.Length==0 ? "plot survey required" : "surveyed plot")+". Inputs: "+string.Join(", ",b.Materials.Select(m=>Units(m.Amount)+" "+m.Resource))));
        lines.AddRange(quote.Imports.Select(i=>"Import "+Units(i.Amount)+" "+i.Resource+" · "+i.Funds.ToString("N0")+" funds including "+i.FreightFunds.ToString("N0")+" freight · "+i.TravelSeconds.ToString("N0")+" game seconds lead time."));
        lines.AddRange(quote.BootstrapWorkers.Select(w=>"Approved worker shift: "+w.Name+" · "+w.Trait+" from "+w.SourceCabinName+" → "+(quote.Buildings.FirstOrDefault(b=>b.Id==w.BuildingId)?.Name ?? "reviewed workplace")+". Automatically relocates this existing ordinary visitor into a verified nearby work cabin, at most "+w.MaximumDistanceMeters.ToString("N0")+" metres; actual destination seat is resolved after construction. Visitor/resident status and certified home are preserved. Protected astronauts and other crew are not substituted."));
        string Home(ColonyPlanningResident r)=>r.HomeBuildingId.Length>0 ? (quote.Buildings.First(b=>b.Id==r.HomeBuildingId).Name+" · reviewed room "+(r.HomeSlot+1)) : "Reviewed existing certified home";
        lines.AddRange(quote.Residents.Select(r=>r.Name+" · "+r.Trait+" → "+Home(r)+". "+(r.Kind=="existing" ? "Designate existing visitor from "+r.SourceCabinName+"; preserve actual physical crew cabin; no fare." : "Paid new arrival: "+r.Fare.ToString("N0")+" funds; "+r.TravelSeconds.ToString("N0")+" game seconds. Exact named roster, route and room readback required.")+" No substitute person or building is authorized."));
        if(quote.StartupPolicies is { } p)
        {
            lines.AddRange(p.Reorders.Select(r=>"Reviewed recurring "+r.Resource+": "+(r.Enabled ? "enabled" : "disabled")+"; reorder at "+Units(r.ReorderPoint)+" units; target "+Units(r.TargetAmount)+" units; cadence "+r.CadenceSeconds.ToString("N0")+" game seconds. Paid finite supplier, freight, shared funds and cash floor apply."));
            lines.Add("Reviewed installed-tank service: "+(p.ServiceEnabled ? "enabled" : "disabled")+"; "+(p.ServiceTargetFillFraction*100).ToString("N0")+"% target; cadence "+p.ServiceCadenceSeconds.ToString("N0")+" game seconds. Real stock, qualified physical workers and conserved provider receipts required. Initial owned-stock targets: "+string.Join(", ",p.StockTargets.Select(m=>Units(m.Amount)+" "+m.Resource))+".");
            lines.Add("Reviewed local procurement: "+(p.LocalProcurementEnabled ? "enabled" : "disabled")+"; "+p.LocalProcurementCadenceSeconds.ToString("N0")+" s minimum review; "+Units(p.LocalProcurementMaximumMicroUnits)+" units maximum per transfer; native inputs at most "+(p.NativeInputBufferFraction*100).ToString("N0")+"% full. Incoming capacity and reservations remain separate from available stock.");
        }
        lines.Add("Startup support reserve: "+Units(quote.StartupSupportReserve)+" Supplies; staging setup "+quote.StagingFunds.ToString("N0")+" funds. "+quote.MakeOrImport);
        var f=quote.Forecast;lines.Add("Forecast: "+f.Cash.ToString("N0")+" account funds; "+f.CommittedCash.ToString("N0")+" committed; "+f.AvailableCash.ToString("N0")+" available; "+f.Receivables.ToString("N0")+" receivables. Downside "+f.DownsideCash.ToString("N0")+" funds and "+f.DownsideSupplyDays.ToString("N1")+" support days. "+f.Assumptions);
        if(f.Shortages.Count>0)lines.Add("Downside shortages: "+string.Join("; ",f.Shortages));
        return string.Join(Environment.NewLine+Environment.NewLine,lines);
    }
    private static ColonyManagementPresentation PresentPlanningReview(ColonyManagementPresentation view,ColonyPlanningQuote quote,string? targetId=null)
    {
        var rows=new List<ColonyManagementRow>();
        rows.AddRange(ProductionReviewRows(quote));
        rows.AddRange(quote.MakeImportComparisons.Select(c=>new ColonyManagementRow("plan-option:"+c.Resource,c.Resource+" acquisition comparison",c.ProductionInvestmentSupported?"Costed native investment option":"Finite imports / actual stock",c.ReviewedMaximumImportFunds.ToString("N0")+" import funds","Reviewed acquisition alternatives",c.Rationale+" "+c.UnsupportedReason)));
        rows.AddRange(quote.Blockers.Select((b,i)=>new ColonyManagementRow("plan-blocker:"+i,"Startup prerequisite","Needs attention","—","Current planning authority",b)));
        rows.AddRange(quote.ExistingAssets.Select((a,i)=>new ColonyManagementRow("plan-asset:"+i,a,"Preserved","Existing hardware","Current asset witness","This asset remains owned by its actual physical producer; no free stock or work bonus is inferred.")));
        rows.AddRange(quote.Buildings.Select(b=>new ColonyManagementRow("plan-building:"+b.Id,b.Name,b.PlotId.Length==0 ? "Survey required" : "Planned · "+b.Role,b.Funds.ToString("N0")+" funds","Reviewed package",b.Homes+" homes; "+b.LaborSeconds.ToString("N0")+" labor seconds. Inputs: "+string.Join(", ",b.Materials.Select(m=>(m.Amount/(decimal)ColonyLimits.Units).ToString("0.######")+" "+m.Resource))+". Physical placement, staffing, utilities and commissioning remain required.")));
        rows.AddRange(quote.Imports.Select(i=>new ColonyManagementRow("plan-import:"+i.Id,i.Resource,"Planned paid import",(i.Amount/(decimal)ColonyLimits.Units).ToString("N2")+" units · "+i.Funds.ToString("N0")+" funds","Finite supplier quote",i.TravelSeconds.ToString("N0")+" game seconds lead time; freight "+i.FreightFunds.ToString("N0")+" funds. Incoming commitments are not available stock.")));
        rows.AddRange(quote.BootstrapWorkers.Select(w=>new ColonyManagementRow("plan-worker:"+w.Id,w.Name+" · "+w.Trait,"Approved automatic worker shift",w.MaximumDistanceMeters.ToString("N0")+" m maximum","Current ordinary visiting crew",w.SourceCabinName+" → "+(quote.Buildings.FirstOrDefault(b=>b.Id==w.BuildingId)?.Name ?? "reviewed workplace")+". "+w.DestinationSeatRole+" is revalidated after physical construction. Preserves visitor/resident status and home; does not substitute mission crew. Source vessel "+w.SourceVesselId+" / part "+w.SourcePartId+"; exact actual cabin readback is required.")));
        rows.AddRange(quote.Residents.Select(r=>new ColonyManagementRow("plan-resident:"+r.Id,r.Name+" · "+r.Trait,r.Kind=="existing" ? "Reviewed resident designation" : "Reviewed paid arrival",r.Fare.ToString("N0")+" funds",r.Kind=="existing" ? r.SourceCabinName : "Named KSP roster / paid service",
            "Home: "+(r.HomeBuildingId.Length>0 ? quote.Buildings.First(b=>b.Id==r.HomeBuildingId).Name+" · room "+(r.HomeSlot+1) : "existing certified home")+". "+(r.Kind=="existing" ? "Keeps actual source cabin membership; this changes residency status only." : r.TravelSeconds.ToString("N0")+" game seconds on the exact reviewed route.")+" No alternate person or building will be selected.")));
        if(quote.StartupPolicies is { } startup)
        {
            rows.AddRange(startup.Reorders.Select(r=>new ColonyManagementRow("plan-policy:reorder:"+r.Resource,r.Resource+" replenishment",r.Enabled ? "Reviewed automatic policy" : "Explicitly disabled",(r.TargetAmount/(decimal)ColonyLimits.Units).ToString("N2")+" target units","Finite paid supplier",
                "Reorder point "+(r.ReorderPoint/(decimal)ColonyLimits.Units).ToString("N2")+" units; cadence "+r.CadenceSeconds.ToString("N0")+" game seconds; support covers "+startup.SupportedPeople+" actual/promised people. Shared cash floor and freight capacity remain enforced.")));
            rows.Add(new("plan-policy:service","Installed tank maintenance",startup.ServiceEnabled ? "Reviewed recurring service" : "Explicitly disabled",(startup.ServiceTargetFillFraction*100).ToString("N0")+"% target","Owned stock + actual Engineer",
                "Cadence "+startup.ServiceCadenceSeconds.ToString("N0")+" game seconds. Exact initial buffer targets: "+string.Join(", ",startup.StockTargets.Select(m=>(m.Amount/(decimal)ColonyLimits.Units).ToString("N2")+" "+m.Resource))+". This buys no free stock or remote crew bonus."));
        }
        string page=quote.Kind=="founding" ? "founding" : "overview";
        return view with {Sections=view.Sections.Select(s=>s.Key==page ? s with {Rows=rows.Concat(targetId is null ? s.Rows.Take(1) : s.Rows.Where(r=>r.Id==targetId)).ToArray(),Actions=s.Actions.Select(a=>a.Kind==view.QuoteKind ? a with {Available=a.Available && quote.CanApprove,Reason=quote.CanApprove ? "Approve the complete reviewed scope and exact current commitments." : string.Join(" ",quote.Blockers)} : a).ToArray()} : s).ToArray()};
    }
}
