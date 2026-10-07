using System.Text;
using System.Text.Json.Nodes;
using Expanse.Domain.Colonies;

namespace Expanse.Clock.Tests;

// Actual quantities here are explicit provider-contract fixtures. Native KSP
// qualification remains separate from these conserved domain transitions.
public sealed class ColonyPhysicalProcurementTests
{
    const long U=ColonyLimits.Units;
    static string Id()=>Guid.NewGuid().ToString("D");
    static string Hash(string text)=>ColonyStateCodec.Hash(Encoding.UTF8.GetBytes(text));
    static (ColonyState s,ColonyEnvironment e,ColonyLocalStock local) Fixture(long owned=10*U,long imported=0,string resource="MaterialKits")
    {
        var s=ColonyEngine.Create(Id(),100);var c=new ColonyRecord {Id=Id(),Name="Physical test",FoundedUt=100,SupportAccountedUt=100,Site=new(){Body="Minmus"},Charter=new(){PopulationTarget=0,ResidentLimit=12,ReserveDays=1,SpendingLimit=10000,FoundingBudget=10000}};
        c.Stock.Add(new(){Resource=resource,Amount=owned,ImportedAmount=imported,Capacity=100*U});var f=new ColonyFacility {Id=Id(),VesselId=Id(),Name="Adopted real factory",PartIds=new(){7}};c.Facilities.Add(f);s.Colonies.Add(c);
        var e=new ColonyEnvironment {WorldId=s.WorldId,ContextKey="explicit physical test provider",Ut=100,AvailableFunds=10000,People=new(){PresenceComplete=true,PresentByColony=new(){[c.Id]=new()}}};
        var local=Local(c,f,e,resource);e.Planning.LocalStocks.Add(local);ColonyStateCodec.Serialize(s);return(s,e,local);
    }
    static ColonyLocalStock Local(ColonyRecord c,ColonyFacility f,ColonyEnvironment e,string resource)=>new() {Id=Id(),ColonyId=c.Id,FacilityId=f.Id,VesselId=f.VesselId,PartId=f.PartIds[0],PartName="Registered warehouse",DepotId="actual depot",Resource=resource,Amount=20*U,Capacity=100*U,Current=true,CanApply=true,WarehouseEnabled=true,FlowAllowed=true,WithinRange=true,Provider="Explicit exact physical test provider",ProviderVersion="test",MembershipHash=Hash("members"),AccessWitness=Hash("access"),ContextKey=e.ContextKey,ObservedUt=e.Ut};
    static ColonyCommand Command(ColonyState s,ColonyEnvironment e,string c,string kind)=>new(){OperationId=Id(),ColonyId=c,ContextKey=e.ContextKey,ExpectedRevision=s.Revision,Kind=kind};
    static (ColonyState s,ColonyCommand command) Reserve(ColonyState s,ColonyEnvironment e,ColonyLocalStock local,string direction,long amount)
    {
        var q=ColonyEngine.QuotePhysicalTransfer(s,local.ColonyId,local.Id,direction,amount,e);Assert.True(q.CanApprove,q.Reason);
        var command=Command(s,e,local.ColonyId,"transferColonyStock");command.TargetId=local.Id;command.QuoteId=q.Id;command.Fields=new(){["Direction"]=direction,["AmountMicroUnits"]=amount.ToString()};
        var result=ColonyEngine.Execute(s,command,e);Assert.Equal("accepted",result.Outcome);return(result.State,command);
    }
    static ColonyState Finish(ColonyState s,ColonyEnvironment e,ColonyLocalStock local,string operationId)
    {
        var op=s.PhysicalTransfers.Single(o=>o.Id==operationId);double before=local.Amount/(double)U,after=op.Direction=="toColony"?before-op.Amount/(double)U:before+op.Amount/(double)U;
        s=ColonyEngine.HoldPreparedPhysicalTransfer(s,op.Id,local.Provider,"exact before="+before,"exact after="+after,before,after);
        s=ColonyEngine.CompletePreparedPhysicalTransfer(s,op.Id,e.Ut,"exact observed after="+after);local.Amount=(long)Math.Floor(after*U);return s;
    }
    [Fact] public void InboundReservesCapacityThenCreditsOnlyOneExactReceipt()
    {
        var(s,e,l)=Fixture(0);var before=s.Revision;(s,var command)=Reserve(s,e,l,"toColony",3*U);
        Assert.Equal(0,s.Colonies.Single().Stock.Single().Amount);Assert.Equal(3*U,s.Colonies.Single().Stock.Single().IncomingReserved);
        s=Finish(s,e,l,command.OperationId);Assert.Equal(3*U,s.Colonies.Single().Stock.Single().Amount);Assert.Equal(0,s.Colonies.Single().Stock.Single().IncomingReserved);Assert.Equal(17*U,l.Amount);
        var replay=ColonyEngine.Execute(ColonyStateCodec.Copy(s),command,e);Assert.Equal("duplicate",replay.Outcome);Assert.Equal(3*U,replay.State.Colonies.Single().Stock.Single().Amount);
        Assert.Same(s,ColonyEngine.CompletePreparedPhysicalTransfer(s,command.OperationId,e.Ut,"same exact receipt"));Assert.True(s.Revision>before);
    }
    [Fact] public void OutboundHoldEscrowsOwnedStockAndConfirmedRollbackUsesPriorReservation()
    {
        var(s,e,l)=Fixture(10*U,4*U);(s,var command)=Reserve(s,e,l,"toPhysical",3*U);var reserved=s;
        var held=ColonyEngine.HoldPreparedPhysicalTransfer(s,command.OperationId,l.Provider,"before","after",20,23);
        Assert.Equal(7*U,held.Colonies.Single().Stock.Single().Amount);Assert.Equal(U,held.Colonies.Single().Stock.Single().ImportedAmount);Assert.Equal(0,held.Colonies.Single().Stock.Single().Reserved);
        held=ColonyStateCodec.Copy(held);Assert.Equal("held",held.Effects.Single().State);
        var cancel=Command(held,e,l.ColonyId,"cancelPhysicalTransfer");cancel.TargetId=command.OperationId;Assert.Equal("rejected",ColonyEngine.Execute(held,cancel,e).Outcome);
        Assert.Throws<InvalidDataException>(()=>ColonyEngine.HoldPreparedPhysicalTransfer(held,command.OperationId,l.Provider,"again","again",20,23));
        var rolled=ColonyEngine.RejectPreparedPhysicalTransfer(reserved,command.OperationId,e.Ut,"exact original rollback");Assert.Equal(10*U,rolled.Colonies.Single().Stock.Single().Amount);Assert.Equal(4*U,rolled.Colonies.Single().Stock.Single().ImportedAmount);Assert.Equal(0,rolled.Colonies.Single().Stock.Single().Reserved);
    }
    [Fact] public void WarehouseAccessAndNativeReserveAreMandatory()
    {
        var(s,e,l)=Fixture();l.NativeInput=true;l.PhysicalReserve=15*U;
        Assert.False(ColonyEngine.QuotePhysicalTransfer(s,l.ColonyId,l.Id,"toColony",6*U,e).CanApprove);
        Assert.True(ColonyEngine.QuotePhysicalTransfer(s,l.ColonyId,l.Id,"toColony",5*U,e).CanApprove);
        l.CanApply=false;l.WarehouseEnabled=false;Assert.False(ColonyEngine.QuotePhysicalTransfer(s,l.ColonyId,l.Id,"toColony",U,e).CanApprove);
        l.WarehouseEnabled=true;l.FlowAllowed=false;Assert.False(ColonyEngine.QuotePhysicalTransfer(s,l.ColonyId,l.Id,"toPhysical",U,e).CanApprove);
        l.FlowAllowed=true;l.WithinRange=false;Assert.False(ColonyEngine.QuotePhysicalTransfer(s,l.ColonyId,l.Id,"toPhysical",U,e).CanApprove);
    }
    [Fact] public void SupportFloorAndReceivingShipmentsRemainProtected()
    {
        var(s,e,l)=Fixture(10*U,0,"Supplies");s.Colonies.Single().Stock.Single().SupportFloor=8*U;
        Assert.False(ColonyEngine.QuotePhysicalTransfer(s,l.ColonyId,l.Id,"toPhysical",3*U,e).CanApprove);
        Assert.True(ColonyEngine.QuotePhysicalTransfer(s,l.ColonyId,l.Id,"toPhysical",2*U,e).CanApprove);
        s.Colonies.Single().Stock.Single().Capacity=11*U;Assert.False(ColonyEngine.QuotePhysicalTransfer(s,l.ColonyId,l.Id,"toColony",2*U,e).CanApprove);
    }
    [Fact] public void StaleExactQuoteAndReusedOperationPayloadCannotMutateStock()
    {
        var(s,e,l)=Fixture();var q=ColonyEngine.QuotePhysicalTransfer(s,l.ColonyId,l.Id,"toColony",U,e);var command=Command(s,e,l.ColonyId,"transferColonyStock");command.TargetId=l.Id;command.QuoteId=q.Id;command.Fields=new(){["Direction"]="toColony",["AmountMicroUnits"]=U.ToString()};
        l.Amount++;Assert.Equal("rejected",ColonyEngine.Execute(s,command,e).Outcome);l.Amount--;(s,command)=Reserve(s,e,l,"toColony",U);
        command.Fields["AmountMicroUnits"]=(2*U).ToString();Assert.Equal("rejected",ColonyEngine.Execute(s,command,e).Outcome);Assert.Equal(U,s.Colonies.Single().Stock.Single().IncomingReserved);
    }
    [Fact] public void ImportedPhysicalReturnCannotBecomeLocalProduction()
    {
        var(s,e,l)=Fixture(10*U,5*U);(s,var outbound)=Reserve(s,e,l,"toPhysical",3*U);s=Finish(s,e,l,outbound.OperationId);
        Assert.Equal(3*U,s.PhysicalLots.Single().ImportedAttribution);Assert.True(s.PhysicalLots.Single().HadImportedStock);
        (s,var inbound)=Reserve(s,e,l,"toColony",3*U);s=Finish(s,e,l,inbound.OperationId);
        Assert.Equal(10*U,s.Colonies.Single().Stock.Single().Amount);Assert.Equal(5*U,s.Colonies.Single().Stock.Single().ImportedAmount);Assert.Equal(20*U,l.Amount);
    }
    [Fact] public void NativeDepletionThenRefillKeepsStickyImportedOrigin()
    {
        var(s,e,l)=Fixture(10*U,5*U);(s,var outbound)=Reserve(s,e,l,"toPhysical",3*U);s=Finish(s,e,l,outbound.OperationId);
        l.Amount=2*U; // Unobserved native consumption, then later production.
        (s,var inbound)=Reserve(s,e,l,"toColony",2*U);s=Finish(s,e,l,inbound.OperationId);Assert.Equal(2*U,s.PhysicalTransfers.Last().ImportedAttribution);Assert.True(s.PhysicalLots.Single().ProvenanceUncertain);
        s=ColonyStateCodec.Copy(s);l.Amount=7*U;(s,var later)=Reserve(s,e,l,"toColony",4*U);s=Finish(s,e,l,later.OperationId);
        Assert.Equal(4*U,s.PhysicalTransfers.Last().ImportedAttribution);Assert.True(s.PhysicalLots.Single().HadImportedStock);Assert.True(s.PhysicalLots.Single().ProvenanceUncertain);
    }
    [Fact] public void NativeOutputWithoutImportHistoryIsActualLocalStock()
    {
        var(s,e,l)=Fixture(0);(s,var first)=Reserve(s,e,l,"toColony",U);s=Finish(s,e,l,first.OperationId);l.Amount+=5*U;
        (s,var next)=Reserve(s,e,l,"toColony",2*U);s=Finish(s,e,l,next.OperationId);Assert.Equal(3*U,s.Colonies.Single().Stock.Single().Amount);Assert.Equal(0,s.Colonies.Single().Stock.Single().ImportedAmount);Assert.False(s.PhysicalLots.Single().HadImportedStock);
    }
    [Fact] public void UncertainInboundReloadCannotCreditCapacityOrRepeatDebit()
    {
        var(s,e,l)=Fixture(0);(s,var command)=Reserve(s,e,l,"toColony",U);s=ColonyEngine.HoldPreparedPhysicalTransfer(s,command.OperationId,l.Provider,"before","intended after",20,19);s=ColonyStateCodec.Copy(s);
        Assert.Equal(0,s.Colonies.Single().Stock.Single().Amount);Assert.Equal(U,s.Colonies.Single().Stock.Single().IncomingReserved);Assert.Same(s,ColonyEngine.RunPhysicalProcurementPolicies(s,e));
        var newCommand=Command(s,e,l.ColonyId,"configurePhysicalProcurement");newCommand.Fields["Enabled"]="true";Assert.Equal("rejected",ColonyEngine.Execute(s,newCommand,e).Outcome);
    }
    [Fact] public void DuplicateTankClaimsAcrossColoniesFailValidation()
    {
        var(s,e,l)=Fixture();(s,var command)=Reserve(s,e,l,"toColony",U);var second=new ColonyRecord {Id=Id(),Name="Other colony",FoundedUt=100,SupportAccountedUt=100,Site=new(){Body="Minmus",Latitude=10},Charter=new(){PopulationTarget=0}};
        second.Stock.Add(new(){Resource=l.Resource,Capacity=100*U,IncomingReserved=U});second.Facilities.Add(new(){Id=Id(),VesselId=Id(),PartIds=new(){7}});s.Colonies.Add(second);
        var op=new ColonyPhysicalTransfer {Id=Id(),ColonyId=second.Id,FacilityId=second.Facilities[0].Id,LocalStockId=Id(),DepotId=l.DepotId,PartId=7,Resource=l.Resource,Amount=U,Provider=l.Provider,MembershipHash=l.MembershipHash,AccessWitness=l.AccessWitness,CreatedUt=100};s.PhysicalTransfers.Add(op);
        s.Effects.Add(new(){Id=Id(),OperationId=op.Id,ColonyId=second.Id,TargetId=op.Id,Kind="physicalTransfer"});Assert.Throws<InvalidDataException>(()=>ColonyStateCodec.Serialize(s));
    }
    [Fact] public void AutomaticNativeBufferRequiresOptInAndNeverOverfillsHalfCapacity()
    {
        var(s,e,l)=Fixture(100*U,20*U);l.NativeInput=true;l.Amount=49*U;l.PhysicalReserve=50*U;Assert.Same(s,ColonyEngine.RunPhysicalProcurementPolicies(s,e));
        var enable=Command(s,e,l.ColonyId,"configurePhysicalProcurement");enable.Fields["Enabled"]="true";s=ColonyEngine.Execute(s,enable,e).State;s=ColonyEngine.RunPhysicalProcurementPolicies(s,e);
        Assert.Single(s.PhysicalTransfers);Assert.Equal(U,s.PhysicalTransfers.Single().Amount);Assert.Equal("toPhysical",s.PhysicalTransfers.Single().Direction);s=Finish(s,e,l,s.PhysicalTransfers.Single().Id);
        e.Ut+=5;l.ObservedUt=e.Ut;s=ColonyEngine.Advance(s,e);s=ColonyEngine.RunPhysicalProcurementPolicies(s,e);Assert.Single(s.PhysicalTransfers);Assert.Equal(50*U,l.Amount);Assert.Equal(99*U,s.Colonies.Single().Stock.Single().Amount);
    }
    [Fact] public void LocalReorderUsesObservedStockBeforeFiniteSupplierPurchase()
    {
        var(s,e,l)=Fixture(0);s.Suppliers.Add(new(){Id="paid",Resource=l.Resource,Available=100*U,FundsPerUnit=3,FreightFunds=2,TravelSeconds=10,ConcurrentCapacity=1,MassCapacityMicroTonnes=100*U,VolumeCapacityMilliLiters=100*U});s.Colonies.Single().Stock.Single().UnitMassMicroTonnes=1;s.Colonies.Single().Stock.Single().UnitVolumeMilliLiters=1;
        s.ReorderPolicies.Add(new(){ColonyId=l.ColonyId,Resource=l.Resource,Enabled=true,ReorderPoint=5*U,TargetAmount=10*U,NextReviewUt=100});s=ColonyEngine.RunPlanningPolicies(s,e);
        Assert.Empty(s.Shipments);Assert.Single(s.PhysicalTransfers);Assert.Equal(10*U,s.PhysicalTransfers.Single().Amount);Assert.Equal(0,s.Colonies.Single().Stock.Single().Amount);
        s=Finish(s,e,l,s.PhysicalTransfers.Single().Id);Assert.Equal(10*U,s.Colonies.Single().Stock.Single().Amount);Assert.Equal(100*U,s.Suppliers.Single().Available);
    }
    [Fact] public void FoundingLocalSubstitutionPreservesBillAndReleasesOnlyActualRemainingCost()
    {
        var(s,e,id)=ColonyPlanningTests.Fixture();s=ColonyPlanningTests.Stage(s,e,id);var c=s.Colonies.Single();var f=new ColonyFacility {Id=Id(),VesselId=Id(),PartIds=new(){7}};c.Facilities.Add(f);var local=Local(c,f,e,"MaterialKits");local.Amount=2*U;e.Planning.LocalStocks.Add(local);
        s=ColonyPlanningTests.Approve(s,e,id);var original=s.Plans.Single().Quote;long before=s.Plans.Single().RemainingFunds;var line=s.Plans.Single().Imports.Single(i=>i.Resource=="MaterialKits");Assert.Equal(5*U,line.Amount);Assert.Equal(15,line.Funds);
        s=ColonyEngine.RunPlanning(s,e);Assert.Empty(s.Shipments);var op=s.PhysicalTransfers.Single();Assert.Equal(2*U,op.PlannedMaterialAmount);s=Finish(s,e,local,op.Id);
        Assert.Equal(original.Id,s.Plans.Single().Quote.Id);Assert.Equal(5*U,s.Plans.Single().Quote.Imports.Single(i=>i.Resource=="MaterialKits").Amount);
        Assert.Equal(before-4,s.Plans.Single().RemainingFunds);Assert.Equal(3*U,ColonyEngine.PlanningSupplierReserved(s,"MaterialKits"));Assert.Equal(2*U,s.Colonies.Single().Stock.Single(x=>x.Resource=="MaterialKits").Reserved);
        s=ColonyEngine.RunPlanning(s,e);var shipment=s.Shipments.Single();Assert.Equal(3*U,shipment.Amount);Assert.Equal(11,shipment.Funds);Assert.Equal(20,shipment.TravelSeconds);Assert.Equal(original.Id,s.Plans.Single().Quote.Id);
    }
    [Fact] public void FabricatedLocalSubstitutionWithoutCompletedReceiptIsRejected()
    {
        var(s,e,id)=ColonyPlanningTests.Fixture();s=ColonyPlanningTests.Approve(s,e,id);var line=s.Plans.Single().Imports.First();line.SubstitutedLocallyAmount=U;s.Plans.Single().RemainingFunds-=line.Funds-ColonyEngine.PlanningImportFunds(line);
        Assert.Throws<InvalidDataException>(()=>ColonyStateCodec.Serialize(s));
    }
    [Fact] public void AdditiveProgressFieldKeepsPreexistingSavedQuoteHashProjection()
    {
        var(s,e,id)=ColonyPlanningTests.Fixture();var q=ColonyEngine.QuoteFoundingPlan(s,id,e);
        // Reproduce the previous canonical projection independently using the
        // original quote DTO properties, excluding later additive fields.
        var terms=new ColonyPlanningQuote {Kind=q.Kind,ColonyId=q.ColonyId,ContextKey=q.ContextKey,Revision=q.Revision,AuthorityHash=q.AuthorityHash,SupportPolicyHash=q.SupportPolicyHash,Buildings=q.Buildings,Imports=q.Imports,Materials=q.Materials,ExistingIncoming=q.ExistingIncoming,StagingPolicyId=q.StagingPolicyId,StagingPolicyHash=q.StagingPolicyHash,StagingFunds=q.StagingFunds,StartupSupportReserve=q.StartupSupportReserve,TargetPopulation=q.TargetPopulation,TotalFunds=q.TotalFunds,LaborSeconds=q.LaborSeconds};
        var node=JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(terms))!;
        foreach(string additive in new[]{"BootstrapWorkers","FoundingIntent","Residents","StartupPolicies","MakeImportComparisons","ProductionInvestments"})node.AsObject().Remove(additive);
        foreach(var load in node["Imports"]!.AsArray())load!.AsObject().Remove("SubstitutedLocallyAmount");
        string Canonical(JsonNode? n)=>n is JsonObject obj?"{"+string.Join(",",obj.OrderBy(p=>p.Key,StringComparer.Ordinal).Select(p=>System.Text.Json.JsonSerializer.Serialize(p.Key)+":"+Canonical(p.Value)))+"}":n is JsonArray arr?"["+string.Join(",",arr.Select(Canonical))+"]":n?.ToJsonString()??"null";
        Assert.Equal(Hash(Canonical(node)),q.Id);
        s=ColonyPlanningTests.Approve(s,e,id);var saved=JsonNode.Parse(Encoding.UTF8.GetString(ColonyStateCodec.Serialize(s)))!;
        foreach(var plan in saved["Plans"]!.AsArray()){
            foreach(string additive in new[]{"Workers","Residents","StartupReservesReleased","StartupPoliciesApplied","Production"})plan!.AsObject().Remove(additive);
            foreach(string additive in new[]{"BootstrapWorkers","FoundingIntent","Residents","StartupPolicies","MakeImportComparisons","ProductionInvestments"})plan!["Quote"]!.AsObject().Remove(additive);
            foreach(var load in plan!["Imports"]!.AsArray())load!.AsObject().Remove("SubstitutedLocallyAmount");foreach(var load in plan["Quote"]!["Imports"]!.AsArray())load!.AsObject().Remove("SubstitutedLocallyAmount");}
        saved.AsObject().Remove("PhysicalTransfers");saved.AsObject().Remove("PhysicalLots");saved.AsObject().Remove("PhysicalPolicies");var loaded=ColonyStateCodec.Deserialize(Encoding.UTF8.GetBytes(saved.ToJsonString()));Assert.Equal(q.Id,loaded.Plans.Single().Quote.Id);Assert.Equal(q.TotalFunds,loaded.Plans.Single().RemainingFunds);
    }
    [Fact] public void QualifiedRealMachineryReserveFeedsInstalledMaintenanceWithoutBuyingDuplicateStock()
    {
        var(s,e,l)=Fixture(0,0,"Machinery");var c=s.Colonies.Single();c.Stock.Single().Capacity=1000*U;c.Facilities.Single().PartIds.Add(8);
        l.Amount=7500*U;l.Capacity=10000*U;l.StockKind="maintenanceReserve";l.QualifiedWorker=true;l.WorkerWitness="Explicit actual Engineer/workshop provider fixture";
        var target=new ColonyServiceTarget {ColonyId=c.Id,FacilityId=l.FacilityId,PartId=8,PartName="Actual installed converter tank",DepotId=l.DepotId,SourceResource="Machinery",DestinationResource="Machinery",Amount=80*U,Capacity=100*U,QuoteHash=Hash("service"),ContextKey=e.ContextKey,ObservedUt=e.Ut,Current=true,CanApply=true,QualifiedWorker=true,WorkerWitness=l.WorkerWitness,Provider=l.Provider};e.Services.Targets.Add(target);
        s.ServicePolicies.Add(new(){ColonyId=c.Id,AutomaticEnabled=true,TargetFillFraction=.95});s=ColonyEngine.RunPhysicalProcurementPolicies(s,e);var op=s.PhysicalTransfers.Single();Assert.Equal(15*U,op.Amount);Assert.Equal(0,ColonyEngine.PendingCash(s));Assert.Empty(s.Shipments);
        s=Finish(s,e,l,op.Id);Assert.Equal(7485*U,l.Amount);Assert.Equal(15*U,s.Colonies.Single().Stock.Single().Amount);
        e.Ut=101;l.ObservedUt=101;target.ObservedUt=101;s=ColonyEngine.Advance(s,e);var service=s.ServiceOperations.Single();Assert.Equal(15*U,service.Amount);Assert.Equal(8u,service.PartId);
        s=ColonyEngine.HoldPreparedService(s,service.Id,l.Provider,"owned 15; exact installed before 80");s=ColonyEngine.CompletePreparedService(s,service.Id,e.Ut,"owned 0; exact installed after 95");
        Assert.Equal(0,s.Colonies.Single().Stock.Single().Amount);Assert.Equal(7580*U,l.Amount+target.Amount+service.Amount);Assert.Equal(0,ColonyEngine.PendingCash(s));ColonyStateCodec.Serialize(s);
    }
    [Fact] public void MachineryReserveWithoutWorkerCannotBeReclassifiedAsGenericWarehouse()
    {
        var(s,e,l)=Fixture(0,0,"Machinery");l.StockKind="maintenanceReserve";l.QualifiedWorker=false;
        var q=ColonyEngine.QuotePhysicalTransfer(s,l.ColonyId,l.Id,"toColony",U,e);Assert.False(q.CanApprove);Assert.Contains("qualified repair-worker",q.Reason);
    }
    [Fact] public void CancellationReleasesUnattemptedSourceAndDestinationClaims()
    {
        foreach(string direction in new[]{"toColony","toPhysical"})
        {
            var(s,e,l)=Fixture();(s,var transfer)=Reserve(s,e,l,direction,U);var cancel=Command(s,e,l.ColonyId,"cancelPhysicalTransfer");cancel.TargetId=transfer.OperationId;var result=ColonyEngine.Execute(s,cancel,e);Assert.Equal("accepted",result.Outcome);s=result.State;
            Assert.Equal("cancelled",s.PhysicalTransfers.Single().State);Assert.Equal(10*U,s.Colonies.Single().Stock.Single().Amount);Assert.Equal(0,s.Colonies.Single().Stock.Single().Reserved);Assert.Equal(0,s.Colonies.Single().Stock.Single().IncomingReserved);Assert.Equal(20*U,l.Amount);
        }
    }
    [Fact] public void ImportedOriginMarkerCannotBeErasedAfterUnknownDepletion()
    {
        var(s,e,l)=Fixture(10*U,5*U);(s,var op)=Reserve(s,e,l,"toPhysical",3*U);s=Finish(s,e,l,op.OperationId);
        s.PhysicalLots.Single().HadImportedStock=false;s.PhysicalLots.Single().ImportedAttribution=0;Assert.Throws<InvalidDataException>(()=>ColonyStateCodec.Serialize(s));
    }
}
