using Expanse.Domain.Colonies;
namespace Expanse.Clock.Manager;
public partial class MainWindow
{
    private static ColonyManagementField[] ProductionFields(ColonyManagementSnapshot value)=>[
        new("ProductionMode","Supplies production strategy","importOnly","Local investment adds paid physical agriculture, finite raw-feed dependencies and native activation. The full Supplies import downside remains reserved.",[
            new("importOnly","Import Supplies — no new production investment"),new("compare","Compare cultivation with imports — no production commitment"),new("localInvestment","Invest in native cultivation and raw-feed hoppers")]),
        new("ProductionRecipe","Cultivation package",value.Production.Recipes.FirstOrDefault()?.Id ?? "",value.Production.Reason,
            value.Production.Recipes.Select(r=>new ColonyManagementChoice(r.Id,r.Name)).DefaultIfEmpty(new("","Installed cultivation package unavailable")).ToArray()),
        new("ProductionCount","Cultivation packages","1","One to four complete agriculture + raw-feed packages; WOLF dependencies are purchased together once."),
        new("ProductionHorizonDays","Comparison horizon (Kerbin days)","6","Installed nominal recipe rates are estimates. Actual native output, physical transfers, labor, power, fuel and recurring costs remain separate."),
        new("ProductionRegister","Manage the new production tanks","true",value.Production.Registry.Reason+" Creates two inventory endpoints per paid package. Declining leaves physical stock unmanaged and declines the operating buffers below.",[new("true","Create reviewed inventory endpoints"),new("false","Leave physical stock unmanaged")]),
        new("ProductionIntake","Collect native output into colony stock","true","Exact warehouse transfers collect physical Supplies and raw feeds. The paid startup contents retain their purchased origin; capacity alone creates no stock.",[new("true","Collect verified physical output"),new("false","Keep output in physical tanks")]),
        new("ProductionRefill","Refill native production inputs","true","Refill only the exact paid tanks. Warehouse input fills are bounded to 50%; Machinery and local Ranger fuel require an actual Engineer within workshop range. Turning this off also declines the recurring purchases below.",[new("true","Use reviewed owned inputs and service"),new("false","Decline automatic refill and purchases")]),
        new("ProductionFertilizerInitial","Extra Fertilizer buffer per package (units)","100","Optional additional owned reserve, separate from the paid startup tank contents. Zero declines this buffer."),
        new("ProductionFertilizerPoint","Fertilizer reorder point per package (units)","100","Finite supplier purchases are reviewed separately from native production."),
        new("ProductionFertilizerTarget","Fertilizer reorder target per package (units)","200","Full import downside remains reserved; no income from nominal output."),
        new("ProductionFertilizerEnabled","Recurring Fertilizer purchases","true","Exact point and target apply while automatic refill is enabled.",[new("true","Enable reviewed finite purchases"),new("false","Decline recurring purchases")]),
        new("ProductionMachineryInitial","Extra Machinery buffer per package (units)","100","Optional owned repair reserve. Zero declines this initial buffer; installed Machinery is serviced through the real worker/provider path."),
        new("ProductionMachineryPoint","Machinery reorder point per package (units)","100","Must agree with any separately reviewed startup Machinery policy."),
        new("ProductionMachineryTarget","Machinery reorder target per package (units)","100","Actual installed service targets 95% capacity; stock is debited only through conserved service escrow."),
        new("ProductionMachineryEnabled","Recurring Machinery purchases","true","No unreviewed replacement of an existing or declined startup policy.",[new("true","Enable reviewed finite purchases"),new("false","Decline recurring purchases")]),
        new("ProductionFuelInitial","Extra Ranger fuel buffer per package (units)","1","Optional owned Plutonium-238 reserve. The separate new package bills its own 20-unit startup fuel; this row buys an additional refill buffer."),
        new("ProductionFuelPoint","Ranger fuel reorder point per package (units)","1","A finite import/refill path preserves the exact local NO_FLOW tank and never restarts a stopped generator."),
        new("ProductionFuelTarget","Ranger fuel reorder target per package (units)","2","Actual output and fuel endurance remain conditional on native operation."),
        new("ProductionFuelEnabled","Recurring Ranger fuel purchases","true","Requires the actual qualified Engineer service path.",[new("true","Enable reviewed finite purchases"),new("false","Decline recurring purchases")]),
        new("ProductionOperatingDays","Input purchasing review cadence (Kerbin days)","1","At least one Kerbin day. Each enabled purchase retains the charter cash floor, spending limits, finite supplier stock and shared freight capacity.")];
    private static IEnumerable<string> ProductionReviewLines(ColonyPlanningQuote quote)
    {
        foreach(var item in quote.ProductionInvestments)
        {
            var recipe=item.Recipe;
            yield return "Native investment: "+recipe.Name+" · estimated "+item.NominalSuppliesPerDay.ToString("N2")+" Supplies/Kerbin day before actual native efficiency; "+(item.ReviewHorizonSeconds/ColonyLimits.KerbinDay).ToString("N1")+" day review horizon.";
            yield return "Native recipe inputs: "+string.Join(", ",recipe.Inputs.Select(r=>r.UnitsPerSecond.ToString("0.######")+" "+r.Resource+"/s"))+"; requires actual "+recipe.NativeExperienceEffect+" worker. "+recipe.EstimateBasis;
            if(item.Wolf.Id.Length>0)yield return "One shared WOLF purchase: "+item.Wolf.Funds.ToString("N0")+" funds; "+item.Wolf.LaborSeconds.ToString("N0")+" labor seconds; exact inputs "+string.Join(", ",item.Wolf.Demands.Select(d=>d.Points+" "+d.Resource+" points"))+". Purchased modules: "+string.Join(", ",item.Wolf.Modules.Select(m=>m.Count+" × "+m.Recipe.PartName))+". Abstract allocation does not credit resource stock.";
            if(item.Inventory is {} inventory)
            {
                yield return "Physical inventory: two paid-building endpoints; "+(inventory.AutomaticIntake?"verified output collection enabled":"output collection declined")+"; "+(inventory.AutomaticInputRefill?"exact input refill enabled":"input refill declined")+". Manual deletion remains an outage.";
                foreach(var r in inventory.Resources)yield return r.Resource+": additional owned buffer "+(r.InitialReserve/(double)ColonyLimits.Units).ToString("N2")+" units; "+(r.ReorderEnabled?"reorder at "+(r.ReorderPoint/(double)ColonyLimits.Units).ToString("N2")+" to "+(r.TargetAmount/(double)ColonyLimits.Units).ToString("N2")+" units every "+(r.CadenceSeconds/ColonyLimits.KerbinDay).ToString("N1")+" Kerbin days":"recurring purchases declined")+"; native fill target "+r.NativeTargetFraction.ToString("P0")+". Buffers are included in the itemized initial import bill.";
            }
            else yield return "Automatic production inventory operations are not authorized by this reviewed intent.";
            yield return item.Downside;
        }
    }
    private static IEnumerable<ColonyManagementRow> ProductionReviewRows(ColonyPlanningQuote quote)=>quote.ProductionInvestments.Select(i=>new ColonyManagementRow("plan-production:"+i.Id,i.Recipe.Name,"Reviewed local investment",i.NominalSuppliesPerDay.ToString("N2")+" nominal Supplies/day","Installed recipe estimate",
        string.Join(" ",ProductionReviewLines(new ColonyPlanningQuote{ProductionInvestments=[i]}))));
    private static ColonyManagementPresentation PresentProduction(ColonyManagementPresentation view,ColonyManagementSnapshot snapshot)
    {
        var rows=new List<ColonyManagementRow>();
        foreach(var plan in snapshot.State?.Plans.Where(p=>p.ColonyId==view.ColonyId) ?? [])foreach(var item in plan.Quote.ProductionInvestments)
        {
            var claim=plan.Production.Single(c=>c.Id==item.Id);var actual=snapshot.Production.Observations.FirstOrDefault(o=>o.PlanId==plan.Id&&o.InvestmentId==item.Id);
            rows.Add(new("production:"+plan.Id+":"+item.Id,item.Recipe.Name,actual is {Qualified:true} ? "Output receipt recorded" : claim.State,
                actual is {NativeSampleSeconds:>0} ? string.Join(", ",actual.NativeDeliveredOutputs.Select(o=>o.UnitsPerSecond.ToString("0.######")+" "+o.Resource+"/s measured")) : "Current rate not sampled",
                actual?.Provider ?? "Saved production intent",(actual?.Reason ?? claim.Reason)+" Estimate: "+item.NominalSuppliesPerDay.ToString("N2")+" Supplies/day. Stock credit requires a conserved physical transfer."));
            if(claim.Inventory is {} inventory)foreach(var endpoint in inventory.Endpoints)rows.Add(new("production-inventory:"+endpoint.SpecId,item.Inventory!.Endpoints.Single(s=>s.Id==endpoint.SpecId).Role=="cultivation"?"Cultivation inventory":"Raw-feed inventory",endpoint.State,"Paid selected tanks",snapshot.Production.Registry.Ready?"Selected-save registry":"Registry unavailable",endpoint.Reason+" Saved applied receipts do not certify current access; missing or changed registration remains an outage."));
        }
        return view with{Sections=view.Sections.Select(s=>s.Key=="construction" ? s with{Rows=s.Rows.Concat(rows).ToArray()} :
            s.Key=="production" ? s with{Rows=rows.Concat(s.Rows).ToArray()} : s).ToArray()};
    }
}
