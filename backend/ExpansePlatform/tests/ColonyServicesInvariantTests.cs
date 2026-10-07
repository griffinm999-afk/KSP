using Expanse.Domain.Colonies;

namespace Expanse.Clock.Tests;

public sealed class ColonyServicesInvariantTests
{
    [Fact]
    public void ReservationProtectsSupportFloorAndNeverCreditsPhysicalTankInPureDomain()
    {
        var (state,env,colony,target)=Fixture();
        var accepted=Service(state,env,colony,target,20);Assert.Equal("accepted",accepted.Outcome);
        Assert.Equal(100*ColonyLimits.Units,accepted.State.Colonies[0].Stock[0].Amount);
        Assert.Equal(20*ColonyLimits.Units,accepted.State.Colonies[0].Stock[0].Reserved);
        Assert.Equal(10*ColonyLimits.Units,target.Amount);
        Assert.Contains("already",Service(accepted.State,env,colony,target,1).Reason);
        Assert.Contains("inputs",Service(state,env,colony,target,91).Reason);
        var stale=Fixture();stale.target.ContextKey="other epoch";Assert.Equal("rejected",Service(stale.state,stale.env,stale.colony,stale.target,1).Outcome);
        target.QualifiedWorker=false;Assert.Equal("rejected",Service(state,env,colony,target,1).Outcome);Assert.Empty(state.ServiceOperations);
    }
    [Fact]
    public void PhysicalCreditRequiresDurableDebitAndVerifiedReadbackWithNoDuplicateCharge()
    {
        var (state,env,colony,target)=Fixture();var accepted=Service(state,env,colony,target,20).State;string id=accepted.ServiceOperations.Single().Id;
        Assert.Throws<InvalidDataException>(()=>ColonyEngine.CompletePreparedService(accepted,id,1,"unproven"));
        var held=ColonyEngine.HoldPreparedService(accepted,id,"actual-provider","source=100; physical=10");
        Assert.Equal(80*ColonyLimits.Units,held.Colonies[0].Stock[0].Amount);Assert.Equal(0,held.Colonies[0].Stock[0].Reserved);Assert.Equal("held",held.Effects.Single().State);
        Assert.Throws<InvalidDataException>(()=>ColonyEngine.HoldPreparedService(held,id,"actual-provider","retry"));
        Assert.Throws<InvalidDataException>(()=>ColonyEngine.RejectPreparedService(held,id,1,"unknown physical credit","guess"));
        var complete=ColonyEngine.CompletePreparedService(held,id,1,"source=80; physical=30 exact readback");
        Assert.Equal(80*ColonyLimits.Units,complete.Colonies[0].Stock[0].Amount);Assert.Equal("applied",complete.Effects.Single().State);
        Assert.Same(complete,ColonyEngine.CompletePreparedService(complete,id,2,"retry"));Assert.Equal(-20*ColonyLimits.Units,complete.Journal.Last().ResourceDelta);
        ColonyStateCodec.Deserialize(ColonyStateCodec.Serialize(complete));
    }
    [Fact]
    public void CancelOrConfirmedRollbackReleasesReservationWithoutInventingStock()
    {
        var (state,env,colony,target)=Fixture();var accepted=Service(state,env,colony,target,20).State;string id=accepted.ServiceOperations.Single().Id;
        var cancelled=ColonyEngine.Execute(accepted,new() {OperationId=Guid.NewGuid().ToString("D"),ContextKey=env.ContextKey,ExpectedRevision=accepted.Revision,ColonyId=colony.Id,TargetId=id,Kind="cancelService"},env);
        Assert.Equal("accepted",cancelled.Outcome);Assert.Equal(100*ColonyLimits.Units,cancelled.State.Colonies[0].Stock[0].Amount);Assert.Equal(0,cancelled.State.Colonies[0].Stock[0].Reserved);
        var rollback=ColonyEngine.RejectPreparedService(accepted,id,1,"confirmed exact rollback","physical still=10");
        Assert.Equal(100*ColonyLimits.Units,rollback.Colonies[0].Stock[0].Amount);Assert.Equal(0,rollback.Colonies[0].Stock[0].Reserved);Assert.Equal("cancelled",rollback.ServiceOperations.Single().State);
    }
    [Fact]
    public void RecurringPolicyDefaultsOffAndQueuesAtMostFourCurrentCostedServices()
    {
        var (state,env,colony,target)=Fixture();env.Ut=1;target.Capacity=20*ColonyLimits.Units;
        Assert.Empty(ColonyEngine.Advance(state,env).ServiceOperations);
        state.ServicePolicies.Add(new() {ColonyId=colony.Id,AutomaticEnabled=true});
        for(uint part=43;part<48;part++){colony.Facilities[0].PartIds.Add(part);env.Services.Targets.Add(new() {ColonyId=colony.Id,FacilityId=target.FacilityId,PartId=part,DepotId=target.DepotId,SourceResource="Machinery",DestinationResource="Machinery",Capacity=20*ColonyLimits.Units,Current=true,ContextKey=env.ContextKey,CanApply=true,QualifiedWorker=true});}
        var advanced=ColonyEngine.Advance(state,env);Assert.Equal(4,advanced.ServiceOperations.Count);Assert.True(advanced.Colonies[0].Stock[0].Reserved<=90*ColonyLimits.Units);
        var op=advanced.ServiceOperations.First();var rollback=ColonyEngine.RejectPreparedService(advanced,op.Id,1,"rollback","before unchanged");
        env.Ut=2;var next=ColonyEngine.Advance(rollback,env);Assert.DoesNotContain(next.ServiceOperations,o=>o.Id!=op.Id && o.PartId==op.PartId);
    }
    [Fact]
    public void BatteryAndShortStorageDeltaCannotCertifyPowerOrHeat()
    {
        var report=new ColonyUtilityReport {ElectricCharge=100000,ConnectedCapacity=100000,NetStorageEcPerSecond=10,WindowSeconds=30};
        Assert.False(ColonyUtilityQualification.PowerSupported(report));Assert.False(ColonyUtilityQualification.HeatSupported(report));
        report.ActualConnectedPath=true;report.FullDemandAccounted=true;report.ContinuousSourceQualified=true;report.DistributionReachQualified=true;report.NominalGenerationEcPerSecond=.75;report.NominalDemandEcPerSecond=.5;
        Assert.True(ColonyUtilityQualification.PowerSupported(report));report.NominalDemandEcPerSecond=1;Assert.False(ColonyUtilityQualification.PowerSupported(report));
        report.HeatRejectionQualified=true;report.MinimumTemperatureMargin=200;report.MinimumCoreShutdownMargin=1000;Assert.True(ColonyUtilityQualification.HeatSupported(report));report.MinimumCoreShutdownMargin=-1;Assert.False(ColonyUtilityQualification.HeatSupported(report));
    }
    static ColonyResult Service(ColonyState state,ColonyEnvironment env,ColonyRecord colony,ColonyServiceTarget target,int units)=>ColonyEngine.Execute(state,new() {OperationId=Guid.NewGuid().ToString("D"),ContextKey=env.ContextKey,ExpectedRevision=state.Revision,ColonyId=colony.Id,TargetId=target.FacilityId,Kind="serviceFacility",Fields=new() {{"PartId",target.PartId.ToString()},{"DestinationResource",target.DestinationResource},{"ServiceQuoteHash",target.QuoteHash},{"AmountMicroUnits",(units*ColonyLimits.Units).ToString()}}},env);
    static (ColonyState state,ColonyEnvironment env,ColonyRecord colony,ColonyServiceTarget target) Fixture()
    {
        var state=ColonyEngine.Create(Guid.NewGuid().ToString("D"),0);var facility=new ColonyFacility {Id=Guid.NewGuid().ToString("D"),VesselId=Guid.NewGuid().ToString("D"),Name="Real tank fixture",PartIds=new(){42}};
        var colony=new ColonyRecord {Id=Guid.NewGuid().ToString("D"),Name="Service invariant fixture",Site=new(){Body="Minmus"},Facilities=new(){facility},Stock=new(){new(){Resource="Machinery",Amount=100*ColonyLimits.Units,Capacity=1000*ColonyLimits.Units,SupportFloor=10*ColonyLimits.Units}}};state.Colonies.Add(colony);
        var env=new ColonyEnvironment {WorldId=state.WorldId,ContextKey=state.WorldId+"/epoch",AvailableFunds=1000000};
        var target=new ColonyServiceTarget {ColonyId=colony.Id,FacilityId=facility.Id,PartId=42,DepotId=Guid.NewGuid().ToString("D"),SourceResource="Machinery",DestinationResource="Machinery",Amount=10*ColonyLimits.Units,Capacity=200*ColonyLimits.Units,QuoteHash="current",ContextKey=env.ContextKey,Current=true,CanApply=true,QualifiedWorker=true,Provider="fixture",WorkerWitness="Independent domain fixture; actual runtime not certified"};env.Services.Targets.Add(target);return(state,env,colony,target);
    }
}
