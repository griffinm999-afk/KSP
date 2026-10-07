using Expanse.Domain;

public sealed class OreExportTests
{
    static AcceptedState RouteState(long quantity = OreExportPolicy.BatchMicroUnits, long rate = 500)
    {
        var state = new AcceptedState { WorldId="ore-world", CheckpointId="initial" };
        var depots = new[] { new DepotRecord { DepotId="source", MembershipRevision=1, MembershipHash=new string('a',64) } };
        var registry = new DepotRegistrySnapshot { RegistryVersion="ore-registry", Depots=depots, RegistryHash=OperationIdentity.ComputeDepotRegistryHash(depots) };
        var synced=AcceptedStateCodec.SyncDepots(state,OperationIdentity.Create(state.WorldId,1,"sync"),"sync",1,OperationIdentity.DepotRegistryPayloadHash(registry),0,AcceptedStateCodec.ComputeHash(state),registry,1);
        Assert.Equal("accepted",synced.Outcome); state=synced.State;
        var route = new RouteVersionRecord { RouteId="ore-route", Version=1, SourceDepotId="source", SourceMembershipRevision=1, SourceMembershipHash=depots[0].MembershipHash, DestinationDepotId=OreExportPolicy.KerbinBuyerId, DestinationKind=OreExportPolicy.VirtualDestinationKind, FundsPerUnit=rate, TravelDurationSeconds=64800, Provenance="modeled routine landing and recovery", Resources=[new ResourceAmount { ResourceName="Ore", AmountMicroUnits=quantity }] };
        var accepted=AcceptedStateCodec.UpsertRoute(state,OperationIdentity.Create(state.WorldId,2,"route"),"route",2,OperationIdentity.RouteVersionPayloadHash(route),state.Revision,AcceptedStateCodec.ComputeHash(state),route,2);
        Assert.Equal("accepted",accepted.Outcome); return accepted.State;
    }
    internal static (AcceptedState State, PhysicalEffectIntent Intent, ActiveShipmentRecord Shipment) Dispatched(long quantity = OreExportPolicy.BatchMicroUnits, long rate = 500)
    {
        var state=RouteState(quantity,rate); var route=state.RouteVersions.Single();
        var capability=new InventoryCapability { DepotId="source", MembershipRevision=1, MembershipHash=route.SourceMembershipHash, AnchorPersistentId=7, MemberSetHash=OperationIdentity.ComputeMemberSetHash(7,[7]), ProviderId="synthetic", ProviderVersion="1", Scene="Flight", ObservationAvailable=true, ReadSupported=true, WriteSupported=true, SynchronousRollbackSupported=true, PersistenceSyncSupported=true };
        var intent=new PhysicalEffectIntent { EffectKind="dispatchDebit", Capability=capability, MemberPersistentIds=[7], MembershipRevision=1, Deltas=[new PhysicalEffectResourceDelta { MemberPersistentId=7, ResourceName="Ore", DeltaMicroUnits=-quantity }] };
        var shipment=new ActiveShipmentRecord { ShipmentId="ore-shipment", RouteId=route.RouteId, RouteVersion=1, SourceDepotId="source", DestinationDepotId=route.DestinationDepotId, DestinationKind=route.DestinationKind, FundsPerUnit=route.FundsPerUnit, RemainingResources=route.Resources };
        var witness=new PhysicalSuccessWitness { ProviderId="synthetic", ProviderVersion="1", Rows=[new PhysicalSuccessWitnessRow { MemberPersistentId=7, ResourceName="Ore", BeforeAmount=quantity / 1000000d + 100, IntendedAfterAmount=100, ObservedAfterAmount=100 }] };
        var accepted=AcceptedStateCodec.Dispatch(state,OperationIdentity.Create(state.WorldId,3,"dispatch"),"dispatch",3,OperationIdentity.DispatchPayloadHash(shipment,intent),state.Revision,AcceptedStateCodec.ComputeHash(state),shipment,intent,10,null,witness);
        Assert.Equal("accepted",accepted.Outcome); return (accepted.State,intent,shipment);
    }
    static FundsSuccessWitness Witness() => new() { BeforeFunds=125.5, IntendedDeltaFunds=500000, IntendedAfterFunds=500125.5, ObservedAfterFunds=500125.5 };
    static EconomicRecoveryIntent Intent() => new() { ShipmentId="ore-shipment", FundsDelta=500000 };
    static StateTransitionResult Settle(AcceptedState state, double ut, FundsSuccessWitness? witness=null)
        => AcceptedStateCodec.SettleRecovery(state,OperationIdentity.Create(state.WorldId,4,"sale"),"sale",4,OperationIdentity.RecoveryPayloadHash(Intent()),state.Revision,AcceptedStateCodec.ComputeHash(state),Intent(),ut,witness ?? Witness());

    [Fact]
    public void PhysicalOreDebitPrecedesDelayedSaleAndBuyerNeedsNoPhysicalDepot()
    {
        var state=Dispatched().State; Assert.Single(state.Depots); Assert.Single(state.ActiveShipments);
        Assert.Equal(64810,state.ActiveShipments.Single().DueUt); Assert.Null(state.Receipts.Last().FundsWitness);
        Assert.Equal("held",Settle(state,64809).Outcome);
        var settled=Settle(state,64810); Assert.Equal("accepted",settled.Outcome); Assert.Empty(settled.State.ActiveShipments);
        Assert.Equal(500000,settled.State.Receipts.Last().FundsWitness!.IntendedDeltaFunds);
        var roundtrip=AcceptedStateCodec.ReadCapsule(AcceptedStateCodec.CreateCapsule(settled.State));
        Assert.Equal(AcceptedStateCodec.ComputeHash(settled.State),AcceptedStateCodec.ComputeHash(roundtrip)); Assert.Equal(4,roundtrip.CapsuleEncodingVersion);
        Assert.Equal("duplicate",Settle(roundtrip,64810).Outcome); Assert.Empty(roundtrip.ActiveShipments);
    }
    [Fact]
    public void WrongReadbackDoesNotRemoveCargoOrProduceAReceipt()
    {
        var state=Dispatched().State; var witness=Witness(); witness.ObservedAfterFunds+=1;
        var result=Settle(state,64810,witness); Assert.Equal("rejected",result.Outcome); Assert.Same(state,result.State); Assert.Single(state.ActiveShipments);
    }
    [Fact]
    public void RoundedFundsDeltaAtLargeBalanceCannotSettleOrBecomeFaultEvidence()
    {
        var before=Math.Pow(2, 60); var after=before+OreExportPolicy.FundsPerRecovery;
        Assert.True(after>before); Assert.NotEqual((double)OreExportPolicy.FundsPerRecovery,after-before);
        var state=Dispatched().State;
        Assert.Equal("rejected",Settle(state,64810,new FundsSuccessWitness { BeforeFunds=before, IntendedDeltaFunds=500000, IntendedAfterFunds=after, ObservedAfterFunds=after }).Outcome);
        Assert.Throws<InvalidDataException>(()=>OreExportPolicy.ValidateResult(new EconomicEffectResult { Status="uncertain", BeforeFunds=before, IntendedDeltaFunds=500000, IntendedAfterFunds=after, Reason="Unknown readback" }));
        Assert.Single(state.ActiveShipments);
    }
    [Fact]
    public void UncertainFundsWriteRetainsCargoBlocksWritesAndCannotLoseFaultReceipt()
    {
        var state=Dispatched().State;
        var fault=AcceptedStateCodec.FaultRecovery(state,OperationIdentity.Create(state.WorldId,4,"sale"),"sale",4,OperationIdentity.RecoveryPayloadHash(Intent()),state.Revision,AcceptedStateCodec.ComputeHash(state),Intent(),new EconomicEffectResult { Status="uncertain", BeforeFunds=125.5, IntendedDeltaFunds=500000, IntendedAfterFunds=500125.5, ObservedAfterKnown=false, Reason="Interrupted during synchronous funds mutation." },64810);
        Assert.Equal("faulted",fault.Outcome); state=AcceptedStateCodec.ReadCapsule(AcceptedStateCodec.CreateCapsule(fault.State));
        Assert.True(state.WritesBlocked); Assert.Single(state.ActiveShipments); Assert.Single(state.EconomicFaults); Assert.Equal("duplicate",Settle(state,64810).Outcome);
        Assert.Throws<InvalidDataException>(()=>AcceptedStateCodec.Compact(state,OperationIdentity.Create(state.WorldId,5,"compact"),"compact",5,OperationIdentity.CompactionPayloadHash(4),state.Revision,AcceptedStateCodec.ComputeHash(state),4,64810));
        var other=new EconomicRecoveryIntent { ShipmentId="ore-shipment", FundsDelta=500000 };
        Assert.Equal("held",AcceptedStateCodec.SettleRecovery(state,OperationIdentity.Create(state.WorldId,5,"retry-new"),"retry-new",5,OperationIdentity.RecoveryPayloadHash(other),state.Revision,AcceptedStateCodec.ComputeHash(state),other,64810,Witness()).Outcome);
    }
    [Theory]
    [InlineData(1099999999,"noDispatch")]
    [InlineData(1100000000,"dispatch")]
    public void ExportReserveUsesPhysicalSourceStock(long stock, string outcome)
    {
        var result=LogisticsPlanner.PlanExportStock(100000000,new StockObservation { DepotId="source", Available=true, MicroUnitProjectionSafe=true, Resources=[new StockAmount { ResourceName="Ore", AmountMicroUnits=stock, CapacityMicroUnits=2000000000 }] });
        Assert.Equal(outcome,result.Outcome); if(outcome=="dispatch") Assert.Equal(OreExportPolicy.BatchMicroUnits,result.RequestedDispatchMicroUnits);
    }
    [Fact]
    public void PhysicalEncodingsRoundtripWithoutUpgradeAndBuyerFieldsBindPayload()
    {
        foreach(var version in new[] { 1,2,3 })
        {
            var old=new AcceptedState { SchemaVersion=version==1?1:2, CapsuleEncodingVersion=version, WorldId="legacy", CheckpointId="checkpoint" };
            var bytes=AcceptedStateCodec.Serialize(old); Assert.Equal((byte)(48+version),bytes[3]); Assert.Equal(bytes,AcceptedStateCodec.Serialize(AcceptedStateCodec.Deserialize(bytes)));
        }
        var state=RouteState(); var route=state.RouteVersions.Single(); Assert.Equal((byte)'4',AcceptedStateCodec.Serialize(state)[3]);
        route.Resources[0].AmountMicroUnits--; Assert.Throws<ArgumentException>(()=>OperationIdentity.RouteVersionPayloadHash(route));
    }
    [Fact]
    public void ExportRuleAcceptsZeroReserveAndRejectsWrongBatchOrPhysicalStockRule()
    {
        var state=RouteState();
        var rule=new DeliveryRuleRecord { RuleId="export", Revision=1, Kind="exportStock", RouteId="ore-route", RouteVersion=1, Enabled=true, ResourceName="Ore", BatchSizeMicroUnits=OreExportPolicy.BatchMicroUnits };
        StateTransitionResult Upsert() => AcceptedStateCodec.UpsertRule(state,OperationIdentity.Create(state.WorldId,3,"rule"),"rule",3,OperationIdentity.RulePayloadHash(rule),state.Revision,AcceptedStateCodec.ComputeHash(state),rule,3);
        Assert.Equal("accepted",Upsert().Outcome);
        rule.BatchSizeMicroUnits--; Assert.NotEqual("accepted",Upsert().Outcome);
        rule.BatchSizeMicroUnits=OreExportPolicy.BatchMicroUnits; rule.Kind="keepStock"; rule.TargetMicroUnits=OreExportPolicy.BatchMicroUnits; rule.LowTriggerMicroUnits=OreExportPolicy.BatchMicroUnits;
        Assert.NotEqual("accepted",Upsert().Outcome);
    }
    [Fact]
    public void SettlementCompactionRetainsWatermarkAndCannotReplayRemovedSale()
    {
        var state=Settle(Dispatched().State,64810).State;
        state=AcceptedStateCodec.Compact(state,OperationIdentity.Create(state.WorldId,5,"compact"),"compact",5,OperationIdentity.CompactionPayloadHash(4),state.Revision,AcceptedStateCodec.ComputeHash(state),4,64810);
        state=AcceptedStateCodec.ReadCapsule(AcceptedStateCodec.CreateCapsule(state)); Assert.Empty(state.ActiveShipments); Assert.Equal(4,state.CompactionWatermark); Assert.Equal("rejected",Settle(state,64810).Outcome);
    }
    [Theory]
    [InlineData(1000000,100)]
    [InlineData(250000000,25000)]
    [InlineData(1000000000000,100000000)]
    public void ConfigurableWholeCargoSettlesImmutableHundredFundsPrice(long quantity,long payout)
    {
        var state=Dispatched(quantity,100).State;
        var intent=new EconomicRecoveryIntent { ShipmentId="ore-shipment", FundsDelta=payout };
        StateTransitionResult Sell(EconomicRecoveryIntent terms, long witnessed) => AcceptedStateCodec.SettleRecovery(state,OperationIdentity.Create(state.WorldId,4,"sale"),"sale",4,OperationIdentity.RecoveryPayloadHash(terms),state.Revision,AcceptedStateCodec.ComputeHash(state),terms,64810,new FundsSuccessWitness { BeforeFunds=125, IntendedDeltaFunds=witnessed, IntendedAfterFunds=125+witnessed, ObservedAfterFunds=125+witnessed });
        Assert.Equal("rejected",Sell(new EconomicRecoveryIntent { ShipmentId="ore-shipment", FundsDelta=payout+1 },payout+1).Outcome);
        Assert.Equal("rejected",Sell(intent,payout+1).Outcome);
        var sale=Sell(intent,payout); Assert.Equal("accepted",sale.Outcome);
        state=AcceptedStateCodec.ReadCapsule(AcceptedStateCodec.CreateCapsule(sale.State));
        Assert.Equal("duplicate",Sell(intent,payout).Outcome);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(-1000000)]
    [InlineData(1000001)]
    [InlineData(1000001000000)]
    [InlineData(long.MaxValue)]
    public void InvalidCargoCannotBecomeBuyerRouteOrPayout(long quantity)
    {
        Assert.False(OreExportPolicy.IsValidBatch(quantity));
        Assert.Throws<ArgumentException>(()=>OreExportPolicy.RecoveryFunds([new ResourceAmount { ResourceName="Ore", AmountMicroUnits=quantity }],100));
        Assert.Equal("held",LogisticsPlanner.PlanExportStock(0,new StockObservation(),quantity).Outcome);
    }
    [Fact]
    public void HistoricalFiveHundredRouteAndPendingCargoRoundtripWithoutChangingBytesOrHash()
    {
        var state=Dispatched().State; var bytes=AcceptedStateCodec.Serialize(state); var hash=AcceptedStateCodec.ComputeHash(state);
        var restored=AcceptedStateCodec.Deserialize(bytes);
        Assert.Equal(bytes,AcceptedStateCodec.Serialize(restored)); Assert.Equal(hash,AcceptedStateCodec.ComputeHash(restored));
        Assert.Equal(500,restored.ActiveShipments.Single().FundsPerUnit); Assert.Equal("accepted",Settle(restored,64810).Outcome);
        Assert.Throws<ArgumentException>(()=>OreExportPolicy.RecoveryFunds([new ResourceAmount { ResourceName="Ore", AmountMicroUnits=1000000 }],500));
    }
}