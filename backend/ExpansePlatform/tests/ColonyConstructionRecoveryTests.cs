using Expanse.Domain.Colonies;

namespace Expanse.Clock.Tests;

public sealed partial class ColonyConstructionInvariantTests
{
    static (ColonyState State, ColonyEnvironment Env, ColonyTemplate Template, ColonyCommand Command) HeldRetry()
    {
        var (paid, env, template) = Paid(); var state = Finished(paid, env); var id = state.Construction.Single().Id;
        state = ColonyEngine.PrepareConstructionPlacement(state, id, env, Intent(state, env));
        var p = state.Construction.Single().Placement;
        state = ColonyEngine.ObserveConstructionPlacement(state, id, template, new()
        { WorldId = state.WorldId, OperationId = p.OperationId, RequestFingerprint = p.RequestFingerprint, Phase = "RecoveryHold", AfterWitness = Sha('f'), Reason = "Original pre-assembly geometry failure", ObservedUt = env.Ut });
        env.ConstructionRecovery[id] = new()
        {
            WorldId = state.WorldId, ContextKey = env.ContextKey, OrderId = id, OperationId = p.OperationId,
            RequestFingerprint = p.RequestFingerprint, PayloadHash = p.PayloadHash, EscrowWitness = p.EscrowWitness,
            Phase = "RecoveryHold", Current = true, ObservedUt = env.Ut, AfterWitness = Sha('f')
        };
        var command = new ColonyCommand { Kind = "retryConstructionPlacement", OperationId = Id(), TargetId = id,
            ColonyId = state.Construction.Single().ColonyId, ContextKey = env.ContextKey, ExpectedRevision = state.Revision };
        command.Fields["PlacementOperationId"] = p.OperationId; command.Fields["RequestFingerprint"] = p.RequestFingerprint; command.Fields["PayloadHash"] = p.PayloadHash;
        return (state, env, template, command);
    }

    [Fact]
    public void RecoveryWitnessAndOriginalRetryCommandRoundTripThroughExistingWireProtocol()
    {
        var (state,env,_,command)=HeldRetry();
        var request=ColonyManagementWire.DecodeRequest(ColonyManagementWire.Encode(new ColonyManagementWireRequest {RequestId=Id(),Kind="submit",Command=command}));
        Assert.Equal(command.OperationId,request.Command!.OperationId);Assert.Equal(command.Fields,request.Command.Fields);
        var response=ColonyManagementWire.DecodeResponse(ColonyManagementWire.Encode(new ColonyManagementWireResponse {RequestId=Id(),Outcome="snapshot",
            Snapshot=new ColonyManagementSnapshot {ContextKey=env.ContextKey,State=state,ConstructionRecovery=env.ConstructionRecovery.Values.ToList()}}));
        Assert.Equal(command.TargetId,response.Snapshot!.ConstructionRecovery.Single().OrderId);
        Assert.True(response.Snapshot.ConstructionRecovery.Single().Current);
        Assert.True(ColonyEngine.CanRetryConstructionPlacement(response.Snapshot.State!,command.TargetId,env,out _));
        env.ConstructionRecovery[command.TargetId].Current=false;
        Assert.False(ColonyEngine.CanRetryConstructionPlacement(state,command.TargetId,env,out _));
    }

    [Fact]
    public void RetryAuthorizationSerializesOriginalHoldBeforeProviderAndPreservesAllPaidFacts()
    {
        var (state, env, _, command) = HeldRetry(); var before = ColonyEngine.ConstructionEscrowWitness(state, command.TargetId);
        var result = ColonyEngine.Execute(state, command, env); Assert.True(result.Outcome == "accepted", result.Reason);
        var recovered = ColonyStateCodec.Deserialize(ColonyStateCodec.Serialize(result.State));
        var order = recovered.Construction.Single(); var retry = recovered.Effects.Single(e => e.Kind == ColonyEngine.ConstructionRetryKind);
        Assert.Equal("prepared", retry.State); Assert.Equal(0, retry.FundsDelta); Assert.Contains("Original pre-assembly geometry failure", retry.BeforeWitness);
        Assert.Equal("RecoveryHold", order.Placement.Phase); Assert.Equal("held", order.State); Assert.False(order.Placement.AssemblyAttempted);
        Assert.Equal(before, ColonyEngine.ConstructionEscrowWitness(recovered, order.Id)); Assert.Equal(state.Colonies.Single().SpentFunds, recovered.Colonies.Single().SpentFunds);
        Assert.Equal(state.Colonies.Single().Stock.Select(s => (s.Amount,s.Reserved,s.ImportedAmount)), recovered.Colonies.Single().Stock.Select(s => (s.Amount,s.Reserved,s.ImportedAmount)));
        Assert.Equal(state.Construction.Single().Placement.RequestPayload, order.Placement.RequestPayload);
        Assert.Contains(recovered.Journal, j => j.Kind == "constructionRetryOriginalHold" && j.Detail == "Original pre-assembly geometry failure");
        var applying = ColonyEngine.MarkConstructionRetryApplying(recovered, retry.Id);
        Assert.Equal(retry.BeforeWitness, applying.Effects.Single(e => e.Id == retry.Id).BeforeWitness);
        Assert.Equal("held", applying.Construction.Single().State);
    }

    [Fact]
    public void RetryDuplicateAndLostAckDoNotAddChargesEffectsOrAuthorizations()
    {
        var (state, env, _, command) = HeldRetry(); var first = ColonyEngine.Execute(state, command, env);
        env.ConstructionRecovery.Clear(); // Lost acknowledgement/provider no longer available.
        var duplicate = ColonyEngine.Execute(first.State, command, env);
        Assert.Equal("duplicate", duplicate.Outcome); Assert.Equal(first.State.Revision, duplicate.State.Revision);
        Assert.Single(duplicate.State.Effects.Where(e => e.Kind == ColonyEngine.ConstructionRetryKind));
        command.Fields["PayloadHash"] = Sha('1'); Assert.Equal("rejected", ColonyEngine.Execute(first.State, command, env).Outcome);
    }

    [Theory]
    [InlineData("unavailable")][InlineData("world")][InlineData("context")][InlineData("stale")]
    [InlineData("fingerprint")][InlineData("payload")][InlineData("escrow")][InlineData("operation")]
    [InlineData("assembly")][InlineData("marker")][InlineData("phase")][InlineData("package")]
    [InlineData("revision")][InlineData("command-context")][InlineData("command-operation")][InlineData("command-payload")]
    public void RetryRejectsEveryUnsupportedProviderOrCommandWithoutMutatingPaidState(string fault)
    {
        var (state, env, template, command) = HeldRetry(); var w = env.ConstructionRecovery[command.TargetId];
        switch(fault)
        {
            case "unavailable": w.Current=false; break; case "world": w.WorldId=Id(); break;
            case "context": w.ContextKey="other"; break; case "stale": w.ObservedUt=env.Ut-2; break;
            case "fingerprint": w.RequestFingerprint=Sha('1'); break; case "payload": w.PayloadHash=Sha('1'); break;
            case "escrow": w.EscrowWitness=Sha('1'); break; case "operation": w.OperationId=Id(); break;
            case "assembly": w.AssemblyAttempted=true; break; case "marker": w.MarkedVessels=1; break;
            case "phase": w.Phase="Created"; break; case "package": template.Hash=Sha('1'); break;
            case "revision": command.ExpectedRevision--; break; case "command-context": command.ContextKey="other"; break;
            case "command-operation": command.Fields["PlacementOperationId"]=Id(); break; case "command-payload": command.Fields["PayloadHash"]=Sha('1'); break;
        }
        var before=ColonyStateCodec.Hash(ColonyStateCodec.Serialize(state));
        Assert.Equal("rejected",ColonyEngine.Execute(state,command,env).Outcome);
        Assert.Equal(before,ColonyStateCodec.Hash(ColonyStateCodec.Serialize(state)));
        Assert.DoesNotContain(state.Effects,e=>e.Kind==ColonyEngine.ConstructionRetryKind);
    }

    [Fact]
    public void RetryCannotBypassAnotherHoldOrAuthorizeASecondPendingRetry()
    {
        var (state,env,_,command)=HeldRetry();
        state.Effects.Add(new() {Id=Id(),OperationId=Id(),ColonyId=command.ColonyId,TargetId=command.TargetId,Kind="testOtherEffect",State="held"});
        Assert.Equal("rejected",ColonyEngine.Execute(state,command,env).Outcome);
        state.Effects.RemoveAt(state.Effects.Count-1);
        var first=ColonyEngine.Execute(state,command,env); Assert.Equal("accepted",first.Outcome);
        command.OperationId=Id();command.ExpectedRevision=first.State.Revision;
        Assert.Equal("rejected",ColonyEngine.Execute(first.State,command,env).Outcome);
    }

    [Fact]
    public void ProviderRetryReceiptDoesNotFabricatePlacementSuccessAndFreshReadbackResumesOriginalOrder()
    {
        var (state,env,template,command)=HeldRetry();state=ColonyEngine.Execute(state,command,env).State;
        state=ColonyEngine.MarkConstructionRetryApplying(state,command.OperationId);var w=env.ConstructionRecovery[command.TargetId];
        Assert.Throws<InvalidDataException>(()=>ColonyEngine.CompleteConstructionRetry(state,command.OperationId,env.ContextKey,env.Ut,w));
        w.RetryOperationId=command.OperationId;w.Phase="AwaitingPlacement";
        state=ColonyEngine.CompleteConstructionRetry(state,command.OperationId,env.ContextKey,env.Ut,w);
        Assert.Equal("held",state.Construction.Single().State); // Retry acknowledgement is not physical placement readback.
        var p=state.Construction.Single().Placement;
        state=ColonyEngine.ObserveConstructionPlacement(state,command.TargetId,template,new()
        { WorldId=state.WorldId,OperationId=p.OperationId,RequestFingerprint=p.RequestFingerprint,Phase=w.Phase,AfterWitness=w.AfterWitness,ObservedUt=env.Ut,Reason="Original request queued" });
        Assert.Equal("placing",state.Construction.Single().State); Assert.Empty(state.Colonies.Single().Facilities);Assert.False(state.Construction.Single().Placement.AssemblyAttempted);
        Assert.Equal("applied",state.Effects.Single(e=>e.Id==command.OperationId).State);
    }

    [Fact]
    public void AcknowledgedRetryMayStillBeHeldAndColdReconciliationNeverRequiresAnotherRetry()
    {
        var (state,env,_,command)=HeldRetry();state=ColonyEngine.Execute(state,command,env).State;
        state=ColonyStateCodec.Deserialize(ColonyStateCodec.Serialize(ColonyEngine.MarkConstructionRetryApplying(state,command.OperationId)));
        env.ContextKey="new-loaded-context";var w=env.ConstructionRecovery[command.TargetId];w.ContextKey=env.ContextKey;w.RetryOperationId=command.OperationId;
        w.Phase="RecoveryHold";w.Reason="The exact footprint still fails";
        state=ColonyEngine.CompleteConstructionRetry(state,command.OperationId,env.ContextKey,env.Ut,w);
        Assert.Equal("held",state.Construction.Single().State);Assert.Equal("RecoveryHold",state.Construction.Single().Placement.Phase);
        Assert.Equal("applied",state.Effects.Single(e=>e.Id==command.OperationId).State);
        Assert.Equal("rejected",ColonyEngine.Execute(state,command,env).Outcome); // Old context cannot replay as a new mutation.
    }
}
