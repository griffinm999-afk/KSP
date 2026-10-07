using System.Text;
using Expanse.Domain.Colonies;

namespace Expanse.Clock.Tests;

public sealed class ColonyPlanningStartupTests
{
    const long U=ColonyLimits.Units;
    static string Id()=>Guid.NewGuid().ToString("D");
    static string Hash(string value)=>ColonyStateCodec.Hash(Encoding.UTF8.GetBytes(value));
    static ColonyFoundingIntent Intent(bool enabled=true)=>new(){FillPopulationTarget=false,Startup=new(){SuppliesReorderEnabled=enabled,
        ServiceEnabled=enabled,LocalProcurementEnabled=enabled,MaterialKitsReserveMicroUnits=2*U}};
    static ColonyState Approve(ColonyState state,ColonyEnvironment env,string id,ColonyFoundingIntent intent)
    {
        var quote=ColonyEngine.QuoteFoundingPlan(state,id,env,intent);Assert.True(quote.CanApprove,string.Join(" ",quote.Blockers));
        var result=ColonyEngine.Execute(state,new(){OperationId=Id(),ContextKey=env.ContextKey,ExpectedRevision=state.Revision,ColonyId=id,
            Kind="approveFoundingPlan",QuoteId=quote.Id,FoundingIntent=quote.FoundingIntent},env);
        Assert.Equal("accepted",result.Outcome);return result.State;
    }
    static void AddVisitors(ColonyState state,ColonyEnvironment env,string id,int count)
    {
        var colony=state.Colonies.Single();var source=new ColonyFacility{Id=Id(),VesselId=Id(),Name="Observed visitor cabin",PartIds=new(){99}};
        colony.Facilities.Add(source);var seat=new ColonySeatWitness{FacilityId=source.Id,VesselId=source.VesselId,PartId=99,Capacity=count+1,
            Current=true,ContextKey=env.ContextKey,Evidence="Explicit pure-test actual crew provider"};env.People.Seats.Add(seat);
        for(int i=0;i<count;i++)
        {string roster="visitor "+i;colony.VisitorRosterIds.Add(roster);env.People.PresentByColony[id].Add(roster);seat.Occupants.Add(roster);
            env.People.Roster.Add(new(){RosterId=roster,Name=roster,Trait="Scientist",Type="Crew",Status="Assigned",VesselId=source.VesselId,
                PartId=99,Current=true,ContextKey=env.ContextKey});}
    }
    static ColonyFoundingIntent AddRecruits(ColonyEnvironment env,int count)
    {
        var route=new ColonyPassengerRoute{Id="finite-passenger-test",Hash=Hash("paid route"),Body="Minmus",Fare=500,RecruitmentFee=100,
            TravelSeconds=20,ConcurrentSeats=2,Qualified=true,Evidence="Explicit test transport provider"};env.People.Routes.Add(route);
        var intent=Intent();intent.NewArrivalCount=count;intent.PassengerRouteId=route.Id;
        for(int i=0;i<count;i++){string roster="recruit "+i;intent.RecruitRosterIds.Add(roster);env.People.Roster.Add(new(){RosterId=roster,
            Name=roster,Trait="Scientist",Type="Applicant",Status="Available",Current=true,ContextKey=env.ContextKey});}
        return intent;
    }
    // Advances only paid events, finite declared labor and explicit pure provider
    // readback. This does not claim KSP native placement/roster certification.
    static ColonyState Complete(ColonyState state,ColonyEnvironment env,Action<ColonyState>? observe=null)
    {
        for(int tick=0;tick<100 && state.Plans.Single().State!="complete";tick++)
        {
            state=ColonyEngine.RunPlanning(state,env);
            state=ColonyEngine.RunPlanningPolicies(state,env);
            observe?.Invoke(state);
            while(state.Effects.Any(e=>e.State=="prepared"&&e.FundsDelta<0))state=ColonyPlanningTests.Pay(state,env);
            var building=state.Construction.FirstOrDefault(o=>o.State!="operational"&&o.State!="cancelled");
            if(building!=null){state=ColonyPlanningTests.CompleteOne(state,env,building.Id);continue;}
            double? next=state.Shipments.Where(s=>s.State=="inTransit").Select(s=>(double?)s.ArrivalUt)
                .Concat(state.PeopleOperations.Where(o=>o.State=="inTransit").Select(o=>(double?)o.ArrivalUt))
                .Concat(state.Colonies.Where(c=>c.Logistics.State=="delivering").Select(c=>(double?)(c.Logistics.ActivationUt+c.Logistics.TravelSeconds))).OrderBy(t=>t).FirstOrDefault();
            if(next.HasValue){env.Ut=next.Value;state=ColonyEngine.Advance(state,env);}
            foreach(var ready in state.Effects.Where(e=>e.State=="prepared"&&e.Kind=="peopleArrival").ToArray())
            {
                var op=state.PeopleOperations.Single(o=>o.Id==ready.TargetId);var seat=env.People.Seats.Single(s=>s.PartId==op.HomePartId&&s.FacilityId==op.HomeFacilityId);
                seat.CrewMutationSupported=true;
                state=ColonyEngine.PreparePeopleEffect(state,ready.Id,env,"Explicit pure-test before roster/cabin witness");
                var person=env.People.Roster.Single(p=>p.RosterId==op.RosterId);person.Type="Crew";person.Status="Assigned";person.VesselId=seat.VesselId;person.PartId=seat.PartId;
                seat.Occupants.Add(person.RosterId);env.People.PresentByColony[op.ColonyId].Add(person.RosterId);
                state=ColonyEngine.CompletePeopleEffect(state,ready.Id,env,"Explicit pure-test exact crew/cabin after witness");
            }
        }
        Assert.Equal("complete",state.Plans.Single().State);return state;
    }
    [Fact] public void StartupReserveIncludesPresentUnionPromisedImmigrantsWithoutCountingVisitorDesignationsTwice()
    {
        var(s,e,id)=ColonyPlanningTests.Fixture();AddVisitors(s,e,id,3);var intent=AddRecruits(e,2);
        var q=ColonyEngine.QuoteFoundingPlan(s,id,e,intent);Assert.True(q.CanApprove,string.Join(" ",q.Blockers));
        Assert.Equal(5*U,q.StartupSupportReserve);Assert.Equal(5,q.StartupPolicies!.SupportedPeople);Assert.Equal(5,q.Forecast.SupportedPeople);
        Assert.Equal(10*U+9260,q.StartupPolicies.SuppliesTargetMicroUnits);Assert.Equal(5*U+9260,q.StartupPolicies.ReserveMaterials.Single(m=>m.Resource=="Supplies").Amount);
        Assert.Equal(1200,q.Residents.Sum(r=>r.Fare));Assert.Equal(q.Buildings.Sum(b=>b.Funds)+q.StagingFunds+q.Imports.Sum(i=>i.Funds)+1200,q.TotalFunds);
        intent=Intent();intent.ExistingResidentRosterIds.Add("visitor 0");q=ColonyEngine.QuoteFoundingPlan(s,id,e,intent);
        Assert.True(q.CanApprove,string.Join(" ",q.Blockers));Assert.Equal(3*U,q.StartupSupportReserve);Assert.Equal(3,q.StartupPolicies!.SupportedPeople);
    }
    [Fact] public void InitialBuffersUseCentralClaimsAndProtectStockFromUnrelatedMaintenance()
    {
        var(s,e,id)=ColonyPlanningTests.Fixture();s=ColonyPlanningTests.Stage(s,e,id);var c=s.Colonies.Single();
        c.Stock.Single(x=>x.Resource=="MaterialKits").Amount=7*U;c.Stock.Single(x=>x.Resource=="Supplies").Amount=5*U;
        s=Approve(s,e,id,Intent());var plan=s.Plans.Single();Assert.Empty(plan.Imports);Assert.Equal(7*U,plan.Claims.Single(x=>x.Resource=="MaterialKits").Reserved);
        Assert.Equal(plan.Quote.StartupPolicies!.ReserveMaterials.Single(m=>m.Resource=="Supplies").Amount,plan.Claims.Single(x=>x.Resource=="Supplies").Reserved);Assert.False(plan.StartupReservesReleased);
        var f=new ColonyFacility{Id=Id(),VesselId=Id(),Name="Actual fixture installed service",PartIds=new(){88}};s.Colonies.Single().Facilities.Add(f);
        var target=new ColonyServiceTarget{ColonyId=id,FacilityId=f.Id,PartId=88,DestinationResource="ReplacementParts",SourceResource="MaterialKits",Capacity=10*U,
            Current=true,CanApply=true,QualifiedWorker=true,ContextKey=e.ContextKey,QuoteHash=Hash("service"),Provider="Explicit test physical service"};e.Services.Targets.Add(target);
        var result=ColonyEngine.Execute(s,new(){OperationId=Id(),ColonyId=id,ExpectedRevision=s.Revision,ContextKey=e.ContextKey,Kind="serviceFacility",TargetId=f.Id,
            Fields=new(){["PartId"]="88",["DestinationResource"]="ReplacementParts",["ServiceQuoteHash"]=target.QuoteHash,["AmountMicroUnits"]=U.ToString()}},e);
        Assert.Equal("rejected",result.Outcome);Assert.Contains("insufficient",result.Reason);Assert.Empty(s.ServiceOperations);
    }
    [Fact] public void OneApprovalPurchasesConservativelyCompletesActualBuildingsAndInstallsOnlyReviewedPoliciesOnce()
    {
        var(s,e,id)=ColonyPlanningTests.Fixture();s=Approve(s,e,id,Intent());var q=s.Plans.Single().Quote;
        Assert.Equal(7*U,q.Imports.Single(i=>i.Resource=="MaterialKits").Amount);Assert.Equal(q.StartupPolicies!.SuppliesTargetMicroUnits,q.Imports.Single(i=>i.Resource=="Supplies").Amount);
        s=Complete(s,e);var p=s.Plans.Single();Assert.True(p.StartupReservesReleased);Assert.True(p.StartupPoliciesApplied);
        Assert.All(p.Claims,c=>{Assert.Equal(0,c.Reserved);Assert.Equal(0,c.Remaining);});
        Assert.Equal(2*U,s.Colonies.Single().Stock.Single(x=>x.Resource=="MaterialKits").Amount);
        Assert.Equal(q.StartupPolicies!.SuppliesTargetMicroUnits,s.Colonies.Single().Stock.Single(x=>x.Resource=="Supplies").Amount);
        Assert.True(s.ServicePolicies.Single().AutomaticEnabled);Assert.True(s.PhysicalPolicies.Single().Enabled);Assert.Equal(1000*U,s.PhysicalPolicies.Single().MaximumTransfer);
        Assert.Equal(2,s.ReorderPolicies.Count);Assert.All(s.ReorderPolicies,p=>Assert.True(p.Enabled));Assert.Equal(q.TotalFunds,s.Colonies.Single().SpentFunds);
        var again=ColonyEngine.RunPlanning(s,e);Assert.Equal(s.Colonies.Single().SpentFunds,again.Colonies.Single().SpentFunds);
        Assert.Equal(s.ReorderPolicies.Count,again.ReorderPolicies.Count);Assert.Equal(q.Id,ColonyStateCodec.Deserialize(ColonyStateCodec.Serialize(s)).Plans.Single().Quote.Id);
    }
    [Fact] public void DeclinedStartupPoliciesStayDisabledAndZeroBuffersDoNotCreateGuessedRequirements()
    {
        var(s,e,id)=ColonyPlanningTests.Fixture();var intent=Intent(false);intent.Startup!.MaterialKitsReserveMicroUnits=0;
        s=Complete(Approve(s,e,id,intent),e);Assert.False(s.ReorderPolicies.Single().Enabled);Assert.False(s.ServicePolicies.Single().AutomaticEnabled);Assert.False(s.PhysicalPolicies.Single().Enabled);
        Assert.Single(s.Plans.Single().Quote.StartupPolicies!.StockTargets);Assert.Empty(s.Plans.Single().Quote.StartupPolicies!.ReserveMaterials);
        Assert.Equal(0,s.Colonies.Single().Stock.Single(x=>x.Resource=="MaterialKits").Amount);
    }
    [Fact] public void PaidArrivalsRemainFundingCommitmentsUntilTheirOwnExactReservationAndReceipt()
    {
        var(s,e,id)=ColonyPlanningTests.Fixture();var intent=AddRecruits(e,2);s=Approve(s,e,id,intent);var q=s.Plans.Single().Quote;
        Assert.Equal(q.TotalFunds,ColonyEngine.PendingCash(s));Assert.Empty(s.PeopleOperations);
        s=Complete(s,e);Assert.Equal(2,s.Colonies.Single().Residents.Count);Assert.All(s.PeopleOperations,o=>Assert.Equal("complete",o.State));
        Assert.All(s.Plans.Single().Residents,r=>{Assert.Equal("complete",r.State);Assert.NotEmpty(r.AfterWitness);});
        Assert.Equal(q.TotalFunds,s.Colonies.Single().SpentFunds);Assert.Equal(0,s.Plans.Single().RemainingFunds);Assert.Equal(0,ColonyEngine.PendingCash(s));
        Assert.Equal(2,ColonyEngine.Forecast(s,id,e).SupportedPeople);Assert.Equal(q.Id,s.Plans.Single().Quote.Id);
    }
    [Fact] public void BufferAndCadenceChangesInvalidateReviewedHashAndFareBudgetCannotHideInsideConstruction()
    {
        var(s,e,id)=ColonyPlanningTests.Fixture();var intent=AddRecruits(e,2);var q=ColonyEngine.QuoteFoundingPlan(s,id,e,intent);
        intent.Startup!.ServiceCadenceSeconds=43200;Assert.NotEqual(q.Id,ColonyEngine.QuoteFoundingPlan(s,id,e,intent).Id);
        intent.Startup.MaterialKitsReserveMicroUnits=3*U;Assert.NotEqual(q.Id,ColonyEngine.QuoteFoundingPlan(s,id,e,intent).Id);
        e.AvailableFunds=q.TotalFunds+s.Colonies.Single().Charter.CashFloor-1;
        var held=ColonyEngine.QuoteFoundingPlan(s,id,e,q.FoundingIntent);Assert.False(held.CanApprove);Assert.Contains(held.Blockers,b=>b.Contains("cash"));
        e.AvailableFunds=50000;s.Colonies.Single().Charter.FoundingBudget=q.TotalFunds-1;
        held=ColonyEngine.QuoteFoundingPlan(s,id,e,q.FoundingIntent);Assert.False(held.CanApprove);Assert.Contains(held.Blockers,b=>b.Contains("budget"));
    }
    [Fact] public void OperatingTargetsMustFitFiniteReceivingCapacityAndSupplierInventory()
    {
        var(s,e,id)=ColonyPlanningTests.Fixture();var intent=Intent();intent.Startup!.SuppliesTargetMicroUnits=1001*U;
        Assert.Contains(ColonyEngine.QuoteFoundingPlan(s,id,e,intent).Blockers,b=>b.Contains("Receiving capacity"));
        intent.Startup.SuppliesTargetMicroUnits=101*U;Assert.Contains(ColonyEngine.QuoteFoundingPlan(s,id,e,intent).Blockers,b=>b.Contains("Finite supplier"));
        intent.Startup.SuppliesTargetMicroUnits=U;Assert.Contains(ColonyEngine.QuoteFoundingPlan(s,id,e,intent).Blockers,b=>b.Contains("actual present"));
    }
    [Fact] public void CancellingUnstartedStartupReleasesClaimsButDoesNotMintMaterialsOrActivatePolicies()
    {
        var(s,e,id)=ColonyPlanningTests.Fixture();s=ColonyPlanningTests.Stage(s,e,id);s.Colonies.Single().Stock.Single(x=>x.Resource=="MaterialKits").Amount=7*U;
        s=Approve(s,e,id,Intent());var result=ColonyEngine.Execute(s,new(){OperationId=Id(),ColonyId=id,ExpectedRevision=s.Revision,ContextKey=e.ContextKey,Kind="cancelColonyPlan",TargetId=s.Plans.Single().Id},e);
        Assert.Equal("accepted",result.Outcome);Assert.Equal(7*U,result.State.Colonies.Single().Stock.Single(x=>x.Resource=="MaterialKits").Amount);
        Assert.All(result.State.Plans.Single().Claims,c=>Assert.Equal(0,c.Reserved));Assert.Empty(result.State.ServicePolicies);Assert.Empty(result.State.ReorderPolicies);Assert.Equal(0,ColonyEngine.PendingCash(result.State));
    }
    [Fact] public void ProductionComparisonDoesNotPretendWolfCapacityIsPhysicalManufacturingThroughput()
    {
        var(s,e,id)=ColonyPlanningTests.Fixture();var q=ColonyEngine.QuoteFoundingPlan(s,id,e,Intent());Assert.True(q.CanApprove);
        Assert.All(q.MakeImportComparisons,r=>{Assert.False(r.ProductionInvestmentSupported);Assert.Contains("WOLF abstract points do not establish physical stock output",r.UnsupportedReason);
            Assert.Equal(q.Imports.Where(i=>i.Resource==r.Resource).Sum(i=>i.Funds),r.ReviewedMaximumImportFunds);Assert.True(r.ImportMassTonnes>0);Assert.True(r.ImportVolumeLiters>0);});
        var hash=q.Id;q.MakeImportComparisons.Clear();Assert.Equal(hash,ColonyStateCodec.PlanningQuoteHash(q));
    }
    [Fact] public void ThreeDayPassengerAndFreightLeadDoNotDeadlockReserveAdmissionOrDeferredReorders()
    {
        var(s,e,id)=ColonyPlanningTests.Fixture();AddVisitors(s,e,id,23);s.Colonies.Single().Charter.ReserveDays=6;
        var supply=e.EconomyPolicies.Single().Suppliers.Single(x=>x.Resource=="Supplies");supply.Available=1000*U;supply.TravelSeconds=3*ColonyLimits.KerbinDay;
        e.EconomyPolicies.Single().Hash=ColonyEngine.EconomyPolicyHash(e.EconomyPolicies.Single());
        var intent=AddRecruits(e,2);e.People.Routes.Single().TravelSeconds=3*ColonyLimits.KerbinDay;
        s=Approve(s,e,id,intent);var q=s.Plans.Single().Quote;Assert.Equal(150*U,q.StartupSupportReserve);
        Assert.Equal(300*U,q.StartupPolicies!.SuppliesReorderPointMicroUnits);Assert.Equal(325*U,q.StartupPolicies.SuppliesTargetMicroUnits);
        bool pendingObserved=false;
        s=Complete(s,e,current=>
        {
            if(current.Plans.Single().Residents.Any(r=>r.State=="queued"))
            {pendingObserved=true;Assert.True(current.Plans.Single().StartupPoliciesApplied);Assert.True(current.ReorderPolicies.Single(p=>p.Resource=="Supplies").Enabled);
                Assert.NotNull(current.Colonies.Single().SupportCommissionedUt);Assert.NotEqual("complete",current.Plans.Single().State);}
        });
        Assert.True(pendingObserved);Assert.Equal(2,s.Colonies.Single().Residents.Count);Assert.Equal(25,ColonyEngine.Forecast(s,id,e).SupportedPeople);
        Assert.True(s.Colonies.Single().Stock.Single(x=>x.Resource=="Supplies").Amount>=150*U);
        Assert.Contains(s.Shipments,x=>x.Resource=="Supplies"&&x.Id!=s.Plans.Single().Imports.Single(i=>i.Resource=="Supplies").ShipmentId);
        Assert.True(s.Colonies.Single().SpentFunds>=q.TotalFunds);Assert.True(e.AvailableFunds>s.Colonies.Single().Charter.CashFloor);
    }
}
