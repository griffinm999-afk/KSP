using System.Text;
using Expanse.Domain.Colonies;

namespace Expanse.Clock.Tests;

public sealed class ColonyConstructionActivationTests
{
    static string Id()=>Guid.NewGuid().ToString("D");
    static string Hash(string s)=>ColonyStateCodec.Hash(Encoding.UTF8.GetBytes(s));
    static (ColonyState State,ColonyEnvironment Env) Anchored()
    {
        var template=new ColonyTemplate {Id="test-power",Name="Explicit pure test power",Hash=new('a',64),CraftSha256=new('b',64),RuntimeCertified=true,
            CertificationEvidence="Explicit domain fixture, not a native certificate",BuildFunds=1000,LaborFunds=200,LaborSeconds=10,ExpectedPartCount=1,
            WidthMeters=10,LengthMeters=10,MinX=-4,MaxX=4,MinZ=-4,MaxZ=4,ClearanceMetres=1,Materials=[new(){Resource="Metal",Amount=ColonyLimits.Units}]};
        var plot=new ColonyPlot {Id=Id(),WidthMeters=10,LengthMeters=10,SurveyHash=new('c',64),TemplateId=template.Id,TemplateHash=template.Hash,EvidenceContext="test-context",SurveyProvenance="Explicit domain fixture"};
        var colony=new ColonyRecord {Id=Id(),Name="Test colony",Site=new(){Body="Minmus",RadiusMeters=400},Charter=new(){Purpose="Test",CashFloor=0,FoundingBudget=100000,SpendingLimit=100000,ReserveDays=6,GrowthPolicy="approval"},
            Plots=[plot],Stock=[new(){Resource="Metal",Amount=5*ColonyLimits.Units,Capacity=10*ColonyLimits.Units}]};
        var state=new ColonyState {WorldId=Id(),Colonies=[colony]};var env=new ColonyEnvironment {WorldId=state.WorldId,ContextKey="test-context",AvailableFunds=20000,Templates=[template]};
        var command=new ColonyCommand {OperationId=Id(),ColonyId=colony.Id,ContextKey=env.ContextKey,Kind="approveConstruction",Fields=new(){{"TemplateId",template.Id},{"TemplateHash",template.Hash},{"PlotId",plot.Id}}};
        var result=ColonyEngine.Execute(state,command,env);Assert.Equal("accepted",result.Outcome);state=result.State;
        var funds=state.Effects.Single();state=ColonyEngine.MarkEffectApplying(state,funds.Id,"Explicit test funds","funds=20000");state=ColonyEngine.CompleteFundsEffect(state,funds.Id,20000,19000,0,"funds=19000");
        string order=state.Construction.Single().Id;env.Ut=10;env.ConstructionLaborByOrder[order]=new(){ProviderId="Paid test labor",PoolId="test-pool",ContextKey=env.ContextKey,EvidenceHash=new('d',64),Workers=1,PoolCapacity=1,ValidFromUt=0,ValidThroughUt=10,CostIncludedInPaidEscrow=true};
        state=ColonyEngine.Advance(state,env);string payload="Explicit test placement; no game mutation";
        state=ColonyEngine.PrepareConstructionPlacement(state,order,env,new(){OperationId=Id(),EffectId=Id(),Phase="intent",ContextKey=env.ContextKey,RequestPayload=payload,PayloadHash=Hash(payload),RequestFingerprint=new('e',64),EscrowWitness=ColonyEngine.ConstructionEscrowWitness(state,order),BeforeWitness="Explicit test world"});
        var placement=state.Construction.Single().Placement;
        state=ColonyEngine.ObserveConstructionPlacement(state,order,template,new(){WorldId=state.WorldId,OperationId=placement.OperationId,RequestFingerprint=placement.RequestFingerprint,Phase="Anchored",AssemblyAttempted=true,ObservedUt=10,AfterWitness="Explicit anchored test readback",VesselId=Id(),FoundationId=Id(),Anchored=true,CraftToPersistentIds=new(){{100,500}}});
        return (state,env);
    }
    [Fact] public void InterruptedPartialActivationRetainsExactIntentAndOnlyAfterReadbackCompletes()
    {
        var (state,_)=Anchored();string order=state.Construction.Single().Id;long stock=state.Colonies.Single().Stock.Single().Amount;
        var applying=ColonyEngine.PrepareConstructionActivation(state,order,"world/epoch","reactor500:disabled;rad501:off","reactor500:enabled;rad501:on");
        var child=applying.Effects.Single(e=>e.Kind==ColonyEngine.ConstructionActivationKind);Assert.Equal("applying",child.State);
        Assert.True(ColonyEngine.IsConstructionActivationEffect(applying,order,child.Id));
        var held=ColonyEngine.ObserveConstructionActivation(ColonyStateCodec.Deserialize(ColonyStateCodec.Serialize(applying)),order,"reactor500:disabled;rad501:on");
        Assert.Equal("held",held.Effects.Single(e=>e.Id==child.Id).State);Assert.Equal(stock,held.Colonies.Single().Stock.Single().Amount);
        Assert.Throws<InvalidDataException>(()=>ColonyEngine.PrepareConstructionActivation(held,order,"world/newEpoch","before","after"));
        var complete=ColonyEngine.ObserveConstructionActivation(held,order,"reactor500:enabled;rad501:on");
        Assert.Equal("applied",complete.Effects.Single(e=>e.Id==child.Id).State);Assert.Equal(stock,complete.Colonies.Single().Stock.Single().Amount);
        Assert.Equal(state.Colonies.Single().SpentFunds,complete.Colonies.Single().SpentFunds);
        Assert.Same(complete,ColonyEngine.ObserveConstructionActivation(complete,order,"reactor500:disabled;rad501:off"));
    }
    [Fact] public void DifferentSavedPartOrParentLineageCannotReconcile()
    {
        var (state,_)=Anchored();string order=state.Construction.Single().Id;
        var applying=ColonyEngine.PrepareConstructionActivation(state,order,"epoch","off","on");
        var changed=ColonyStateCodec.Copy(applying);changed.Colonies.Single().Facilities.Single().PartIds=[501];
        Assert.Throws<InvalidDataException>(()=>ColonyStateCodec.Serialize(changed));
        changed=ColonyStateCodec.Copy(applying);changed.Effects.Single(e=>e.Kind==ColonyEngine.ConstructionActivationKind).AfterWitness="binding="+new string('f',64)+";context=epoch;settings=on";
        Assert.Throws<InvalidDataException>(()=>ColonyStateCodec.Serialize(changed));
    }
    [Fact] public void UnpaidOrNonCommissioningPackageAndOversizedTargetAreRejectedBeforeIntent()
    {
        var (state,_)=Anchored();string order=state.Construction.Single().Id;
        Assert.Throws<InvalidDataException>(()=>ColonyEngine.PrepareConstructionActivation(state,order,"epoch","off",new string('x',3501)));
        Assert.DoesNotContain(state.Effects,e=>e.Kind==ColonyEngine.ConstructionActivationKind);
        state.Colonies.Single().Facilities.Single().State="operational";
        Assert.Throws<InvalidDataException>(()=>ColonyEngine.PrepareConstructionActivation(state,order,"epoch","off","on"));
    }
    [Fact] public void UnrelatedUncertainEffectIsNeverExempted()
    {
        var (state,_)=Anchored();string order=state.Construction.Single().Id;
        state.Effects.Add(new(){Id=Id(),OperationId=Id(),ColonyId=state.Colonies.Single().Id,TargetId="other",Kind="test",State="held"});
        Assert.False(ColonyEngine.IsConstructionActivationEffect(state,order,state.Effects.Last().Id));
        Assert.Throws<InvalidDataException>(()=>ColonyEngine.PrepareConstructionActivation(state,order,"epoch","off","on"));
    }
    [Fact] public void TerminalCompactionRetainsAppliedInitialActivationAuthority()
    {
        var (state,env)=Anchored();string order=state.Construction.Single().Id;
        state=ColonyEngine.PrepareConstructionActivation(state,order,"epoch","off","on");state=ColonyEngine.ObserveConstructionActivation(state,order,"on");string activation=ColonyEngine.ConstructionActivationId(state.Construction.Single());
        while(state.Effects.Count<ColonyLimits.Effects)state.Effects.Add(new(){Id=Id(),OperationId=Id(),ColonyId=state.Colonies.Single().Id,TargetId="old",Kind="oldTerminalTest",State="applied"});
        var supplier=new ColonySupplier {Id="test-metal",Resource="Metal",Available=10*ColonyLimits.Units,FundsPerUnit=1,MassCapacityMicroTonnes=100000000,VolumeCapacityMilliLiters=100000000,ConcurrentCapacity=1,TravelSeconds=10};state.Suppliers.Add(supplier);
        var command=new ColonyCommand {OperationId=Id(),ColonyId=state.Colonies.Single().Id,ExpectedRevision=state.Revision,ContextKey=env.ContextKey,Kind="approveTrade",Fields=new(){{"SupplierId",supplier.Id},{"AmountMicroUnits",ColonyLimits.Units.ToString()},{"QuotedFunds","1"}}};
        var result=ColonyEngine.Execute(state,command,env);Assert.True(result.Outcome=="accepted",result.Reason);Assert.Equal(ColonyLimits.Effects,result.State.Effects.Count);
        Assert.Equal("applied",result.State.Effects.Single(e=>e.Id==activation).State);
        Assert.Throws<InvalidDataException>(()=>ColonyEngine.PrepareConstructionActivation(result.State,order,"newEpoch","off","on"));
    }
}
