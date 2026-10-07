using System.Text;
using Expanse.Domain.Colonies;

namespace Expanse.Clock.Tests;

// Pure financial/material/provider-contract tests. Fixture survey/placement
// evidence does not certify any actual package or KSP terrain/runtime adapter.
public sealed class ColonyPlanningTests
{
    const long U=ColonyLimits.Units;
    static string Id()=>Guid.NewGuid().ToString("D");
    static string Hash(string s)=>ColonyStateCodec.Hash(Encoding.UTF8.GetBytes(s));
    internal static (ColonyState state,ColonyEnvironment env,string colonyId) Fixture()
    {
        var state=ColonyEngine.Create(Id(),100);
        var colony=new ColonyRecord {Id=Id(),Name="Planning test",FoundedUt=100,SupportAccountedUt=100,Site=new(){Body="Minmus"},
            Charter=new(){PopulationTarget=2,ResidentLimit=12,ReserveDays=1,CashFloor=100,FoundingBudget=50000,SpendingLimit=50000}};
        state.Colonies.Add(colony);
        var env=new ColonyEnvironment {WorldId=state.WorldId,ContextKey="explicit planning test provider",Ut=100,AvailableFunds=50000,
            BodyRadiiMeters=new(){["Minmus"]=60000},Support=new(){Ready=true,PolicyId="support",PolicyHash=Hash("support"),MicroUnitsPerPersonDay=U},
            People=new(){PresenceComplete=true,PresentByColony=new(){[colony.Id]=new()}},Planning=new(){ImportDelayFactor=2,CadenceSeconds=21600}};
        foreach(string role in new[]{"storage","power","workshop","lamp","housing"})
        {
            var template=new ColonyTemplate {Id=role,Name="Actual fixture "+role,Hash=Hash(role),CraftSha256=Hash("craft"+role),RuntimeCertified=true,
                CertificationEvidence="Explicit domain unit-test provider",BuildFunds=100,LaborFunds=10,LaborSeconds=10,WidthMeters=10,LengthMeters=10,
                MinX=-4,MaxX=4,MinZ=-4,MaxZ=4,ClearanceMetres=1,ExpectedPartCount=1,Homes=role=="housing"?2:0,
                HomeCraftPartIds=role=="housing"?new(){101}:new(),Materials=new(){new(){Resource="MaterialKits",Amount=U}}};
            env.Templates.Add(template);env.Planning.Roles.Add(new(){Role=role,TemplateId=role,MinimumCount=1});
            colony.Plots.Add(new(){Id=Id(),TemplateId=role,TemplateHash=template.Hash,WidthMeters=10,LengthMeters=10,SurveyHash=Hash("plot"+role),
                EvidenceContext=env.ContextKey,ObservedUt=100,SurveyProvenance="Explicit unit-test terrain provider"});
        }
        var policy=new ColonyEconomyPolicy {Id="supply",Body="Minmus",Provider="Finite fixture supplier",SetupFunds=100,SetupTravelSeconds=10,ContractorWorkers=2};
        foreach(string resource in new[]{"Supplies","MaterialKits"})
        {
            policy.Stores.Add(new(){Resource=resource,Capacity=1000*U,UnitMassMicroTonnes=1000,UnitVolumeMilliLiters=5000});
            policy.Suppliers.Add(new(){Id=resource,Resource=resource,DestinationBody="Minmus",FreightPoolId="shared finite fleet",Available=100*U,
                FundsPerUnit=2,FreightFunds=5,TravelSeconds=20,ConcurrentCapacity=1,MassCapacityMicroTonnes=1000*U,VolumeCapacityMilliLiters=1000*U});
        }
        policy.Hash=ColonyEngine.EconomyPolicyHash(policy);env.EconomyPolicies.Add(policy);return(state,env,colony.Id);
    }
    static ColonyCommand Command(ColonyState s,ColonyEnvironment e,string colony,string kind,Dictionary<string,string>? fields=null)=>
        new(){OperationId=Id(),Kind=kind,ColonyId=colony,ExpectedRevision=s.Revision,ContextKey=e.ContextKey,Fields=fields??new()};
    static ColonyState Accept(ColonyState s,ColonyEnvironment e,ColonyCommand c)
    {var result=ColonyEngine.Execute(s,c,e);Assert.True(result.Outcome=="accepted",result.Reason);return result.State;}
    internal static ColonyState Approve(ColonyState s,ColonyEnvironment e,string colony)
    {var q=ColonyEngine.QuoteFoundingPlan(s,colony,e);Assert.True(q.CanApprove,string.Join(" ",q.Blockers));var c=Command(s,e,colony,"approveFoundingPlan");c.QuoteId=q.Id;return Accept(s,e,c);}
    internal static ColonyState Pay(ColonyState s,ColonyEnvironment e)
    {
        var effect=s.Effects.First(x=>x.State=="prepared"&&x.FundsDelta<0);long before=e.AvailableFunds,after=before+effect.FundsDelta;
        s=ColonyEngine.MarkEffectApplying(s,effect.Id,"Explicit test Funding","before="+before);
        s=ColonyEngine.CompleteFundsEffect(s,effect.Id,before,after,e.Ut,"after="+after);e.AvailableFunds=after;return s;
    }
    static ColonyState Advance(ColonyState s,ColonyEnvironment e,double ut)
    {e.Ut=ut;return ColonyEngine.Advance(s,e);}
    internal static ColonyState Stage(ColonyState s,ColonyEnvironment e,string colony)
    {
        var p=e.EconomyPolicies.Single();s=Accept(s,e,Command(s,e,colony,"activateLogistics",new(){["PolicyId"]=p.Id,["PolicyHash"]=p.Hash,["QuotedFunds"]=p.SetupFunds.ToString()}));
        s=Pay(s,e);return Advance(s,e,e.Ut+10);
    }
    internal static ColonyState CompleteOne(ColonyState s,ColonyEnvironment e,string orderId)
    {
        var order=s.Construction.Single(o=>o.Id==orderId);var template=e.Templates.Single(t=>t.Id==order.TemplateId);
        e.ConstructionLaborByOrder.Clear();e.Ut+=10;
        e.ConstructionLaborByOrder[orderId]=new(){ProviderId="Explicit finite paid domain-test labor",PoolId="one finite pool",ContextKey=e.ContextKey,
            EvidenceHash=Hash("labor"+orderId),Workers=1,PoolCapacity=1,ValidFromUt=e.Ut-10,ValidThroughUt=e.Ut,CostIncludedInPaidEscrow=true};
        s=ColonyEngine.Advance(s,e);Assert.Equal("awaitingPlacement",s.Construction.Single(o=>o.Id==orderId).State);
        var intent=new ColonyConstructionPlacementIntent {OperationId=Id(),EffectId=Id(),ContextKey=e.ContextKey,Phase="intent",RequestPayload="Explicit test placement "+orderId,
            PayloadHash=Hash("Explicit test placement "+orderId),RequestFingerprint=Hash("request"+orderId),EscrowWitness=ColonyEngine.ConstructionEscrowWitness(s,orderId),BeforeWitness="Explicit test world"};
        s=ColonyEngine.PrepareConstructionPlacement(s,orderId,e,intent);
        uint part=(uint)(1000+s.Construction.FindIndex(o=>o.Id==orderId));
        s=ColonyEngine.ObserveConstructionPlacement(s,orderId,template,new(){WorldId=s.WorldId,OperationId=intent.OperationId,RequestFingerprint=intent.RequestFingerprint,
            Phase="Anchored",AssemblyAttempted=true,ObservedUt=e.Ut,AfterWitness="Explicit test anchored readback",VesselId=Id(),FoundationId=Id(),Anchored=true,
            CraftToPersistentIds=new(){[101]=part},QualifiedHomePartIds=template.Homes>0?new(){part}:new()});
        s=ColonyEngine.CommissionConstruction(s,orderId,template,new(){Provider="Explicit domain test utility/anchor/home qualifier",Context="unit-test",EvidenceHash=Hash("qualification"),
            ObservedUt=e.Ut,PlacementStable=true,PowerReliable=true,HeatSafe=true,InputsAccessible=true,StaffingQualified=true,BackgroundSupported=true,HousingCertified=template.Homes>0},template.Homes);
        var facility=s.Colonies.Single().Facilities.Single(f=>f.ConstructionOrderId==orderId);
        if(template.Homes>0)e.People.Seats.Add(new(){FacilityId=facility.Id,VesselId=facility.VesselId,PartId=part,Capacity=template.Homes,Current=true,ContextKey=e.ContextKey,HousingCertified=true,UtilitiesQualified=true,Evidence="Explicit test seats"});
        return s;
    }
    [Fact] public void FoundingQuoteIsItemizedAndPreservesAdoptedAssets()
    {
        var(s,e,id)=Fixture();var colony=s.Colonies.Single();var asset=new ColonyFacility {Id=Id(),VesselId=Id(),Name="Existing warehouse"};colony.Facilities.Add(asset);
        e.Planning.Assets.Add(new(){ColonyId=id,FacilityId=asset.Id,Role="storage",ContextKey=e.ContextKey,Witness=Hash("actual tank"),Qualified=true,Name=asset.Name});
        var q=ColonyEngine.QuoteFoundingPlan(s,id,e);Assert.True(q.CanApprove,string.Join(" ",q.Blockers));
        Assert.Equal(4,q.Buildings.Count);Assert.DoesNotContain(q.Buildings,b=>b.Role=="storage");Assert.Contains(q.ExistingAssets,a=>a.Contains("Existing warehouse"));
        Assert.Equal(2*U,q.StartupSupportReserve);Assert.Equal(q.Buildings.Sum(b=>b.Funds)+q.Imports.Sum(i=>i.Funds)+q.StagingFunds,q.TotalFunds);
        Assert.Equal(4*U,q.Materials.Single().Amount);Assert.Contains("Native MKS/WOLF",q.MakeOrImport);Assert.All(q.Buildings,b=>Assert.NotEmpty(b.PlotId));
    }
    [Fact] public void CandidatePackagesRequireBothExplicitDevelopmentAndSandbox()
    {
        var(s,e,id)=Fixture();e.Templates.ForEach(t=>t.RuntimeCertified=false);Assert.False(ColonyEngine.QuoteFoundingPlan(s,id,e).CanApprove);
        e.DevelopmentMode=true;Assert.False(ColonyEngine.QuoteFoundingPlan(s,id,e).CanApprove);
        s.Colonies.Single().Charter.Sandbox=true;Assert.True(ColonyEngine.QuoteFoundingPlan(s,id,e).CanApprove);
    }
    [Fact] public void ExactQuoteRejectsChangedBillAndApprovalReplayCreatesNoExtraClaims()
    {
        var(s,e,id)=Fixture();var q=ColonyEngine.QuoteFoundingPlan(s,id,e);var c=Command(s,e,id,"approveFoundingPlan");c.QuoteId=q.Id;
        e.Templates.Single(t=>t.Id=="housing").BuildFunds++;Assert.Equal("rejected",ColonyEngine.Execute(s,c,e).Outcome);e.Templates.Single(t=>t.Id=="housing").BuildFunds--;
        s=Accept(s,e,c);var replay=ColonyEngine.Execute(ColonyStateCodec.Copy(s),c,e);Assert.Equal("duplicate",replay.Outcome);Assert.Single(replay.State.Plans);
        Assert.Equal(q.TotalFunds,ColonyEngine.PendingCash(s));Assert.All(s.Colonies.Single().Plots,p=>Assert.Equal(c.OperationId,p.ReservedBy));
        Assert.Equal(0,s.Colonies.Single().Stock.Sum(x=>x.Amount));
    }
    [Fact] public void SupportConsumptionAndFreshUtilityEvidenceRetainUnchangedReviewedBill()
    {
        var(s,e,id)=Fixture();s=Stage(s,e,id);var colony=s.Colonies.Single();
        colony.Stock.Single(x=>x.Resource=="Supplies").Amount=10*U;
        colony.Stock.Single(x=>x.Resource=="MaterialKits").Amount=5*U;
        var asset=new ColonyFacility {Id=Id(),VesselId=Id(),Name="Preserved actual warehouse",PartIds=new(){10,11}};colony.Facilities.Add(asset);
        e.Planning.Assets.Add(new(){ColonyId=id,FacilityId=asset.Id,Role="storage",ContextKey=e.ContextKey,Witness=Hash("utility readback at review"),IdentityHash=Hash("parts10,11 provider"),Qualified=true});
        var reviewed=ColonyEngine.QuoteFoundingPlan(s,id,e);Assert.True(reviewed.CanApprove);Assert.Empty(reviewed.Imports);
        colony.Stock.Single(x=>x.Resource=="Supplies").Amount-=U;e.Ut+=10;
        e.Planning.Assets.Single().Witness=Hash("fresh temperature and utility observation");
        var now=ColonyEngine.QuoteFoundingPlan(s,id,e);Assert.Equal(reviewed.Id,now.Id);
        var c=Command(s,e,id,"approveFoundingPlan");c.QuoteId=reviewed.Id;s=Accept(s,e,c);
        Assert.Equal(4*U,s.Plans.Single().Claims.Single().Reserved);Assert.Equal(reviewed.Id,s.Plans.Single().Quote.Id);
        Assert.Equal(9*U,s.Colonies.Single().Stock.Single(x=>x.Resource=="Supplies").Amount);
    }
    [Fact] public void StockDepletionChangesActualProcurementAndRejectsPriorReviewedBill()
    {
        var(s,e,id)=Fixture();s=Stage(s,e,id);s.Colonies.Single().Stock.Single(x=>x.Resource=="MaterialKits").Amount=5*U;
        var q=ColonyEngine.QuoteFoundingPlan(s,id,e);Assert.DoesNotContain(q.Imports,i=>i.Resource=="MaterialKits");
        var c=Command(s,e,id,"approveFoundingPlan");c.QuoteId=q.Id;
        s.Colonies.Single().Stock.Single(x=>x.Resource=="MaterialKits").Amount-=U;
        var now=ColonyEngine.QuoteFoundingPlan(s,id,e);Assert.Equal(U,now.Imports.Single(i=>i.Resource=="MaterialKits").Amount);Assert.NotEqual(q.Id,now.Id);
        Assert.Equal("rejected",ColonyEngine.Execute(s,c,e).Outcome);Assert.Empty(s.Plans);
    }
    [Theory] [InlineData("provider")] [InlineData("member")] [InlineData("qualification")]
    public void PhysicalAssetIdentityAndQualificationChangesRequireReview(string change)
    {
        var(s,e,id)=Fixture();var colony=s.Colonies.Single();
        var asset=new ColonyFacility {Id=Id(),VesselId=Id(),Name="Real registered storage",PartIds=new(){10,11}};colony.Facilities.Add(asset);
        e.Planning.Assets.Add(new(){ColonyId=id,FacilityId=asset.Id,Role="storage",ContextKey=e.ContextKey,Witness=Hash("fresh"),IdentityHash=Hash("members and installed provider"),Qualified=true});
        var q=ColonyEngine.QuoteFoundingPlan(s,id,e);Assert.DoesNotContain(q.Buildings,b=>b.Role=="storage");
        var c=Command(s,e,id,"approveFoundingPlan");c.QuoteId=q.Id;
        if(change=="provider")e.Planning.Assets.Single().IdentityHash=Hash("changed installed provider");
        if(change=="member") { asset.PartIds.Remove(11);e.Planning.Assets.Single().Qualified=false; }
        if(change=="qualification")e.Planning.Assets.Single().Qualified=false;
        Assert.NotEqual(q.Id,ColonyEngine.QuoteFoundingPlan(s,id,e).Id);Assert.Equal("rejected",ColonyEngine.Execute(s,c,e).Outcome);Assert.Empty(s.Plans);
    }
    [Fact] public void FutureSupplierClaimsCannotBeSoldTwiceAcrossColonies()
    {
        var(s,e,id)=Fixture();e.EconomyPolicies.Single().Suppliers.Single(p=>p.Resource=="MaterialKits").Available=6*U;e.EconomyPolicies.Single().Hash=ColonyEngine.EconomyPolicyHash(e.EconomyPolicies.Single());
        s=Approve(s,e,id);var first=s.Plans.Single();Assert.Equal(5*U,ColonyEngine.PlanningSupplierReserved(s,"MaterialKits"));
        var second=ColonyStateCodec.Copy(s).Colonies.Single();second.Id=Id();second.Name="Second site";second.Site.Latitude=20;
        foreach(var p in second.Plots){p.Id=Id();p.ReservedBy="";}s.Colonies.Add(second);e.People.PresentByColony[second.Id]=new();
        var q=ColonyEngine.QuoteFoundingPlan(s,second.Id,e);Assert.False(q.CanApprove);Assert.Contains(q.Blockers,b=>b.Contains("Finite supplier"));Assert.Equal(first.Quote.Id,s.Plans.Single().Quote.Id);
    }
    [Fact] public void FundsStayReservedWhenFutureClaimBecomesRealPaymentAndCargo()
    {
        var(s,e,id)=Fixture();s=Approve(s,e,id);long cost=s.Plans.Single().RemainingFunds;
        s=ColonyEngine.RunPlanning(s,e);Assert.Equal(cost,ColonyEngine.PendingCash(s));Assert.Equal(100,s.Effects.Single().FundsDelta*-1);
        s=Pay(s,e);Assert.Equal(cost-100,ColonyEngine.PendingCash(s));s=Advance(s,e,110);
        s=ColonyEngine.RunPlanning(s,e);var shipment=s.Shipments.Single();Assert.Equal("reserved",shipment.State);Assert.Equal(0,s.Colonies.Single().Stock.Sum(x=>x.Amount));
        s=Pay(s,e);Assert.Equal(110,s.Shipments.Single().DepartUt);Assert.Equal(130,s.Shipments.Single().ArrivalUt);
        s=Advance(s,e,129);Assert.Equal(0,s.Colonies.Single().Stock.Single(x=>x.Resource==shipment.Resource).Amount);
        s=Advance(s,e,130);Assert.Equal(shipment.Amount,s.Colonies.Single().Stock.Single(x=>x.Resource==shipment.Resource).Amount);
        Assert.Equal(shipment.Amount,s.Plans.Single().Claims.Single().Reserved);ColonyStateCodec.Serialize(s);
        long amount=s.Colonies.Single().Stock.Single(x=>x.Resource==shipment.Resource).Amount;s=Advance(ColonyStateCodec.Copy(s),e,140);Assert.Equal(amount,s.Colonies.Single().Stock.Single(x=>x.Resource==shipment.Resource).Amount);
    }
    [Fact] public void ExistingInTransitCargoIsClaimedOnceAndCancellationDoesNotRecallIt()
    {
        var(s,e,id)=Fixture();s=Stage(s,e,id);s=Accept(s,e,Command(s,e,id,"approveTrade",new(){["SupplierId"]="MaterialKits",["AmountMicroUnits"]=(5*U).ToString(),["QuotedFunds"]="15"}));s=Pay(s,e);
        var shipment=s.Shipments.Single();var q=ColonyEngine.QuoteFoundingPlan(s,id,e);Assert.DoesNotContain(q.Imports,i=>i.Resource=="MaterialKits");Assert.Equal(5*U,q.ExistingIncoming.Single().MaterialAmount);
        s=Approve(s,e,id);var cancel=Command(s,e,id,"cancelTrade");cancel.TargetId=shipment.Id;Assert.Equal("rejected",ColonyEngine.Execute(s,cancel,e).Outcome);
        var parentCancel=Command(s,e,id,"cancelColonyPlan");parentCancel.TargetId=s.Plans.Single().Id;s=Accept(s,e,parentCancel);
        s=Advance(s,e,130);Assert.Equal("arrived",s.Shipments.Single().State);Assert.Equal(5*U,s.Colonies.Single().Stock.Single(x=>x.Resource=="MaterialKits").Amount);Assert.Equal(0,ColonyEngine.PlanningCommittedFunds(s));
    }
    [Fact] public void ChangedSupplierTermsHoldUnstartedLoadAndPreserveImmutableQuote()
    {
        var(s,e,id)=Fixture();s=Stage(s,e,id);s=Approve(s,e,id);string quote=s.Plans.Single().Quote.Id;
        s.Suppliers.Single(p=>p.Resource=="MaterialKits").TravelSeconds=200;
        var next=ColonyEngine.RunPlanning(s,e);Assert.Empty(next.Shipments);Assert.Equal(quote,next.Plans.Single().Quote.Id);Assert.Contains("terms changed",next.Plans.Single().Reason);Assert.Equal(s.Plans.Single().RemainingFunds,next.Plans.Single().RemainingFunds);
    }
    [Fact] public void SupplierFleetIsNotMultipliedAndSupportReordersCountInTransitCargo()
    {
        var(s,e,id)=Fixture();s=Stage(s,e,id);s=Accept(s,e,Command(s,e,id,"configureReorderPolicy",new(){["Resource"]="Supplies",["Enabled"]="true",["ReorderPointMicroUnits"]=(2*U).ToString(),["TargetMicroUnits"]=(5*U).ToString(),["CadenceSeconds"]="60"}));
        s.Colonies.Single().Stock.Single(x=>x.Resource=="Supplies").SupportFloor=2*U;
        s=ColonyEngine.RunPlanningPolicies(s,e);Assert.Single(s.Shipments);s=Pay(s,e);
        // At the next review this arrival is deliberately late but already owns
        // capacity. Reorder must not buy a duplicate replacement load.
        s.Shipments.Single().ArrivalUt=1000;e.Ut=170;s=ColonyEngine.Advance(s,e);s=ColonyEngine.RunPlanningPolicies(s,e);Assert.Single(s.Shipments);
        Assert.Contains("in-transit",s.ReorderPolicies.Single().Reason);Assert.Equal(5*U,s.Colonies.Single().Stock.Single(x=>x.Resource=="Supplies").IncomingReserved);
    }
    [Fact] public void ForecastSeparatesReceivablesAndNeverSpendsUnverifiedExports()
    {
        var(s,e,id)=Fixture();s=Stage(s,e,id);s.Colonies.Single().Stock.Single(x=>x.Resource=="Supplies").Amount=U;e.AvailableFunds=100;
        s.Shipments.Add(new(){Id=Id(),ColonyId=id,Kind="export",State="inTransit",Resource="Ore",Amount=U,Funds=100000,DepartUt=110,ArrivalUt=130,TravelSeconds=20});
        var f=ColonyEngine.Forecast(s,id,e);Assert.Equal(100000,f.Receivables);Assert.Equal(0,f.AvailableCash);Assert.True(f.DownsideCash<0);Assert.False(f.Sustainable);Assert.Equal(.5,f.CurrentSupplyDays);
    }
    [Fact] public void FutureCashFloorProtectsReviewedPlanFromUnrelatedPurchase()
    {
        var(s,e,id)=Fixture();s=Stage(s,e,id);s=Approve(s,e,id);e.AvailableFunds=ColonyEngine.PendingCash(s)+s.Colonies.Single().Charter.CashFloor+1;
        var purchase=Command(s,e,id,"approveTrade",new(){["SupplierId"]="Supplies",["AmountMicroUnits"]=U.ToString(),["QuotedFunds"]="7"});
        var rejected=ColonyEngine.Execute(s,purchase,e);Assert.Equal("rejected",rejected.Outcome);Assert.Contains("insufficient cash",rejected.Reason);Assert.Empty(s.Shipments);
    }
    [Fact] public void ExternalUncertaintyStopsBothPlannerAndPolicyPump()
    {
        var(s,e,id)=Fixture();s=Approve(s,e,id);s=ColonyEngine.RunPlanning(s,e);s=ColonyEngine.HoldEffect(s,s.Effects.Single().Id,"uncertain physical/funds boundary");
        Assert.Same(s,ColonyEngine.RunPlanning(s,e));Assert.Same(s,ColonyEngine.RunPlanningPolicies(s,e));Assert.Empty(s.Shipments);
    }
    [Fact] public void SavedClaimTamperingIsRejectedInsteadOfSilentlyRepairingMoney()
    {
        var(s,e,id)=Fixture();s=Approve(s,e,id);var changed=ColonyStateCodec.Copy(s);changed.Plans.Single().RemainingFunds--;
        Assert.Throws<InvalidDataException>(()=>ColonyStateCodec.Serialize(changed));changed=ColonyStateCodec.Copy(s);changed.Plans.Single().Imports[0].Funds++;
        Assert.Throws<InvalidDataException>(()=>ColonyStateCodec.Serialize(changed));
    }
    [Fact] public void CompleteFoundingWorkflowUsesPaidCargoDependenciesAndActualProviderContracts()
    {
        var(s,e,id)=Fixture();var quote=ColonyEngine.QuoteFoundingPlan(s,id,e);s=Approve(s,e,id);
        s=ColonyEngine.RunPlanning(s,e);s=Pay(s,e);s=Advance(s,e,110);
        s=ColonyEngine.RunPlanning(s,e);s=Pay(s,e);s=Advance(s,e,130);
        s=ColonyEngine.RunPlanning(s,e);s=Pay(s,e);s=Advance(s,e,150);
        Assert.Equal(5*U,s.Plans.Single().Claims.Single().Reserved);
        for(int completed=0;completed<5;completed++)
        {
            s=ColonyEngine.RunPlanning(s,e);var child=s.Construction.Single(o=>o.State=="reserved");s=Pay(s,e);
            Assert.All(child.Dependencies,d=>Assert.Equal("operational",s.Construction.Single(o=>o.Id==d).State));
            if(completed<2)Assert.DoesNotContain(s.Construction,o=>o.TemplateId=="housing");
            s=CompleteOne(s,e,child.Id);s=ColonyStateCodec.Copy(s);
        }
        s=ColonyEngine.RunPlanning(s,e);Assert.Equal("complete",s.Plans.Single().State);Assert.Equal("operational",s.Colonies.Single().Status);
        Assert.Equal(quote.TotalFunds,s.Colonies.Single().SpentFunds);Assert.Equal(50000-quote.TotalFunds,e.AvailableFunds);
        Assert.Equal(0,ColonyEngine.PendingCash(s));Assert.All(s.Plans.Single().Claims,c=>Assert.Equal(0,c.Reserved));
        Assert.Equal(0,s.Colonies.Single().Stock.Single(x=>x.Resource=="MaterialKits").Amount);Assert.Equal(2*U,s.Colonies.Single().Stock.Single(x=>x.Resource=="Supplies").Amount);
        Assert.Empty(s.Colonies.Single().Residents);Assert.All(s.Construction,o=>Assert.Equal("operational",o.State));
        var again=ColonyEngine.RunPlanning(s,e);Assert.Equal(s.Construction.Count,again.Construction.Count);Assert.Equal(s.Shipments.Count,again.Shipments.Count);
        Assert.Equal(s.Colonies.Single().SpentFunds,again.Colonies.Single().SpentFunds);Assert.Equal("complete",again.Plans.Single().State);
    }
    internal static (ColonyState state,ColonyEnvironment env,string colony) GrowthFixture()
    {
        var(s,e,id)=Fixture();s=Stage(s,e,id);var colony=s.Colonies.Single();colony.Status="operational";colony.SupportCommissionedUt=e.Ut;colony.SupportAccountedUt=e.Ut;
        colony.SupportPolicyHash=e.Support.PolicyHash;colony.SupportMicroUnitsPerPersonDay=U;
        colony.Stock.Single(x=>x.Resource=="Supplies").Amount=100*U;colony.Stock.Single(x=>x.Resource=="Supplies").SupportFloor=2*U;
        colony.Stock.Single(x=>x.Resource=="MaterialKits").Amount=10*U;
        var home=new ColonyFacility {Id=Id(),VesselId=Id(),Name="Existing certified home",State="operational",CertifiedHomes=2,Qualification=new(){HousingCertified=true}};
        var jobs=new ColonyFacility {Id=Id(),VesselId=Id(),Name="Paid real workshop",State="operational",PartIds=new(){199},RequiredWorkers=4,RequiredTrait="Engineer",
            Qualification=new(){PowerReliable=true,InputsAccessible=true,HeatSafe=true,BackgroundSupported=true}};
        colony.Facilities.Add(home);colony.Facilities.Add(jobs);
        for(int i=0;i<2;i++)colony.Residents.Add(new(){Id=Id(),RosterId="Crew"+i,Name="Crew"+i,Trait="Engineer",HomeFacilityId=home.Id});
        e.People.PresentByColony[id]=new(){"Crew0","Crew1"};e.People.Seats.Add(new(){FacilityId=home.Id,VesselId=home.VesselId,PartId=99,Capacity=2,Current=true,ContextKey=e.ContextKey,HousingCertified=true,UtilitiesQualified=true});
        e.People.Seats.Add(new(){FacilityId=jobs.Id,VesselId=jobs.VesselId,PartId=199,Capacity=4,Current=true,ContextKey=e.ContextKey,WorkSupported=true,UtilitiesQualified=true});
        e.Planning.Assets.Add(new(){ColonyId=id,FacilityId=jobs.Id,Role="workshop",ContextKey=e.ContextKey,Name=jobs.Name,Witness=Hash("actual operational workshop"),Qualified=true});
        return(s,e,id);
    }
    [Fact] public void GrowthNeedsActualJobsPressureSupportAndZeroExportAffordability()
    {
        var(s,e,id)=GrowthFixture();var q=ColonyEngine.QuoteGrowthPlan(s,id,e);Assert.True(q.CanApprove,string.Join(" ",q.Blockers));Assert.Single(q.Buildings);Assert.Equal(4,q.TargetPopulation);
        s.Colonies.Single().Facilities.Single(f=>f.RequiredWorkers>0).RequiredWorkers=0;Assert.False(ColonyEngine.QuoteGrowthPlan(s,id,e).CanApprove);
        s.Colonies.Single().Facilities.Single(f=>f.Name.Contains("workshop")).RequiredWorkers=4;s.Colonies.Single().Stock.Single(x=>x.Resource=="Supplies").Amount=U;
        Assert.False(ColonyEngine.QuoteGrowthPlan(s,id,e).CanApprove);Assert.Equal(2,s.Colonies.Single().Residents.Count);
    }
    [Fact] public void AutomaticGrowthBuildsOneRealHabitatWithoutMintingResidentsAndProtectsHigherReserve()
    {
        var(s,e,id)=GrowthFixture();s.Colonies.Single().Charter.GrowthPolicy="automatic";
        s=ColonyEngine.RunPlanningPolicies(s,e);var plan=s.Plans.Single();Assert.Equal("growth",plan.Quote.Kind);Assert.Equal(4*U,ColonyEngine.PlanningSupportFloor(s,id));
        s=Advance(s,e,e.Ut+1);Assert.Equal(4*U,s.Colonies.Single().Stock.Single(x=>x.Resource=="Supplies").SupportFloor);
        s=ColonyEngine.RunPlanning(s,e);Assert.Single(s.Construction);s=Pay(s,e);s=CompleteOne(s,e,s.Construction.Single().Id);
        s=ColonyEngine.RunPlanning(s,e);Assert.Equal("complete",s.Plans.Single().State);Assert.Equal(4,s.Colonies.Single().Charter.PopulationTarget);Assert.Equal(2,s.Colonies.Single().Residents.Count);
        Assert.Single(s.Plans);Assert.Equal(4,ColonyEngine.QualifiedPlanningHomes(s.Colonies.Single(),e));
    }
    [Fact] public void ApprovalGrowthProducesReviewOnlyUntilExactQuoteIsAccepted()
    {
        var(s,e,id)=GrowthFixture();s=ColonyEngine.RunPlanningPolicies(s,e);Assert.Empty(s.Plans);Assert.Single(s.Colonies.Single().Proposals);Assert.Empty(s.Construction);
        var proposal=s.Colonies.Single().Proposals.Single();var q=ColonyEngine.QuoteGrowthProposal(s,id,proposal.Id,e);var c=Command(s,e,id,"approveGrowthProposal");c.TargetId=proposal.Id;c.QuoteId=q.Id;s=Accept(s,e,c);Assert.Single(s.Plans);Assert.Equal(2,s.Colonies.Single().Residents.Count);
    }
    [Fact] public void RoundRobinAllowsAnotherColonyToProcureWhileFirstWaitsOnDelivery()
    {
        var(s,e,id)=Fixture();s=Approve(s,e,id);string first=s.Plans.Single().Id;
        var second=ColonyStateCodec.Copy(s).Colonies.Single();second.Id=Id();second.Name="Second colony";second.Site.Latitude=20;
        foreach(var p in second.Plots){p.Id=Id();p.ReservedBy="";p.Latitude=20;}s.Colonies.Add(second);e.People.PresentByColony[second.Id]=new();s=Approve(s,e,second.Id);
        string other=s.Plans.Single(p=>p.ColonyId==second.Id).Id;
        s=ColonyEngine.RunPlanning(s,e,other);Assert.Equal("reserved",s.Colonies.Single(c=>c.Id==id).Logistics.State);Assert.Single(s.Effects);
        s=ColonyEngine.RunPlanning(s,e,first);Assert.Equal("reserved",s.Colonies.Single(c=>c.Id==second.Id).Logistics.State);Assert.Equal(2,s.Effects.Count);
        Assert.All(s.Colonies,c=>Assert.Equal(0,c.Stock.Sum(x=>x.Amount)));Assert.Equal(s.Plans.Sum(p=>p.Quote.TotalFunds),ColonyEngine.PendingCash(s));
    }
    [Fact] public void CancellationReleasesOnlyItsSupportFloorAndOwnedMaterialClaims()
    {
        var(s,e,id)=Fixture();s=Stage(s,e,id);var supplies=s.Colonies.Single().Stock.Single(x=>x.Resource=="Supplies");supplies.Amount=10*U;supplies.SupportFloor=U;
        s.Colonies.Single().Stock.Single(x=>x.Resource=="MaterialKits").Amount=10*U;s=Approve(s,e,id);
        Assert.Equal(2*U,s.Colonies.Single().Stock.Single(x=>x.Resource=="Supplies").SupportFloor);Assert.Equal(5*U,s.Colonies.Single().Stock.Single(x=>x.Resource=="MaterialKits").Reserved);
        var c=Command(s,e,id,"cancelColonyPlan");c.TargetId=s.Plans.Single().Id;s=Accept(s,e,c);
        Assert.Equal(U,s.Colonies.Single().Stock.Single(x=>x.Resource=="Supplies").SupportFloor);Assert.Equal(0,s.Colonies.Single().Stock.Single(x=>x.Resource=="MaterialKits").Reserved);
        Assert.Equal(10*U,s.Colonies.Single().Stock.Single(x=>x.Resource=="MaterialKits").Amount);Assert.Equal(0,ColonyEngine.PlanningCommittedFunds(s));
    }
    [Fact] public void ImportBatchBoundAndMissingSurveyRejectApprovalWithoutPartialReservations()
    {
        var(s,e,id)=Fixture();e.EconomyPolicies.Single().Suppliers.ForEach(p=>p.VolumeCapacityMilliLiters=500);e.EconomyPolicies.Single().Hash=ColonyEngine.EconomyPolicyHash(e.EconomyPolicies.Single());
        var q=ColonyEngine.QuoteFoundingPlan(s,id,e);Assert.False(q.CanApprove);Assert.Contains(q.Blockers,b=>b.Contains("64-load"));
        var c=Command(s,e,id,"approveFoundingPlan");c.QuoteId=q.Id;Assert.Equal("rejected",ColonyEngine.Execute(s,c,e).Outcome);Assert.Empty(s.Plans);Assert.Empty(s.Effects);
        var fresh=Fixture();fresh.state.Colonies.Single().Plots.Clear();q=ColonyEngine.QuoteFoundingPlan(fresh.state,fresh.colonyId,fresh.env);
        Assert.False(q.CanApprove);Assert.All(q.Buildings,b=>Assert.Empty(b.PlotId));Assert.Contains(q.Blockers,b=>b.Contains("surveyed plots"));
    }
}
