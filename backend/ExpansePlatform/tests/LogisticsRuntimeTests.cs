using Expanse.Domain;

public sealed class LogisticsRuntimeTests
{
    static (AcceptedState State, LogisticsRuntimeContext Context) Fixture(string kind = "exportStock", long rate = 100)
    {
        var state = new AcceptedState { WorldId = "runtime-world", CheckpointId = "initial" };
        var members = new[] { new DepotRecord { DepotId = "source", MembershipRevision = 1, MembershipHash = new string('a',64), Active = true }, new DepotRecord { DepotId = "dest", MembershipRevision = 1, MembershipHash = new string('b',64), Active = true } };
        var registry = new DepotRegistrySnapshot { RegistryVersion = "registry", Depots = members, RegistryHash = OperationIdentity.ComputeDepotRegistryHash(members) };
        var s = AcceptedStateCodec.SyncDepots(state,OperationIdentity.Create(state.WorldId,1,"sync"),"sync",1,OperationIdentity.DepotRegistryPayloadHash(registry),0,AcceptedStateCodec.ComputeHash(state),registry,1);
        Assert.Equal("accepted",s.Outcome); state = s.State;
        var route = new RouteVersionRecord { RouteId = "route", Version = 1, SourceDepotId = "source", SourceMembershipRevision = 1, SourceMembershipHash = members[0].MembershipHash,
            DestinationDepotId = kind == "exportStock" ? OreExportPolicy.KerbinBuyerId : "dest", DestinationKind = kind == "exportStock" ? OreExportPolicy.VirtualDestinationKind : "physicalDepot",
            FundsPerUnit = kind == "exportStock" ? rate : 0, DestinationMembershipRevision = kind == "exportStock" ? 0 : 1, DestinationMembershipHash = kind == "exportStock" ? "" : members[1].MembershipHash,
            TravelDurationSeconds = kind == "exportStock" ? 64800 : 120, Provenance = "explicit development transport profile", Resources = [new ResourceAmount { ResourceName = kind == "exportStock" ? "Ore" : "LiquidFuel", AmountMicroUnits = kind == "exportStock" ? 1000000000 : 1000000 }] };
        s = AcceptedStateCodec.UpsertRoute(state,OperationIdentity.Create(state.WorldId,2,"route"),"route",2,OperationIdentity.RouteVersionPayloadHash(route),state.Revision,AcceptedStateCodec.ComputeHash(state),route,2);
        Assert.Equal("accepted",s.Outcome); state=s.State;
        var rule = new DeliveryRuleRecord { RuleId="rule", Revision=1, RouteId="route", RouteVersion=1, Kind=kind, Enabled=true, ResourceName=kind=="repeat"?"":route.Resources[0].ResourceName,
            BatchSizeMicroUnits=kind=="repeat"?0:route.Resources[0].AmountMicroUnits, TargetMicroUnits=kind=="keepStock"?3000000:0, LowTriggerMicroUnits=kind=="keepStock"?1000000:0,
            IntervalSeconds=kind=="repeat"?10:0, NextDueUt=kind=="repeat"?10:0 };
        s=AcceptedStateCodec.UpsertRule(state,OperationIdentity.Create(state.WorldId,3,"rule"),"rule",3,OperationIdentity.RulePayloadHash(rule),state.Revision,AcceptedStateCodec.ComputeHash(state),rule,3);
        Assert.Equal("accepted",s.Outcome); state=s.State;
        var context=new LogisticsRuntimeContext { SessionId=Guid.NewGuid(),LoadEpoch=Guid.NewGuid(),InstallNamespace="dev-install", SaveFolder="isolated",RunId="run",EconomicRecoveryAvailable=true,Registry=registry };
        context.Capabilities=members.Select((m,i)=>new InventoryCapability {DepotId=m.DepotId,MembershipRevision=1,MembershipHash=m.MembershipHash,AnchorPersistentId=(uint)(7+i),MemberSetHash=OperationIdentity.ComputeMemberSetHash((uint)(7+i),[(uint)(7+i)]),ProviderId="fixture",ProviderVersion="1",Scene="Flight",ObservationAvailable=true,ReadSupported=true,WriteSupported=true,SynchronousRollbackSupported=true,PersistenceSyncSupported=true}).ToArray();
        context.Stocks=members.Select((m,i)=>new StockObservation {DepotId=m.DepotId,WorldId=state.WorldId,RegistryHash=registry.RegistryHash,SessionId=context.SessionId,LoadEpoch=context.LoadEpoch,MembershipRevision=1,MembershipHash=m.MembershipHash,Available=true,MicroUnitProjectionSafe=true,
            Resources=[new StockAmount {ResourceName=route.Resources[0].ResourceName,AmountMicroUnits=i==0?3000000000:0,CapacityMicroUnits=5000000000,DebitAllowed=true}],
            MemberStocks=[new MemberStockObservation {MemberPersistentId=(uint)(7+i),Resources=[new StockAmount {ResourceName=route.Resources[0].ResourceName,AmountMicroUnits=i==0?3000000000:0,CapacityMicroUnits=5000000000,DebitAllowed=true}]}]}).ToArray();
        return(state,context);
    }
    static StateTransitionResult Dispatch(AcceptedState state, EffectProposal p,double ut,double before)
    {
        var rows=p.Delivery!.PhysicalEffect!.Deltas.Select(x=>new PhysicalSuccessWitnessRow {MemberPersistentId=x.MemberPersistentId,ResourceName=x.ResourceName,BeforeAmount=before,IntendedAfterAmount=before+x.DeltaMicroUnits/1000000d,ObservedAfterAmount=before+x.DeltaMicroUnits/1000000d}).ToArray();
        return AcceptedStateCodec.Dispatch(state,p.OperationId,p.ClientRequestId,p.CommandSequence,p.PayloadHash,p.ExpectedRevision,p.ExpectedStateHash,p.Delivery.Shipment!,p.Delivery.PhysicalEffect,ut,p.Delivery.ScheduleRuleUpdate,new PhysicalSuccessWitness {ProviderId="fixture",ProviderVersion="1",Rows=rows});
    }
    [Fact]
    public void NewOreOrderDebitsExactly1000AndPays100000OnlyAfterSaved64800Seconds()
    {
        var (state,context)=Fixture(); var before=AcceptedStateCodec.Serialize(state);
        var p=LogisticsRuntimePlanner.Plan(state,context,100).Proposal!;
        Assert.Equal(before,AcceptedStateCodec.Serialize(state)); Assert.Equal("dispatch",p.OperationKind);
        Assert.Equal(-1000000000,p.Delivery!.PhysicalEffect!.Deltas.Single().DeltaMicroUnits);
        var dispatched=Dispatch(state,p,100,3000); Assert.Equal("accepted",dispatched.Outcome); state=dispatched.State;
        Assert.Equal(100,state.ActiveShipments.Single().DepartureUt); Assert.Equal(64900,state.ActiveShipments.Single().DueUt);
        Assert.Equal("rejected",Dispatch(state,p,100,3000).Outcome); Assert.Single(state.ActiveShipments);
        state.DeliveryRules[0].Enabled=false;
        Assert.Null(LogisticsRuntimePlanner.Plan(state,context,64899).Proposal);
        var sale=LogisticsRuntimePlanner.Plan(state,context,64900).Proposal!; Assert.Equal("recoverySale",sale.OperationKind); Assert.Equal(100000,sale.Delivery!.RecoveryIntent!.FundsDelta);
        var settled=AcceptedStateCodec.SettleRecovery(state,sale.OperationId,sale.ClientRequestId,sale.CommandSequence,sale.PayloadHash,sale.ExpectedRevision,sale.ExpectedStateHash,sale.Delivery.RecoveryIntent,64900,new FundsSuccessWitness {BeforeFunds=42,IntendedDeltaFunds=100000,IntendedAfterFunds=100042,ObservedAfterFunds=100042});
        Assert.Equal("accepted",settled.Outcome); Assert.Empty(settled.State.ActiveShipments);
        var restored=AcceptedStateCodec.ReadCapsule(AcceptedStateCodec.CreateCapsule(settled.State)); Assert.Null(LogisticsRuntimePlanner.Plan(restored,context,70000).Proposal);
        Assert.Equal("duplicate",AcceptedStateCodec.SettleRecovery(restored,sale.OperationId,sale.ClientRequestId,sale.CommandSequence,sale.PayloadHash,sale.ExpectedRevision,sale.ExpectedStateHash,sale.Delivery.RecoveryIntent,70000,settled.State.Receipts.Last().FundsWitness!).Outcome);
    }
    [Fact]
    public void DepartedOldPriceRemains500000ButCannotStartAnotherOldPriceBatch()
    {
        var(state,context)=Fixture(rate:500); Assert.Null(LogisticsRuntimePlanner.Plan(state,context,100).Proposal);
        var legacy=OreExportTests.Dispatched().State;
        context.Registry=new DepotRegistrySnapshot {RegistryVersion=legacy.DepotRegistryVersion,RegistryHash=legacy.DepotRegistryHash,Depots=legacy.Depots};
        var sale=LogisticsRuntimePlanner.Plan(legacy,context,64810).Proposal!;
        Assert.Equal("recoverySale",sale.OperationKind); Assert.Equal(500000,sale.Delivery!.RecoveryIntent!.FundsDelta); Assert.Equal(64810,legacy.ActiveShipments.Single().DueUt);
    }
    [Theory]
    [InlineData("age")][InlineData("epoch")][InlineData("hash")][InlineData("locked")][InlineData("provider")]
    public void StaleOrLockedProviderCannotCreateCargo(string defect)
    {
        var(state,context)=Fixture(); var stock=context.Stocks[0];
        if(defect=="age")stock.AgeSeconds=6.001; if(defect=="epoch")stock.LoadEpoch=Guid.NewGuid(); if(defect=="hash")stock.RegistryHash="other";
        if(defect=="locked")stock.MemberStocks[0].Resources[0].DebitAllowed=false; if(defect=="provider")context.Capabilities[0].PersistenceSyncSupported=false;
        Assert.Null(LogisticsRuntimePlanner.Plan(state,context,100).Proposal); Assert.Empty(state.ActiveShipments);
    }
    [Fact]
    public void MissedRepeatSlotsCoalesceAndPhysicalDepartureUsesActualCommitUt()
    {
        var(state,context)=Fixture("repeat"); var p=LogisticsRuntimePlanner.Plan(state,context,100000).Proposal!;
        Assert.Equal("dispatch",p.OperationKind); Assert.Equal(10000,p.Delivery!.ScheduleRuleUpdate!.WaitingCoalescedSlots);
        var accepted=Dispatch(state,p,100000,3000); Assert.Equal("accepted",accepted.Outcome);
        Assert.Equal(100120,accepted.State.ActiveShipments.Single().DueUt); Assert.Equal(100010,accepted.State.DeliveryRules.Single().NextDueUt);
        Assert.Null(LogisticsRuntimePlanner.Plan(accepted.State,context,100001).Proposal);
    }
    [Fact]
    public void FuelKeepStockCountsInboundCargoAndDueArrivalIsSelectedBeforeNewDispatch()
    {
        var(state,context)=Fixture("keepStock"); var p=LogisticsRuntimePlanner.Plan(state,context,100).Proposal!;
        var accepted=Dispatch(state,p,100,3000); Assert.Equal("accepted",accepted.Outcome); state=accepted.State;
        Assert.Equal(1000000,state.ActiveShipments.Single().RemainingResources.Single().AmountMicroUnits);
        Assert.Null(LogisticsRuntimePlanner.Plan(state,context,101).Proposal);
        var arrival=LogisticsRuntimePlanner.Plan(state,context,220).Proposal!;
        Assert.Equal("arrival",arrival.OperationKind); Assert.Equal(1000000,arrival.Delivery!.Credits.Single().AmountMicroUnits);
    }
    [Fact]
    public void UnavailableDestinationKeepsCargoAndReportsAccurateTransientHold()
    {
        var(state,context)=Fixture("keepStock"); state=Dispatch(state,LogisticsRuntimePlanner.Plan(state,context,100).Proposal!,100,3000).State;
        context.Capabilities[1].WriteSupported=false; context.Capabilities[1].HoldReason="Anchored provider test hold";
        var decision=LogisticsRuntimePlanner.Plan(state,context,220);
        Assert.Null(decision.Proposal); Assert.Single(state.ActiveShipments); Assert.Equal("Anchored provider test hold",decision.ArrivalHolds.Single().Reason);
    }
    [Fact]
    public void CapacityLimitStillPermitsDueArrivalButNeverCreatesThe33rdShipment()
    {
        var(state,context)=Fixture();
        for(int i=0;i<32;i++)state.ActiveShipments=state.ActiveShipments.Append(new ActiveShipmentRecord {ShipmentId="cargo"+i,RouteId="route",RouteVersion=1,SourceDepotId="source",DestinationDepotId=OreExportPolicy.KerbinBuyerId,DestinationKind=OreExportPolicy.VirtualDestinationKind,FundsPerUnit=100,DepartureUt=100,DueUt=64900,RemainingResources=state.RouteVersions[0].Resources}).ToArray();
        Assert.Null(LogisticsRuntimePlanner.Plan(state,context,200).Proposal);
        Assert.Equal("recoverySale",LogisticsRuntimePlanner.Plan(state,context,64900).Proposal!.OperationKind);
    }
    [Fact] public void ReservedExportTankIsTransientAndDoesNotAdvanceItsSavedRule()
    {
        var(state,context)=Fixture();var bytes=AcceptedStateCodec.Serialize(state);var original=LogisticsRuntimePlanner.Plan(state,context,100).Proposal!;
        context.ReservedPhysicalTanks=[new(){DepotId="source",PartId=7,Resource="Ore"}];
        var held=LogisticsRuntimePlanner.Plan(state,context,101);Assert.Null(held.Proposal);Assert.Contains("temporarily reserved",held.Reason);Assert.Equal(bytes,AcceptedStateCodec.Serialize(state));Assert.Empty(state.ActiveShipments);
        context.ReservedPhysicalTanks=[];var resumed=LogisticsRuntimePlanner.Plan(state,context,102).Proposal!;Assert.Equal(original.OperationId,resumed.OperationId);Assert.Equal(original.PayloadHash,resumed.PayloadHash);
        var dispatched=Dispatch(state,resumed,102,3000);Assert.Equal("accepted",dispatched.Outcome);Assert.Single(dispatched.State.ActiveShipments);Assert.Equal(102,dispatched.State.ActiveShipments.Single().DepartureUt);
    }
    [Fact] public void ReservedArrivalTankPreservesDueCargoAcrossReloadAndRelease()
    {
        var(state,context)=Fixture("keepStock");state=Dispatch(state,LogisticsRuntimePlanner.Plan(state,context,100).Proposal!,100,3000).State;
        context.ReservedPhysicalTanks=[new(){DepotId="dest",PartId=8,Resource="LiquidFuel"}];var bytes=AcceptedStateCodec.Serialize(state);
        var held=LogisticsRuntimePlanner.Plan(state,context,220);Assert.Null(held.Proposal);Assert.Contains("will retry after release",held.ArrivalHolds.Single().Reason);Assert.Equal(bytes,AcceptedStateCodec.Serialize(state));
        state=AcceptedStateCodec.ReadCapsule(AcceptedStateCodec.CreateCapsule(state));Assert.Single(state.ActiveShipments);Assert.Equal(1000000,state.ActiveShipments.Single().RemainingResources.Single().AmountMicroUnits);
        context.ReservedPhysicalTanks=[];var arrival=LogisticsRuntimePlanner.Plan(state,context,300).Proposal!;Assert.Equal("arrival",arrival.OperationKind);Assert.Equal(1000000,arrival.Delivery!.Credits.Single().AmountMicroUnits);Assert.Equal(220,state.ActiveShipments.Single().DueUt);
    }
    [Fact] public void HeldArrivalDoesNotBlockIndependentUnreservedOreWork()
    {
        var(state,context)=Fixture("keepStock");state=Dispatch(state,LogisticsRuntimePlanner.Plan(state,context,100).Proposal!,100,3000).State;
        state.RouteVersions=state.RouteVersions.Append(new RouteVersionRecord {RouteId="ore",Version=1,SourceDepotId="source",SourceMembershipRevision=1,SourceMembershipHash=state.Depots.Single(d=>d.DepotId=="source").MembershipHash,DestinationDepotId=OreExportPolicy.KerbinBuyerId,DestinationKind=OreExportPolicy.VirtualDestinationKind,FundsPerUnit=100,TravelDurationSeconds=64800,Provenance="independent fixture route",Resources=[new(){ResourceName="Ore",AmountMicroUnits=1000000000}]}).ToArray();
        state.DeliveryRules=state.DeliveryRules.Append(new DeliveryRuleRecord {RuleId="rule-z",RouteId="ore",RouteVersion=1,Revision=1,Enabled=true,Kind="exportStock",ResourceName="Ore",BatchSizeMicroUnits=1000000000}).ToArray();
        context.Stocks[0].Resources=context.Stocks[0].Resources.Append(new StockAmount {ResourceName="Ore",AmountMicroUnits=3000000000,CapacityMicroUnits=5000000000,DebitAllowed=true}).ToArray();context.Stocks[0].MemberStocks[0].Resources=context.Stocks[0].Resources;
        context.ReservedPhysicalTanks=[new(){DepotId="dest",PartId=8,Resource="LiquidFuel"}];var decision=LogisticsRuntimePlanner.Plan(state,context,220);
        Assert.Single(decision.ArrivalHolds);Assert.Equal("dispatch",decision.Proposal!.OperationKind);Assert.Equal("Ore",decision.Proposal.Delivery!.PhysicalEffect!.Deltas.Single().ResourceName);Assert.Single(state.ActiveShipments);
    }
}
