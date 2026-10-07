using Expanse.Clock.Core;
using Expanse.Clock.Host;
using Expanse.Domain;

namespace Expanse.Clock.Tests;

public sealed partial class RecoveryTests
{
    [Theory]
    [InlineData(false)][InlineData(true)]
    public void QueuedUserRuleEditRebasesAfterScheduledDispatchButHoldsConcurrentPolicyEdit(bool concurrentPolicyEdit)
    {
        var(state,route)=RuleLifecycleState();
        var rule=new DeliveryRuleRecord {RuleId="automatic",Revision=1,Kind="keepStock",RouteId=route.RouteId,RouteVersion=1,Enabled=true,ResourceName="Fuel",BatchSizeMicroUnits=2000000,LowTriggerMicroUnits=4000000,TargetMicroUnits=6000000};
        state=AcceptedStateCodec.UpsertRule(state,OperationIdentity.Create(state.WorldId,3,"initial-rule"),"initial-rule",3,OperationIdentity.RulePayloadHash(rule),state.Revision,AcceptedStateCodec.ComputeHash(state),rule,30).State;
        var directory=NewDirectory();
        EffectAttach Attachment(AcceptedState selected)
        {
            var attach=AttachFor(selected);attach.Capabilities=["dispatch","arrival",LogisticsRuntimePlanner.OwnershipCapability];
            attach.RegistrySnapshot=new DepotRegistrySnapshot {RegistryVersion=selected.DepotRegistryVersion,RegistryHash=selected.DepotRegistryHash,Depots=selected.Depots};
            attach.InventoryEndpoints=selected.Depots.Select((x,i)=>WritableCapability(x.DepotId,x.MembershipRevision,x.MembershipHash,(uint)(7+i),[(uint)(7+i)])).ToArray();return attach;
        }
        try
        {
            using var host=new RecoveryCoordinator(directory,new AlwaysLiveVerifier());var clock=FreshClock(state.WorldId);
            Assert.IsType<EffectIdle>(host.Attach(Attachment(state),clock));
            var edit=new DeliveryRuleRecord {RuleId=rule.RuleId,Revision=2,Kind=rule.Kind,RouteId=rule.RouteId,RouteVersion=1,Enabled=true,ResourceName="Fuel",BatchSizeMicroUnits=2000000,LowTriggerMicroUnits=4000000,TargetMicroUnits=8000000};
            var submitted=host.Submit(new SubmitCommand {MessageType="submitCommand",ClientRequestId="user-saved-edit",WorldId=state.WorldId,RunId="run-one",CommandKind="ruleUpsert",Delivery=new DeliveryCommandPayload {Kind="ruleUpsert",Rule=edit}});
            Assert.Equal("pending",submitted.Status);
            var advanced=new DeliveryRuleRecord {RuleId=rule.RuleId,Revision=2,Kind=rule.Kind,RouteId=rule.RouteId,RouteVersion=1,Enabled=true,ResourceName="Fuel",BatchSizeMicroUnits=2000000,LowTriggerMicroUnits=4000000,TargetMicroUnits=concurrentPolicyEdit?5000000:6000000};
            if(concurrentPolicyEdit)
                state=AcceptedStateCodec.UpsertRule(state,OperationIdentity.Create(state.WorldId,4,"other-policy-edit"),"other-policy-edit",4,OperationIdentity.RulePayloadHash(advanced),state.Revision,AcceptedStateCodec.ComputeHash(state),advanced,100).State;
            else
            {
                var capability=Attachment(state).InventoryEndpoints[0];
                var intent=new PhysicalEffectIntent {EffectKind="dispatchDebit",Capability=capability,MemberPersistentIds=[7],MembershipRevision=1,Deltas=[new PhysicalEffectResourceDelta {MemberPersistentId=7,ResourceName="Fuel",DeltaMicroUnits=-2000000}]};
                var shipment=new ActiveShipmentRecord {ShipmentId="scheduled-cargo",RouteId=route.RouteId,RouteVersion=1,SourceDepotId=route.SourceDepotId,DestinationDepotId=route.DestinationDepotId,RemainingResources=route.Resources};
                var payload=OperationIdentity.DispatchPayloadHash(shipment,intent,advanced);
                var dispatched=AcceptedStateCodec.Dispatch(state,OperationIdentity.Create(state.WorldId,4,"game-owned-dispatch"),"game-owned-dispatch",4,payload,state.Revision,AcceptedStateCodec.ComputeHash(state),shipment,intent,100,advanced,SuccessWitness(intent,10));
                Assert.Equal("accepted",dispatched.Outcome);state=dispatched.State;
            }
            clock.Accept(new ClockSample(1,"clockSample",2,Session,Epoch,Install,Save,"Synthetic",120,true,"Flight",false,"Year 1",1,WorldId:state.WorldId,RunId:"run-one"));
            Assert.IsType<EffectIdle>(host.Attach(Attachment(state),clock));
            if(concurrentPolicyEdit)
            {
                Assert.IsType<EffectIdle>(host.Poll(PollFor(state),clock));
                Assert.Equal("rejected",host.GetCommandStatus(new GetCommandStatus {MessageType="getCommandStatus",ClientRequestId="user-saved-edit",WorldId=state.WorldId,RunId="run-one"}).Status);
            }
            else
            {
                var rebased=Assert.IsType<EffectProposal>(host.Poll(PollFor(state),clock));Assert.Equal("user-saved-edit",rebased.ClientRequestId);
                Assert.NotEqual(submitted.OperationId,rebased.OperationId);Assert.Equal(3,rebased.Delivery!.Rule!.Revision);Assert.Equal(8000000,rebased.Delivery.Rule.TargetMicroUnits);
                var accepted=AcceptedStateCodec.UpsertRule(state,rebased.OperationId,rebased.ClientRequestId,rebased.CommandSequence,rebased.PayloadHash,rebased.ExpectedRevision,rebased.ExpectedStateHash,rebased.Delivery.Rule,120);
                Assert.Equal("accepted",accepted.Outcome);Assert.IsType<EffectIdle>(host.Receipt(ReceiptFor(rebased,accepted.State,120),clock));
                Assert.Single(accepted.State.ActiveShipments);Assert.Equal(8000000,accepted.State.DeliveryRules.Single().TargetMicroUnits);
            }
        }
        finally {Directory.Delete(directory,true);}
    }

    [Fact]
    public void GameOwnedSchedulingPreventsHostFromPreparingDueRecoveryAndKeepsProjectedHolds()
    {
        var state=OreExportTests.Dispatched().State;
        var directory=NewDirectory();
        try
        {
            using var host=new RecoveryCoordinator(directory,new AlwaysLiveVerifier());
            var clock=FreshClock(state.WorldId);
            clock.Accept(new ClockSample(1,"clockSample",2,Session,Epoch,Install,Save,"Synthetic",64900,true,"Flight",false,"Year 1",1,WorldId:state.WorldId,RunId:"run-one"));
            var attach=AttachFor(state); attach.Capabilities=["dispatch","arrival","economicRecovery.v1",LogisticsRuntimePlanner.OwnershipCapability];
            Assert.IsType<EffectIdle>(host.Attach(attach,clock));
            var poll=PollFor(state);poll.RuntimeShipmentHolds=[new TransientShipmentHold {ShipmentId="ore-shipment",Reason="Funds account unavailable"}];
            Assert.IsType<EffectIdle>(host.Poll(poll,clock));
            var view=host.GetAcceptedState(new GetAcceptedState {MessageType="getAcceptedState",WorldId=state.WorldId,RunId="run-one"});
            Assert.Equal("Funds account unavailable",view.TransientShipmentHolds.Single().Reason);
            Assert.Single(AcceptedStateCodec.ReadCapsule(view.AcceptedCapsule!).ActiveShipments);
            poll.RuntimeShipmentHolds=[]; Assert.IsType<EffectIdle>(host.Poll(poll,clock));
            Assert.Empty(host.GetAcceptedState(new GetAcceptedState {MessageType="getAcceptedState",WorldId=state.WorldId,RunId="run-one"}).TransientShipmentHolds);
        }
        finally {Directory.Delete(directory,true);}
    }

    [Theory]
    [InlineData(false,false)][InlineData(true,false)][InlineData(false,true)]
    public void LostRecoveryAckReattachesThroughLaterGameEventsOrExplicitlyHoldsCompactedReceipt(bool compactReceipt,bool alterWitness)
    {
        var state=OreExportTests.Dispatched().State;
        var directory=NewDirectory();
        try
        {
            var clock=FreshClock(state.WorldId);
            clock.Accept(new ClockSample(1,"clockSample",2,Session,Epoch,Install,Save,"Synthetic",64900,true,"Flight",false,"Year 1",1,WorldId:state.WorldId,RunId:"run-one"));
            EffectProposal sale;
            using(var first=new RecoveryCoordinator(directory,new AlwaysLiveVerifier()))
            {
                var attach=AttachFor(state);attach.Capabilities=["dispatch","arrival","economicRecovery.v1"];
                attach.RegistrySnapshot=new DepotRegistrySnapshot {RegistryVersion=state.DepotRegistryVersion,RegistryHash=state.DepotRegistryHash,Depots=state.Depots};
                attach.InventoryEndpoints=state.Depots.Select(x=>WritableCapability(x.DepotId,x.MembershipRevision,x.MembershipHash,7,[7])).ToArray();
                Assert.IsType<EffectIdle>(first.Attach(attach,clock));sale=Assert.IsType<EffectProposal>(first.Poll(PollFor(state),clock));
            }
            var settled=AcceptedStateCodec.SettleRecovery(state,sale.OperationId,sale.ClientRequestId,sale.CommandSequence,sale.PayloadHash,sale.ExpectedRevision,sale.ExpectedStateHash,sale.Delivery!.RecoveryIntent!,64900,
                new FundsSuccessWitness {BeforeFunds=42,IntendedDeltaFunds=500000,IntendedAfterFunds=500042,ObservedAfterFunds=500042});
            Assert.Equal("accepted",settled.Outcome);state=settled.State;
            var next=state.AcceptedSequence+1;
            var progressed=AcceptedStateCodec.Increment(state,state.WorldId,OperationIdentity.Create(state.WorldId,next,"later-game-event"),"later-game-event",next,OperationIdentity.CounterIncrementPayloadHash(1),1,64901);
            Assert.Equal("accepted",progressed.Outcome);state=progressed.State;
            if(compactReceipt)
            {
                next=state.AcceptedSequence+1;
                state=AcceptedStateCodec.Compact(state,OperationIdentity.Create(state.WorldId,next,"compact-lost-receipt"),"compact-lost-receipt",next,OperationIdentity.CompactionPayloadHash(sale.CommandSequence),state.Revision,AcceptedStateCodec.ComputeHash(state),sale.CommandSequence,64902);
            }
            if(alterWitness)
            {
                // A valid, self-consistent but different witnessed price is not the
                // exact prepared effect and cannot be accepted as its lost ack.
                state.Receipts.Single(x=>x.OperationId==sale.OperationId).PayloadHash=new string('f',64);
            }
            using var restarted=new RecoveryCoordinator(directory,new AlwaysLiveVerifier());
            var selected=AttachFor(state);selected.Capabilities=["dispatch","arrival","economicRecovery.v1",LogisticsRuntimePlanner.OwnershipCapability];
            Assert.IsType<EffectIdle>(restarted.Attach(selected,clock));
            var status=restarted.GetCommandStatus(new GetCommandStatus {MessageType="getCommandStatus",ClientRequestId=sale.ClientRequestId,WorldId=state.WorldId,RunId="run-one"});
            if(compactReceipt||alterWitness)
            {
                Assert.Equal("rejected",status.Status);Assert.True(status.ConfirmedTerminal);
                if(compactReceipt)Assert.Contains("outcome is unknown",status.Reason);
            }
            else {Assert.Equal("accepted",status.Status);Assert.Equal(state.Revision,status.AcceptedRevision);}
            Assert.IsType<EffectIdle>(restarted.Poll(PollFor(state),clock));
            Assert.Empty(AcceptedStateCodec.ReadCapsule(restarted.GetAcceptedState(new GetAcceptedState {MessageType="getAcceptedState",WorldId=state.WorldId,RunId="run-one"}).AcceptedCapsule!).ActiveShipments);
        }
        finally {Directory.Delete(directory,true);}
    }
}
