using Expanse.Domain.Colonies;

namespace Expanse.Colony.Acceptance;

// These checks consume actual recorded snapshots. They never simulate effects,
// alter a save, manufacture witnesses, or infer success from a queued command.
public static class AcceptanceChecks
{
    public static ColonyEnvironment Environment(ColonyManagementSnapshot s) => new()
    {
        WorldId=s.State?.WorldId??"",ContextKey=s.ContextKey,Ut=s.ObservedUt,AvailableFunds=s.AvailableFunds??0,
        Templates=s.Templates,AdoptableFacilities=s.AdoptableFacilities,FacilitySites=s.FacilitySites,
        BodyRadiiMeters=s.BodyRadiiMeters,People=s.People,Services=s.Services,EconomyPolicies=s.EconomyPolicies,
        Wolf=s.Wolf,UnlockedTech=s.UnlockedTech,DevelopmentMode=s.DevelopmentMode,Support=s.Support,Planning=s.Planning,Production=s.Production
    };
    static ColonyRecord Colony(ColonyManagementSnapshot s,string id) => s.State?.Colonies.Single(c=>c.Id==id)??throw new InvalidDataException("Snapshot lacks authoritative colony state.");
    static void Need(bool condition,string reason) { if(!condition)throw new InvalidDataException(reason); }
    static void SameWorld(ColonyManagementSnapshot before,ColonyManagementSnapshot after)
    { Need(before.State!=null&&after.State!=null&&before.State.WorldId==after.State.WorldId,"Both snapshots must belong to the same saved world."); }
    static void ExternalClear(ColonyManagementSnapshot s)
    { Need(!s.State!.Effects.Any(e=>e.State=="held"||e.State=="applying"),"An external effect is still uncertain; acceptance cannot pass."); }
    // Optional acceptance scope only: this does not change the product quote or
    // grant any missing catalog, capability or provider authority.
    public static List<string> CompleteStartupGaps(ColonyPlanningQuote quote,ColonyRecord? colony=null)
    {
        var gaps=new List<string>();var intent=quote.FoundingIntent;
        if(intent==null)
        {gaps.Add("No complete resolved founding contract is available; resolve the current domain blockers without inserting provider evidence.");return gaps;}
        // The domain resolves FillPopulationTarget into exact names and sets it
        // false so approval cannot substitute another person. Count the frozen
        // bill plus already committed residents, not the mutable auto-fill flag.
        var committed=(colony?.Residents??[]).Where(r=>r.Status!="missing").Select(r=>r.RosterId).ToArray();
        var named=quote.Residents.Select(r=>r.RosterId).ToArray();var population=committed.Concat(named).ToArray();
        if(quote.TargetPopulation<=0||population.Any(string.IsNullOrWhiteSpace)||population.Distinct(StringComparer.Ordinal).Count()!=population.Length||population.Length!=quote.TargetPopulation||
            (colony!=null&&colony.Id!=quote.ColonyId))
            gaps.Add("The exact named residents and already committed residents must fill the current population target once each.");
        bool SameNames(IEnumerable<string> a,IEnumerable<string> b)=>a.OrderBy(x=>x,StringComparer.Ordinal).SequenceEqual(b.OrderBy(x=>x,StringComparer.Ordinal),StringComparer.Ordinal);
        if(quote.Residents.Any(r=>r.Kind is not ("existing" or "recruit")||string.IsNullOrWhiteSpace(r.Name))||
            !SameNames(intent.ExistingResidentRosterIds,quote.Residents.Where(r=>r.Kind=="existing").Select(r=>r.RosterId))||
            !SameNames(intent.RecruitRosterIds,quote.Residents.Where(r=>r.Kind=="recruit").Select(r=>r.RosterId)))
            gaps.Add("The resolved founding intent must retain the exact existing and paid-arrival roster identities in its named bill.");
        if(intent?.NewArrivalCount!=2||quote.Residents.Count(r=>r.Kind=="recruit")!=2)gaps.Add("The core run needs two exact named paid arrivals under this approval.");
        var startup=quote.StartupPolicies;
        if(startup==null||!startup.SuppliesReorderEnabled||!startup.ServiceEnabled||!startup.LocalProcurementEnabled)gaps.Add("Supplies reordering, qualified service and local procurement must all be reviewed enabled startup terms.");
        var production=intent?.Production;
        if(production?.Mode!="localInvestment"||quote.ProductionInvestments.Count!=production.PackageCount)gaps.Add("The core run needs the exact current native cultivation investment bill.");
        var operations=production?.Operations;
        if(operations==null||!operations.RegisterCreatedEndpoints||!operations.AutomaticIntake||!operations.AutomaticInputRefill)gaps.Add("Paid-created endpoint registration, conserved intake and automatic input refill must be explicitly approved.");
        foreach(string resource in new[]{"Fertilizer","Machinery","Plutonium-238"})
        {
            var row=operations?.Resources.SingleOrDefault(r=>r.Resource==resource);
            if(row==null||row.InitialReserve<=0||!row.ReorderEnabled||row.ReorderPoint<=0||row.TargetAmount<row.ReorderPoint)gaps.Add("Review a positive owned buffer and finite enabled reorder/refill terms for "+resource+".");
        }
        if(quote.ProductionInvestments.Count>0&&(quote.ProductionInvestments.Any(i=>i.Inventory==null||i.Inventory.Endpoints.Count!=2)||quote.ProductionInvestments.Count(i=>i.Wolf.Id.Length>0)!=1))
            gaps.Add("The bill must retain each exact farm/feed endpoint specification and one shared compound WOLF sequence.");
        return gaps;
    }
    public static void Preserved(ColonyManagementSnapshot before,ColonyManagementSnapshot after,string id)
    {
        SameWorld(before,after);var a=Colony(before,id);var b=Colony(after,id);
        foreach(var f in a.Facilities)
        {var current=b.Facilities.SingleOrDefault(x=>x.Id==f.Id);Need(current!=null&&current.VesselId==f.VesselId&&current.PartIds.OrderBy(x=>x).SequenceEqual(f.PartIds.OrderBy(x=>x)),"An adopted vessel or registered member identity was lost/replaced: "+f.Id);}
        var remaining=b.VisitorRosterIds.Concat(b.Residents.Select(r=>r.RosterId)).ToHashSet(StringComparer.Ordinal);
        Need(a.VisitorRosterIds.Concat(a.Residents.Select(r=>r.RosterId)).All(remaining.Contains),"Existing colony people were lost during the acceptance sequence.");
    }
    public static void Founding(ColonyManagementSnapshot before,ColonyManagementSnapshot after,string id)
    {
        Preserved(before,after,id);ExternalClear(after);var colony=Colony(after,id);
        var plan=after.State!.Plans.Single(p=>p.ColonyId==id&&p.Quote.Kind=="founding"&&p.State!="cancelled");
        Need(plan.State=="complete"&&plan.RemainingFunds==0,"Reviewed founding procurement/construction is not complete.");
        Need(plan.Quote.Id==ColonyStateCodec.PlanningQuoteHash(plan.Quote),"Original reviewed quote lineage changed.");
        foreach(var line in plan.Buildings)
        {
            var order=after.State.Construction.Single(o=>o.Id==line.OrderId);
            var facility=colony.Facilities.Single(f=>f.ConstructionOrderId==order.Id);
            Need(order.State=="operational"&&order.FundsPaid&&order.Placement.Phase=="Anchored","A paid child lacks native placed/commissioned completion: "+line.Id);
            Need(facility.PartIds.Count>0&&facility.FoundationId.Length>0&&facility.PlacementWitnessHash.Length>0&&facility.Qualification.PlacementStable,"A building lacks real part/anchor/readback identities: "+line.Id);
            Need(order.Dependencies.All(d=>after.State.Construction.Single(o=>o.Id==d).State=="operational"),"Construction dependency is not operational.");
            if(line.Homes>0)Need(facility.Qualification.HousingCertified&&facility.CertifiedHomes>=line.Homes&&facility.HomePartPersistentIds.Count>0,"Housing remains a nominal capacity.");
        }
        Need(colony.SupportCommissionedUt.HasValue&&colony.Status=="operational","Resident support has not been commissioned from actual homes and funded stock.");
        Need(colony.Facilities.Where(f=>f.State=="operational"&&f.Qualification.HousingCertified).Sum(f=>f.CertifiedHomes)>=plan.Quote.TargetPopulation,"Actual homes do not cover the reviewed target.");
        foreach(var row in plan.Quote.Residents)
        {
            var claim=plan.Residents.Single(r=>r.Id==row.Id);
            Need(claim.State=="complete"&&claim.AfterWitness.Length>0,"Reviewed named inhabitant has no completed admission receipt: "+row.Name);
            Need(colony.Residents.Any(r=>r.RosterId==row.RosterId&&r.Status=="resident"&&r.HomeFacilityId==claim.HomeFacilityId&&r.HomePartId==claim.HomePartId),"Reviewed inhabitant lacks its exact real home mapping: "+row.Name);
        }
        if(plan.Quote.StartupPolicies is {} startup)
        {
            Need(plan.StartupReservesReleased&&plan.StartupPoliciesApplied,"Reviewed startup operating stock/policies did not complete.");
            foreach(var reviewed in startup.Reorders)
                Need(after.State.ReorderPolicies.Any(p=>p.ColonyId==id&&p.Resource==reviewed.Resource&&p.Enabled==reviewed.Enabled&&p.ReorderPoint==reviewed.ReorderPoint&&p.TargetAmount==reviewed.TargetAmount&&p.CadenceSeconds==reviewed.CadenceSeconds),"Saved recurring resource policy differs from its reviewed startup terms.");
            Need(after.State.ServicePolicies.Any(p=>p.ColonyId==id&&p.AutomaticEnabled==startup.ServiceEnabled&&p.TargetFillFraction==startup.ServiceTargetFillFraction&&p.CadenceSeconds==startup.ServiceCadenceSeconds),"Installed service policy differs from its reviewed startup terms.");
            Need(after.State.PhysicalPolicies.Any(p=>p.ColonyId==id&&p.Enabled==startup.LocalProcurementEnabled&&p.MaximumTransfer==startup.LocalProcurementMaximumMicroUnits&&p.NativeInputTargetFraction==startup.NativeInputBufferFraction),"Local physical procurement policy differs from its reviewed startup terms.");
        }
    }
    public static void Arrivals(ColonyManagementSnapshot before,ColonyManagementSnapshot after,string id,int count=2)
    {
        Preserved(before,after,id);ExternalClear(after);var colony=Colony(after,id);var prior=before.State!.PeopleOperations.Select(p=>p.Id).ToHashSet();
        var arrivals=after.State!.PeopleOperations.Where(p=>p.ColonyId==id&&p.Kind=="arrival"&&!prior.Contains(p.Id)&&p.State!="cancelled").ToArray();
        Need(arrivals.Length==count,"Expected exactly "+count+" distinct new paid passenger arrivals.");
        foreach(var op in arrivals)
        {
            Need(op.State=="complete"&&op.FundsPaid&&op.Funds>0&&op.AfterWitness.Length>0&&op.ArrivalUt>=op.DepartUt+op.TravelSeconds,"Passenger fare, chronology or actual arrival witness is missing.");
            var roster=after.People.Roster.Single(p=>p.RosterId==op.RosterId);
            Need(roster.Current&&roster.Type=="Crew"&&roster.Status=="Assigned"&&!roster.ProtectedMissionCrew,"Arrival did not use actual ordinary roster crew.");
            Need(after.People.Seats.Sum(s=>s.Occupants.Count(n=>n==op.RosterId))==1&&after.People.Seats.Any(s=>s.HousingCertified&&s.PartId==op.HomePartId&&s.Occupants.Contains(op.RosterId)),"The actual passenger was not added exactly once to the certified home cabin.");
            Need(colony.Residents.Any(r=>r.RosterId==op.RosterId&&r.Status=="resident"),"Arrival did not produce the matching resident record.");
        }
    }
    public static void SupportDays(ColonyManagementSnapshot before,ColonyManagementSnapshot after,string id,int days=3)
    {
        SameWorld(before,after);ExternalClear(after);var a=Colony(before,id);var b=Colony(after,id);
        Need(a.SupportCommissionedUt.HasValue&&a.SupportPolicyHash==b.SupportPolicyHash&&a.SupportMicroUnitsPerPersonDay==b.SupportMicroUnitsPerPersonDay,"Support owner or model balance changed.");
        var population=a.Residents.Where(r=>r.Status!="missing").Select(r=>r.RosterId).Concat(a.VisitorRosterIds).OrderBy(x=>x,StringComparer.Ordinal).ToArray();
        Need(population.SequenceEqual(b.Residents.Where(r=>r.Status!="missing").Select(r=>r.RosterId).Concat(b.VisitorRosterIds).OrderBy(x=>x,StringComparer.Ordinal)),"Population changed during the isolated support measurement.");
        decimal dt=(decimal)(b.SupportAccountedUt-a.SupportAccountedUt);
        Need(dt>=days*(decimal)ColonyLimits.KerbinDay,"Fewer than the required actual unloaded days were accounted.");
        long expected=checked((long)decimal.Floor(a.SupportRemainder+(decimal)population.Length*a.SupportMicroUnitsPerPersonDay*dt/(decimal)ColonyLimits.KerbinDay));
        long consumed=b.SupportConsumedMicroUnits-a.SupportConsumedMicroUnits;
        Need(consumed==expected,"Support did not consume exactly the population/model/UT integral.");
        Need(!b.SupportStatus.StartsWith("Shortage",StringComparison.Ordinal),"The colony exhausted supplies during the downside run.");
        // This measurement permits paid supply arrivals/reorders. Freeze building
        // operations and colony export/service writes to keep a closed balance.
        Need(before.State!.Construction.Where(o=>o.ColonyId==id).Select(o=>o.Id+":"+o.State).OrderBy(x=>x).SequenceEqual(after.State!.Construction.Where(o=>o.ColonyId==id).Select(o=>o.Id+":"+o.State).OrderBy(x=>x)),"Construction changed during support conservation measurement.");
        Need(!after.State.ServiceOperations.Any(o=>o.ColonyId==id&&o.SourceResource=="Supplies"&&!before.State.ServiceOperations.Any(p=>p.Id==o.Id&&p.State==o.State)),"A supply service changed during the support measurement.");
        long arrivals=after.State.Shipments.Where(s=>s.ColonyId==id&&s.Resource=="Supplies"&&s.Kind=="import"&&s.State=="arrived"&&!before.State.Shipments.Any(p=>p.Id==s.Id&&p.State=="arrived")).Sum(s=>s.Amount);
        long physical=after.State.PhysicalTransfers.Where(o=>o.ColonyId==id&&o.Resource=="Supplies"&&o.State=="complete"&&!before.State.PhysicalTransfers.Any(p=>p.Id==o.Id&&p.State=="complete")).Sum(o=>o.Direction=="toColony"?o.Amount:-o.Amount);
        Need(!after.State.Shipments.Any(s=>s.ColonyId==id&&s.Resource=="Supplies"&&s.Kind=="export"&&!before.State.Shipments.Any(p=>p.Id==s.Id)),"Supply exports changed the measurement balance.");
        Need(b.Stock.Single(s=>s.Resource=="Supplies").Amount==a.Stock.Single(s=>s.Resource=="Supplies").Amount+arrivals+physical-consumed,"Owned supplies do not balance witnessed arrivals/transfers minus support consumption.");
    }
    public static void Physical(ColonyManagementSnapshot before,ColonyManagementSnapshot after,string id,string operationId)
    {
        SameWorld(before,after);ExternalClear(after);var op=after.State!.PhysicalTransfers.Single(o=>o.ColonyId==id&&o.Id==operationId);
        Need(!before.State!.PhysicalTransfers.Any(o=>o.Id==operationId&&o.State=="complete"),"Baseline must precede this exact transfer completion.");
        Need(op.State=="complete"&&op.Provider.Length>0&&op.BeforeWitness.Length>0&&op.AfterWitness.Length>0,"Transfer lacks exact terminal provider witnesses.");
        double delta=op.Amount/(double)ColonyLimits.Units;
        Need(op.PhysicalAfter==op.PhysicalBefore+(op.Direction=="toColony"?-delta:delta),"Provider before/after amount is not the exact manifest change.");
        var effect=after.State.Effects.Single(e=>e.OperationId==op.Id&&e.Kind=="physicalTransfer");Need(effect.State=="applied"&&effect.FundsDelta==0,"Physical transfer incorrectly charged funds or lacks the terminal effect.");
        var a=Colony(before,id).Stock.Single(s=>s.Resource==op.Resource);var b=Colony(after,id).Stock.Single(s=>s.Resource==op.Resource);
        Need(b.Amount==a.Amount+(op.Direction=="toColony"?op.Amount:-op.Amount),"Freeze competing same-resource writes: owned stock must change once by the exact amount.");
        var row=after.Planning.LocalStocks.SingleOrDefault(s=>s.ColonyId==id&&s.PartId==op.PartId&&s.Resource==op.Resource);
        Need(row!=null&&row.Current&&row.Provider==op.Provider&&Math.Floor(op.PhysicalAfter*ColonyLimits.Units)==row.Amount,"Fresh selected provider readback does not match the durable exact tank witness.");
    }
    public static void Growth(ColonyManagementSnapshot before,ColonyManagementSnapshot after,string id)
    {
        Preserved(before,after,id);ExternalClear(after);var prior=before.State!.Plans.Select(p=>p.Id).ToHashSet();
        var plan=after.State!.Plans.Single(p=>p.ColonyId==id&&p.Quote.Kind=="growth"&&!prior.Contains(p.Id)&&p.State!="cancelled");
        Need(plan.Quote.Buildings.Count==1&&plan.Quote.Buildings.Single().Role=="housing"&&plan.State=="complete"&&plan.RemainingFunds==0,"One bounded real habitat has not finished.");
        Need(plan.Quote.Forecast.Sustainable&&Colony(before,id).Residents.Count(r=>r.Status!="missing")+plan.Quote.Forecast.OpenQualifiedJobs>plan.Quote.Forecast.QualifiedHomes&&plan.Quote.TargetPopulation>plan.Quote.Forecast.QualifiedHomes,"Saved growth quote lacks genuine residents plus open jobs exceeding actual homes.");
        var order=after.State.Construction.Single(o=>o.Id==plan.Buildings.Single().OrderId);var facility=Colony(after,id).Facilities.Single(f=>f.ConstructionOrderId==order.Id);
        Need(order.FundsPaid&&order.State=="operational"&&facility.Qualification.HousingCertified&&facility.HomePartPersistentIds.Count>0,"Growth did not produce paid, physically certified housing.");
        Need(Colony(before,id).Residents.Select(r=>r.RosterId).OrderBy(x=>x).SequenceEqual(Colony(after,id).Residents.Select(r=>r.RosterId).OrderBy(x=>x)),"Construction fabricated or reassigned residents.");
    }
    public static object ProductionReadiness(ColonyManagementSnapshot s,string id)=>new {
        Catalog=s.Production.Recipes,Reason=s.Production.Reason,Registry=s.Production.Registry,
        CurrentServiceTargets=s.Services.Targets.Where(t=>t.ColonyId==id),CurrentPhysicalStocks=s.Planning.LocalStocks.Where(t=>t.ColonyId==id),
        Investments=s.State!.Plans.Where(p=>p.ColonyId==id).SelectMany(p=>p.Production.Select(c=>new {
            PlanId=p.Id,InvestmentId=c.Id,c.State,c.Reason,c.WolfOrderId,c.OutputWitness,c.OutputObservedUt,c.OutputIntervalSeconds,c.OutputSuppliesUnits,
            ReviewedOperations=p.Quote.FoundingIntent?.Production?.Operations,
            ReviewedInventory=p.Quote.ProductionInvestments.Single(i=>i.Id==c.Id).Inventory,SavedInventory=c.Inventory,
            ReviewedWolf=p.Quote.ProductionInvestments.Single(i=>i.Id==c.Id).Wolf,
            Steps=c.Steps,Current=s.Production.Observations.SingleOrDefault(o=>o.PlanId==p.Id&&o.InvestmentId==c.Id),
            Next=c.State=="held"?"Reconcile the exact held native effect; do not repeat connect/start or replace the reviewed operation.":c.State=="planned"?"Wait for paid finite WOLF dependencies, real anchored buildings and the reviewed physically assigned Scientist.":c.State=="ready"||c.State=="configuring"?"Observe the runtime's exact native connect/start children, actual generator/fuel and registry before/after; idle hardware is not productive.":"Require current qualified native output; separately prove conserved physical-to-owned stock transfer and packed/unloaded cold-load continuation."})),
        EvidenceBoundary="Recorded snapshot review only. Nominal rates and WOLF points do not establish physical output or managed stock. Generator, endpoint, intake/refill and loaded/packed/BRP cold-load proofs remain native acceptance requirements."};
    public static void Production(ColonyManagementSnapshot before,ColonyManagementSnapshot after,string id)
    {
        Preserved(before,after,id);ExternalClear(after);
        var plans=after.State!.Plans.Where(p=>p.ColonyId==id&&p.State!="cancelled"&&p.Production.Count>0).ToArray();Need(plans.Length>0,"No explicitly reviewed local production investment exists.");
        foreach(var plan in plans)foreach(var claim in plan.Production)
        {
            var item=plan.Quote.ProductionInvestments.Single(i=>i.Id==claim.Id);
            Need(claim.State=="operational"&&claim.OutputWitness==ColonyEngine.ProductionOutputWitness(plan,claim)&&claim.OutputIntervalSeconds>0&&claim.OutputSuppliesUnits>0,"No finite positive actual first-output receipt exists for the reviewed native module.");
            Need(claim.Steps.Count==5&&claim.Steps.All(s=>s.State=="applied"&&s.BeforeWitness.Length>0&&s.AfterWitness.Length>0&&after.State.Effects.Any(e=>e.Id==s.Id&&e.Kind=="productionNative"&&e.State=="applied"))&&claim.Steps.Where(s=>s.Kind=="hopperConnect").All(s=>s.HopperId.Length>0),"Native connect/start receipts or exact created HopperId lineage are missing.");
            var current=after.Production.Observations.SingleOrDefault(o=>o.PlanId==plan.Id&&o.InvestmentId==claim.Id);
            Need(current!=null&&current.Active&&current.Qualified&&current.ContextKey==after.ContextKey&&after.ObservedUt>=current.ObservedUt&&after.ObservedUt-current.ObservedUt<=10&&current.RecipeHash==item.Recipe.ConfigurationHash&&current.PartId==claim.OutputPartId&&current.ModuleId==claim.OutputModuleId,"Historical output cannot override absent, stale or currently unqualified native production.");
        }
    }
    public static void ProductionOperations(ColonyManagementSnapshot before,ColonyManagementSnapshot after,string id)
    {
        Production(before,after,id);
        var state=after.State!;var plans=state.Plans.Where(p=>p.ColonyId==id&&p.State!="cancelled"&&p.Production.Count>0).ToArray();
        foreach(var plan in plans)
        {
            Need(plan.Quote.FoundingIntent?.Production?.Operations is {RegisterCreatedEndpoints:true,AutomaticIntake:true,AutomaticInputRefill:true},"This approval did not authorize the complete endpoint/intake/refill loop.");
            foreach(var reviewed in ColonyEngine.ProductionOperatingReorders(plan.Quote))
                Need(state.ReorderPolicies.Any(p=>p.ColonyId==id&&p.Resource==reviewed.Resource&&p.Enabled==reviewed.Enabled&&p.ReorderPoint==reviewed.ReorderPoint&&p.TargetAmount==reviewed.TargetAmount&&p.CadenceSeconds==reviewed.CadenceSeconds),"Saved production reorder differs from its exact reviewed terms: "+reviewed.Resource);
            foreach(var claim in plan.Production)
            {
                var quote=plan.Quote.ProductionInvestments.Single(i=>i.Id==claim.Id).Inventory;var progress=claim.Inventory;
                Need(quote!=null&&progress!=null&&progress.ReservesReleased&&progress.PoliciesApplied&&progress.Endpoints.Count==2&&progress.Endpoints.All(e=>e.State=="applied"&&e.MembershipHash.Length==64&&e.BeforeWitness.Length>0&&e.AfterWitness.Length>0),"Paid-created endpoints or owned operating stock/policies lack exact terminal receipts.");
                Need(after.Production.Registry.Ready&&after.Production.Registry.WorldId==quote!.RegistryWorldId,"Current selected physical registry authority is unavailable or belongs to another world.");
                var farm=progress!.Endpoints.Single(r=>quote!.Endpoints.Single(s=>s.Id==r.SpecId).Role=="cultivation");
                bool Selected(ColonyPhysicalTransfer op)=>op.ColonyId==id&&op.DepotId==farm.DepotId&&farm.MemberPartIds.Contains(op.PartId)&&op.State=="complete"&&op.Amount>0&&op.Provider.Length>0&&op.BeforeWitness.Length>0&&op.AfterWitness.Length>0&&
                    !before.State!.PhysicalTransfers.Any(p=>p.Id==op.Id&&p.State=="complete")&&state.Effects.Any(e=>e.OperationId==op.Id&&e.Kind=="physicalTransfer"&&e.State=="applied"&&e.FundsDelta==0)&&
                    op.PhysicalAfter==op.PhysicalBefore+(op.Direction=="toColony"?-1:1)*op.Amount/(double)ColonyLimits.Units;
                Need(state.PhysicalTransfers.Any(o=>Selected(o)&&o.Direction=="toColony"&&o.Resource=="Supplies"&&o.CompletedUt>=claim.OutputObservedUt),"No new conserved Supplies intake follows actual native output for this paid farm.");
                Need(state.PhysicalTransfers.Any(o=>Selected(o)&&o.Direction=="toPhysical"&&o.Resource=="Fertilizer"),"No new exact Fertilizer input refill receipt exists for this paid farm.");
                foreach(string resource in new[]{"Machinery","Plutonium-238"})
                    Need(state.ServiceOperations.Any(o=>o.ColonyId==id&&o.FacilityId==farm.FacilityId&&o.DepotId==farm.DepotId&&farm.MemberPartIds.Contains(o.PartId)&&o.SourceResource==resource&&o.DestinationResource==resource&&o.State=="complete"&&o.SourceDebited&&o.Amount>0&&o.BeforeWitness.Length>0&&o.AfterWitness.Length>0&&o.Provider.Length>0&&
                        !before.State!.ServiceOperations.Any(p=>p.Id==o.Id&&p.State=="complete")&&state.Effects.Any(e=>e.OperationId==o.Id&&e.Kind=="serviceTransfer"&&e.State=="applied"&&e.FundsDelta==0)),"No new qualified owned-to-installed service receipt exists for "+resource+" on this paid farm.");
            }
        }
    }
}
