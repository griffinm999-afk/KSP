using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Expanse.Domain.Colonies;
namespace Expanse.Clock.Tests;
// Trusted domain adapter fixtures only; actual registry/BRP receipts still need
// isolated native acceptance. No physical resource quantities are fabricated.
public sealed class ColonyProductionInventoryTests
{
    const long U=ColonyLimits.Units;
    static string Id()=>Guid.NewGuid().ToString("D");
    static string Hash(string value)=>ColonyStateCodec.Hash(Encoding.UTF8.GetBytes(value));
    internal static ColonyProductionOperationsIntent Operations()=>new(){RegisterCreatedEndpoints=true,AutomaticIntake=true,AutomaticInputRefill=true,Resources=[
        new(){Resource="Fertilizer",InitialReserve=10*U,ReorderEnabled=true,ReorderPoint=10*U,TargetAmount=20*U},
        new(){Resource="Machinery",InitialReserve=10*U,ReorderEnabled=true,ReorderPoint=10*U,TargetAmount=10*U,NativeTargetFraction=.95},
        new(){Resource="Plutonium-238",InitialReserve=U,ReorderEnabled=true,ReorderPoint=U,TargetAmount=2*U,NativeTargetFraction=1}]};
    internal static void AddOperations(ColonyState s,ColonyEnvironment e,ColonyFoundingIntent intent,ColonyProductionOperationsIntent operations)
    {
        intent.Production!.Operations=operations;e.Production.Registry=new(){Ready=true,WorldId=Id(),Witness=Hash("exact registry fixture"),Revision=4};
        foreach(string resource in operations.Resources.Select(r=>r.Resource))
        {
            s.Colonies.Single().Stock.Add(new(){Resource=resource,Capacity=1000*U,Amount=100*U,UnitMassMicroTonnes=1000,UnitVolumeMilliLiters=5000});
            s.Suppliers.Add(new(){Id="production "+resource,Resource=resource,DestinationBody="Minmus",Available=1000*U,FundsPerUnit=2,FreightFunds=5,TravelSeconds=20,
                ConcurrentCapacity=1,MassCapacityMicroTonnes=1000*U,VolumeCapacityMilliLiters=1000*U});
        }
    }
    [Fact] public void ExplicitOperatingBuffersEnterCentralBillOnceAndFiniteImportsFundThem()
    {
        var(s,e,id,intent)=ColonyProductionInvariantTests.Fixture();AddOperations(s,e,intent,Operations());
        foreach(var row in intent.Production!.Operations!.Resources)s.Colonies.Single().Stock.Single(r=>r.Resource==row.Resource).Amount=0;
        var q=ColonyEngine.QuoteFoundingPlan(s,id,e,intent);Assert.True(q.CanApprove,string.Join(" ",q.Blockers));
        foreach(var row in intent.Production.Operations.Resources)
        {Assert.Equal(row.InitialReserve,ColonyEngine.PlanningMaterialRequirements(q).Single(m=>m.Resource==row.Resource).Amount);Assert.Equal(row.InitialReserve,q.Imports.Where(i=>i.Resource==row.Resource).Sum(i=>i.Amount));}
        Assert.Equal(q.Buildings.Sum(b=>b.Funds)+q.Imports.Sum(i=>i.Funds)+q.StagingFunds+q.Residents.Sum(r=>r.Fare)+ColonyEngine.ProductionExtraFunds(q),q.TotalFunds);
        Assert.Empty(s.Plans);Assert.Empty(s.PhysicalTransfers);
    }
    [Fact] public void ScopeCapacityAndRegistryChangesRequireFreshReviewedApproval()
    {
        var(s,e,id,intent)=ColonyProductionInvariantTests.Fixture();AddOperations(s,e,intent,Operations());e.Production.Registry.ColonyEndpoints=127;
        Assert.Contains(ColonyEngine.QuoteFoundingPlan(s,id,e,intent).Blockers,r=>r.Contains("remaining scoped"));
        e.Production.Registry.ColonyEndpoints=126;var q=ColonyEngine.QuoteFoundingPlan(s,id,e,intent);Assert.True(q.CanApprove,string.Join(" ",q.Blockers));
        e.Production.Registry.Revision++;e.Production.Registry.Witness=Hash("different current registry");
        var result=ColonyEngine.Execute(s,new(){OperationId=Id(),Kind="approveFoundingPlan",ColonyId=id,ExpectedRevision=s.Revision,ContextKey=e.ContextKey,QuoteId=q.Id,FoundingIntent=intent},e);
        Assert.Equal("rejected",result.Outcome);Assert.Empty(result.State.Plans);
    }
    [Fact] public void DeclinedOperationsCreateNoEndpointOrRecurringAuthority()
    {
        var(s,e,id,intent)=ColonyProductionInvariantTests.Fixture();intent.Production!.Operations=new();
        var q=ColonyEngine.QuoteFoundingPlan(s,id,e,intent);Assert.True(q.CanApprove,string.Join(" ",q.Blockers));Assert.Null(q.ProductionInvestments.Single().Inventory);
        Assert.Empty(ColonyEngine.ProductionOperatingMaterials(q));
        intent.Production.Operations.AutomaticIntake=true;Assert.Throws<InvalidDataException>(()=>ColonyStateCodec.ValidateProductionIntent(intent.Production));
    }
    [Fact] public void ConflictingDeclinedStartupMachineryCannotBeSilentlyReplaced()
    {
        var(s,e,id,intent)=ColonyProductionInvariantTests.Fixture();AddOperations(s,e,intent,Operations());intent.Startup=new(){MachineryReserveMicroUnits=10*U,ServiceEnabled=false};
        var q=ColonyEngine.QuoteFoundingPlan(s,id,e,intent);Assert.False(q.CanApprove);Assert.Contains(q.Blockers,r=>r.Contains("instructions conflict"));
    }
    static ColonyState ApplyEndpoint(ColonyState s,string planId,string claimId,string specId)
    {
        var plan=s.Plans.Single(p=>p.Id==planId);var item=plan.Quote.ProductionInvestments.Single(i=>i.Id==claimId);var spec=item.Inventory!.Endpoints.Single(e=>e.Id==specId);
        var building=plan.Buildings.Single(b=>b.Id==spec.BuildingId);var order=s.Construction.Single(o=>o.Id==building.OrderId);var facility=s.Colonies.Single().Facilities.Single(f=>f.Id==order.FacilityId);
        // Explicit fake adapter mapping, with exact paid facility IDs. This is
        // kernel lineage testing and not a real craft-marker qualification.
        uint offset=facility.PartIds.Min()-spec.MemberCraftPartIds.Min();uint[] members=spec.MemberCraftPartIds.Select(id=>id+offset).ToArray();return ColonyEngine.MarkProductionRegistrationApplying(s,planId,claimId,specId,facility.Id,facility.VesselId,spec.AnchorCraftPartId+offset,members,4,Hash("before registry"));
    }
    static ColonyState CompleteEndpoint(ColonyState state,string effectId,string hash)
    {var plan=state.Plans.Single();var receipt=plan.Production.SelectMany(c=>c.Inventory!.Endpoints).Single(r=>r.EffectId==effectId);var spec=plan.Quote.ProductionInvestments.SelectMany(i=>i.Inventory!.Endpoints).Single(r=>r.Id==receipt.SpecId);return ColonyEngine.CompleteProductionRegistration(state,effectId,hash,ColonyEngine.ProductionRegistrationAfterWitness(spec,receipt,hash));}
    [Fact] public void InterruptedRegistrationRetainsExactReadbackAndCannotRepeat()
    {
        var(s,e,p,c,_)=ColonyProductionLifecycleTests.Ready(Operations());string spec=s.Plans.Single().Quote.ProductionInvestments.Single().Inventory!.Endpoints[0].Id;
        long before=s.Colonies.Single().Stock.Single(r=>r.Resource=="Supplies").Amount;
        s=ApplyEndpoint(s,p,c,spec);string effect=s.Plans.Single().Production.Single().Inventory!.Endpoints[0].EffectId;
        s=ColonyStateCodec.Deserialize(ColonyStateCodec.Serialize(s));s=ColonyEngine.HoldEffect(s,effect,"Unknown registration outcome on reload.");
        Assert.Throws<InvalidDataException>(()=>ApplyEndpoint(s,p,c,spec));
        s=CompleteEndpoint(s,effect,Hash("actual exact membership"));
        Assert.Equal("applied",s.Plans.Single().Production.Single().Inventory!.Endpoints[0].State);Assert.Equal(before,s.Colonies.Single().Stock.Single(r=>r.Resource=="Supplies").Amount);
        var malformed=ColonyStateCodec.Copy(s);malformed.Effects.Single(f=>f.Id==effect).State="held";Assert.Throws<InvalidDataException>(()=>ColonyStateCodec.Serialize(malformed));
    }
    [Fact] public void CompletedPaidHardwareReleasesOnlyOwnedOperatingClaimsBeforeOutput()
    {
        var(s,e,p,c,_)=ColonyProductionLifecycleTests.Ready(Operations());var plan=s.Plans.Single();var claim=plan.Production.Single();var item=plan.Quote.ProductionInvestments.Single();
        var totals=s.Colonies.Single().Stock.ToDictionary(r=>r.Resource,r=>r.Amount);var method=typeof(ColonyEngine).GetMethod("ReleaseProductionOperatingReserves",BindingFlags.NonPublic|BindingFlags.Static)!;
        Assert.True((bool)method.Invoke(null,[s,plan,claim,item])!);ColonyStateCodec.Validate(s);
        Assert.True(claim.Inventory!.ReservesReleased);Assert.Equal("planned",claim.State);Assert.Empty(claim.OutputWitness);
        foreach(var material in item.Inventory!.ReserveMaterials){Assert.Equal(0,plan.Claims.Single(r=>r.Resource==material.Resource).Reserved);Assert.Equal(totals[material.Resource],s.Colonies.Single().Stock.Single(r=>r.Resource==material.Resource).Amount);}
    }
    [Fact] public void NullOperationsAndInventoryKeepIndependentOldQuoteProjectionHash()
    {
        var(s,e,id,intent)=ColonyProductionInvariantTests.Fixture();var q=ColonyEngine.QuoteFoundingPlan(s,id,e,intent);
        var projection=JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(q))!.AsObject();
        projection["Id"]="";projection["CanApprove"]=false;projection["Blockers"]=new JsonArray();projection["ExistingAssets"]=new JsonArray();projection["Rationale"]="";projection["MakeOrImport"]=new ColonyPlanningQuote().MakeOrImport;projection["Forecast"]=JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(Activator.CreateInstance(q.Forecast.GetType())));
        projection.Remove("MakeImportComparisons");projection.Remove("Residents");projection.Remove("StartupPolicies");projection["FoundingIntent"]!["Production"]!.AsObject().Remove("Operations");
        foreach(var investment in projection["ProductionInvestments"]!.AsArray())investment!.AsObject().Remove("Inventory");
        foreach(var import in projection["Imports"]!.AsArray())import!.AsObject().Remove("SubstitutedLocallyAmount");
        string Canonical(JsonNode? n)=>n is JsonObject obj?"{"+string.Join(",",obj.OrderBy(p=>p.Key,StringComparer.Ordinal).Select(p=>System.Text.Json.JsonSerializer.Serialize(p.Key)+":"+Canonical(p.Value)))+"}":n is JsonArray arr?"["+string.Join(",",arr.Select(Canonical))+"]":n?.ToJsonString()??"null";
        Assert.Equal(Hash(Canonical(projection)),q.Id);
    }
    static (ColonyState State,ColonyEnvironment Env,ColonyProductionEndpointReceipt Farm) OperatingReady(int packages=1)
    {
        var(s,e,p,c,_)=ColonyProductionLifecycleTests.Ready(Operations(),packages);
        var specs=s.Plans.Single().Quote.ProductionInvestments.SelectMany(i=>i.Inventory!.Endpoints.Select(spec=>(i.Id,spec.Id))).ToArray();
        foreach(var (investment,spec) in specs){s=ApplyEndpoint(s,p,investment,spec);var receipt=s.Plans.Single().Production.SelectMany(c=>c.Inventory!.Endpoints).Single(r=>r.SpecId==spec);s=CompleteEndpoint(s,receipt.EffectId,Hash(spec+" exact members"));}
        var plan=s.Plans.Single();var claim=plan.Production.First();
        foreach(var owned in plan.Production)typeof(ColonyEngine).GetMethod("ReleaseProductionOperatingReserves",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,[s,plan,owned,plan.Quote.ProductionInvestments.Single(i=>i.Id==owned.Id)]);
        Assert.True((bool)typeof(ColonyEngine).GetMethod("ApplyProductionOperatingPolicies",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,[s,plan,e])!);ColonyStateCodec.Validate(s);
        return(s,e,claim.Inventory!.Endpoints.Single(r=>r.SpecId.EndsWith(":farm")));
    }
    static ColonyLocalStock SupplyTank(ColonyState s,ColonyEnvironment e,ColonyProductionEndpointReceipt farm)=>new(){Id="exact paid output tank",ColonyId=s.Colonies.Single().Id,FacilityId=farm.FacilityId,VesselId=farm.VesselId,PartId=106,PartName="Paid Supplies output",DepotId=farm.DepotId,
        Resource="Supplies",Amount=U,Capacity=600*U,Current=true,CanApply=true,WarehouseEnabled=true,FlowAllowed=true,WithinRange=true,Provider="Explicit inventory fixture",MembershipHash=farm.MembershipHash,AccessWitness=Hash("current output access"),ContextKey=e.ContextKey,ObservedUt=e.Ut};
    [Fact] public void AutomaticIntakeUsesExactReceiptAndCreditsOnlyAfterConservedPhysicalReadback()
    {
        var(s,e,farm)=OperatingReady();e.Planning.LocalStocks.Add(SupplyTank(s,e,farm));long before=s.Colonies.Single().Stock.Single(r=>r.Resource=="Supplies").Amount;
        s=ColonyEngine.RunProductionOperatingTransfers(s,e);var transfer=Assert.Single(s.PhysicalTransfers);Assert.Equal("toColony",transfer.Direction);Assert.Equal(U,transfer.Amount);Assert.Equal(before,s.Colonies.Single().Stock.Single(r=>r.Resource=="Supplies").Amount);
        s=ColonyEngine.HoldPreparedPhysicalTransfer(s,transfer.Id,"Explicit inventory fixture","exact physical before","intended physical after",1,0);Assert.Equal(before,s.Colonies.Single().Stock.Single(r=>r.Resource=="Supplies").Amount);
        Assert.Same(s,ColonyEngine.RunProductionOperatingTransfers(s,e));s=ColonyEngine.CompletePreparedPhysicalTransfer(s,transfer.Id,e.Ut,"actual physical after");Assert.Equal(before+U,s.Colonies.Single().Stock.Single(r=>r.Resource=="Supplies").Amount);
    }
    [Fact] public void ReplacedOrUnreviewedEndpointCannotSupplyAutomaticStockCredit()
    {
        var(s,e,farm)=OperatingReady();var tank=SupplyTank(s,e,farm);tank.MembershipHash=Hash("different selected members");e.Planning.LocalStocks.Add(tank);
        Assert.Same(s,ColonyEngine.RunProductionOperatingTransfers(s,e));tank.MembershipHash=farm.MembershipHash;tank.DepotId=Id();Assert.Same(s,ColonyEngine.RunProductionOperatingTransfers(s,e));Assert.Empty(s.PhysicalTransfers);
    }
    [Fact] public void ReviewedFuelRefillUsesOwnedServiceEscrowAndNeverMintsOrRestartsFuel()
    {
        var(s,e,farm)=OperatingReady();e.Services.Targets.Add(new(){ColonyId=s.Colonies.Single().Id,FacilityId=farm.FacilityId,PartId=104,PartName="Paid Ranger local tank",DepotId=farm.DepotId,SourceResource="Plutonium-238",DestinationResource="Plutonium-238",Amount=19*U,Capacity=20*U,
            Current=true,CanApply=true,QualifiedWorker=true,WorkerWitness="Explicit current Engineer in actual workshop fixture",Provider="Explicit inventory fixture",ContextKey=e.ContextKey,ObservedUt=e.Ut});
        long before=s.Colonies.Single().Stock.Single(r=>r.Resource=="Plutonium-238").Amount;s=ColonyEngine.RunProductionOperatingTransfers(s,e);var op=Assert.Single(s.ServiceOperations);Assert.Equal(U,op.Amount);
        Assert.Equal(before,s.Colonies.Single().Stock.Single(r=>r.Resource=="Plutonium-238").Amount);s=ColonyEngine.HoldPreparedService(s,op.Id,"Explicit inventory fixture","exact tank 19→20");Assert.Equal(before-U,s.Colonies.Single().Stock.Single(r=>r.Resource=="Plutonium-238").Amount);
        s=ColonyEngine.CompletePreparedService(s,op.Id,e.Ut,"actual tank 20");Assert.Equal("complete",s.ServiceOperations.Single().State);Assert.Equal(before-U,s.Colonies.Single().Stock.Single(r=>r.Resource=="Plutonium-238").Amount);Assert.Empty(s.PhysicalTransfers);
    }
    [Fact] public void AppliedRegistrationReceiptSurvivesTerminalCompaction()
    {
        var(s,e,p,c,_)=ColonyProductionLifecycleTests.Ready(Operations());var specs=s.Plans.Single().Quote.ProductionInvestments.Single().Inventory!.Endpoints.Select(r=>r.Id).ToArray();
        s=ApplyEndpoint(s,p,c,specs[0]);string retained=s.Plans.Single().Production.Single().Inventory!.Endpoints[0].EffectId;s=CompleteEndpoint(s,retained,Hash("members"));
        string ordinary=Id();s.Effects.Insert(0,new(){Id=ordinary,OperationId=ordinary,ColonyId=s.Colonies.Single().Id,TargetId=ordinary,Kind="ordinary completed fixture",State="applied"});
        while(s.Effects.Count<ColonyLimits.Effects){string id=Id();s.Effects.Add(new(){Id=id,OperationId=id,ColonyId=s.Colonies.Single().Id,TargetId=id,Kind="ordinary terminal fixture",State="applied"});}ColonyStateCodec.Validate(s);
        s=ApplyEndpoint(s,p,c,specs[1]);Assert.Contains(s.Effects,r=>r.Id==retained);Assert.DoesNotContain(s.Effects,r=>r.Id==ordinary);Assert.Equal(ColonyLimits.Effects,s.Effects.Count);ColonyStateCodec.Validate(s);
    }
    [Fact] public void FullPendingTransferLedgerDefersAutomaticWorkWithoutGlobalHold()
    {
        var(s,e,farm)=OperatingReady();e.Planning.LocalStocks.Add(SupplyTank(s,e,farm));var facility=s.Colonies.Single().Facilities.Single(f=>f.Id==farm.FacilityId);
        for(int index=0;index<256;index++)
        {
            uint part=(uint)(20000+index);facility.PartIds.Add(part);string id=Id();s.PhysicalTransfers.Add(new(){Id=id,ColonyId=s.Colonies.Single().Id,FacilityId=facility.Id,LocalStockId="pending tank "+index,DepotId=farm.DepotId,PartId=part,Resource="Supplies",Amount=U,CreatedUt=e.Ut,Provider="Explicit physical fixture",MembershipHash=Hash("fixture selected member"),AccessWitness=Hash("fixture access")});
            s.Effects.Add(new(){Id=Id(),OperationId=id,ColonyId=s.Colonies.Single().Id,TargetId=id,Kind="physicalTransfer"});
        }
        s.Colonies.Single().Stock.Single(r=>r.Resource=="Supplies").IncomingReserved=256*U;ColonyStateCodec.Validate(s);
        Assert.Same(s,ColonyEngine.RunProductionOperatingTransfers(s,e));Assert.DoesNotContain(s.Effects,f=>f.State=="held");
    }
    [Fact] public void TerminalPlanTransferLineageIsNeverEvictedForAutomaticRoom()
    {
        var(s,e,farm)=OperatingReady();e.Planning.LocalStocks.Add(SupplyTank(s,e,farm));var facility=s.Colonies.Single().Facilities.Single(f=>f.Id==farm.FacilityId);string retained="";
        for(int index=0;index<256;index++)
        {
            uint part=(uint)(20000+index);facility.PartIds.Add(part);string id=Id();if(index==0)retained=id;
            s.PhysicalTransfers.Add(new(){Id=id,ColonyId=s.Colonies.Single().Id,FacilityId=facility.Id,LocalStockId="terminal tank "+index,DepotId=farm.DepotId,PartId=part,Resource="Supplies",Amount=U,CreatedUt=e.Ut,CompletedUt=e.Ut,Provider="Explicit physical fixture",MembershipHash=Hash("fixture selected member"),AccessWitness=Hash("fixture access"),
                State="complete",PhysicalBefore=1,PhysicalAfter=0,BeforeWitness="exact before fixture",AfterWitness="exact after fixture",PlanId=index==0?s.Plans.Single().Id:""});
        }
        ColonyStateCodec.Validate(s);var next=ColonyEngine.RunProductionOperatingTransfers(s,e);Assert.NotSame(s,next);Assert.Contains(next.PhysicalTransfers,r=>r.Id==retained);Assert.Equal(256,next.PhysicalTransfers.Count);ColonyStateCodec.Validate(next);
    }
    [Fact] public void PaidInitialContentsRetainImportOriginWithoutChangingOwnedStock()
    {
        var(s,e,farm)=OperatingReady();var receipt=s.Plans.Single().Production.Single().Inventory!.Endpoints.Single(r=>r.EffectId==farm.EffectId);long before=s.Colonies.Single().Stock.Single(r=>r.Resource=="Supplies").Amount;
        s=ColonyEngine.RecordProductionInitialProvenance(s,receipt.EffectId,[new(){ColonyId=s.Colonies.Single().Id,FacilityId=receipt.FacilityId,PartId=106,Resource="Supplies",HadImportedStock=true,ImportedAttribution=120*U,LastPhysicalAmount=120,Witness=receipt.AfterWitness}]);
        Assert.Equal(before,s.Colonies.Single().Stock.Single(r=>r.Resource=="Supplies").Amount);Assert.True(s.PhysicalLots.Single().HadImportedStock);Assert.Equal(120*U,s.PhysicalLots.Single().ImportedAttribution);
        var local=SupplyTank(s,e,farm);local.Amount=120*U;e.Planning.LocalStocks.Add(local);s=ColonyEngine.RunProductionOperatingTransfers(s,e);var op=s.PhysicalTransfers.Single();s=ColonyEngine.HoldPreparedPhysicalTransfer(s,op.Id,"Explicit physical fixture","exact 120 before","exact zero after",120,0);s=ColonyEngine.CompletePreparedPhysicalTransfer(s,op.Id,e.Ut,"exact actual zero");Assert.Equal(120*U,s.Colonies.Single().Stock.Single(r=>r.Resource=="Supplies").ImportedAmount);
    }
    [Fact] public void TwelveContinuouslyProducingTanksCannotStarveLastFacilityOrDueMachineryService()
    {
        var(s,e,firstFarm)=OperatingReady(4);var colony=s.Colonies.Single();
        foreach(string resource in new[]{"Water","Substrate"})colony.Stock.Add(new(){Resource=resource,Capacity=1000*U});
        var plan=s.Plans.Single();foreach(var claim in plan.Production)foreach(var receipt in claim.Inventory!.Endpoints)
        {
            bool farm=receipt.SpecId.EndsWith(":farm");foreach(string resource in farm?new[]{"Supplies"}:new[]{"Water","Substrate"})
            {
                uint part=receipt.AnchorPartId+(farm?6u:resource=="Water"?13u:0u);
                e.Planning.LocalStocks.Add(new(){Id="physical output "+part+resource,ColonyId=colony.Id,FacilityId=receipt.FacilityId,VesselId=receipt.VesselId,PartId=part,PartName="Continuous native output fixture",DepotId=receipt.DepotId,Resource=resource,Amount=U,Capacity=600*U,
                    Current=true,CanApply=true,WarehouseEnabled=true,FlowAllowed=true,WithinRange=true,Provider="Explicit physical fixture",MembershipHash=receipt.MembershipHash,AccessWitness=Hash("current output"),ContextKey=e.ContextKey,ObservedUt=e.Ut});
            }
        }
        e.Services.Targets.Add(new(){ColonyId=colony.Id,FacilityId=firstFarm.FacilityId,PartId=firstFarm.AnchorPartId,PartName="Actual Machinery fixture",DepotId=firstFarm.DepotId,SourceResource="Machinery",DestinationResource="Machinery",Capacity=100*U,Current=true,CanApply=true,QualifiedWorker=true,WorkerWitness="Actual workshop Engineer fixture",Provider="Explicit physical fixture",ContextKey=e.ContextKey,ObservedUt=e.Ut});
        var visited=new HashSet<string>();bool serviceVisited=false;
        for(int tick=0;tick<26;tick++)
        {
            foreach(var tank in e.Planning.LocalStocks){tank.Amount=U;tank.ObservedUt=e.Ut;}foreach(var target in e.Services.Targets)target.ObservedUt=e.Ut;
            s=ColonyEngine.RunProductionOperatingTransfers(s,e);
            var physical=s.PhysicalTransfers.LastOrDefault(o=>o.State=="reserved");
            if(physical!=null){visited.Add(physical.LocalStockId);s=ColonyEngine.HoldPreparedPhysicalTransfer(s,physical.Id,"Explicit physical fixture","exact physical before","exact physical after",1,0);s=ColonyEngine.CompletePreparedPhysicalTransfer(s,physical.Id,e.Ut,"exact actual physical after");}
            var service=s.ServiceOperations.LastOrDefault(o=>o.State=="reserved");
            if(service!=null){serviceVisited=true;s=ColonyEngine.HoldPreparedService(s,service.Id,"Explicit physical fixture","actual Machinery before");s=ColonyEngine.CompletePreparedService(s,service.Id,e.Ut,"actual Machinery after");e.Services.Targets.Single().Amount+=service.Amount;}
            e.Ut+=.5;s=ColonyEngine.Advance(s,e);
        }
        Assert.Equal(12,visited.Count);Assert.True(serviceVisited,"The due real Machinery service must share the scheduling cursor with physical work.");Assert.Contains(e.Planning.LocalStocks.Last().Id,visited);
        var saved=ColonyStateCodec.Deserialize(ColonyStateCodec.Serialize(s));Assert.Equal(s.Journal.Last(j=>j.Kind=="productionTankScheduled").OperationId,saved.Journal.Last(j=>j.Kind=="productionTankScheduled").OperationId);
    }
}
