using Expanse.Domain.Colonies;

namespace Expanse.Clock.Tests;

public sealed class ColonyReactorContinuationTests
{
    const double Reserve=6*ColonyLimits.KerbinDay;
    static ColonyReactorContinuationProof Proof()=>new()
    {
        WorldId=Guid.NewGuid().ToString("D"),VesselId=Guid.NewGuid().ToString("D"),HardwareHash=new('a',64),ProviderHash=new('b',64),RecipeHash=new('c',64),
        ObservedUt=100,ExpiresUt=100+2*Reserve,FullThrottleSeconds=30,GenerationEcPerSecond=750,FullLoadHeatKw=1500,NominalCoolingKw=1800,
        ObservedRejectionKw=1500,MinimumShutdownMarginK=250,MinimumLoopOperatingMarginK=100,FuelWasteEnduranceSeconds=10*Reserve
    };
    static bool Continue(ColonyReactorContinuationProof p,double ut,double fuel,double reserve,out string reason)=>
        ColonyReactorContinuation.CanContinue(p,p.WorldId,p.VesselId,p.HardwareHash,p.ProviderHash,p.RecipeHash,ut,fuel,reserve,out reason);
    [Fact] public void EntireRequestedReserveMustFitRemainingAuthorizedHorizon()
    {
        var p=Proof();Assert.True(Continue(p,p.ExpiresUt-Reserve,Reserve,Reserve,out _));
        Assert.False(Continue(p,p.ExpiresUt-Reserve+1,Reserve,Reserve,out var reason));Assert.Contains("horizon",reason);
        Assert.False(Continue(p,p.ExpiresUt-1,Reserve,Reserve,out _));Assert.False(Continue(p,99,Reserve,Reserve,out _));
    }
    [Theory] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)] [InlineData(double.MaxValue)]
    public void NonfiniteOrOverflowingTimeCannotContinue(double ut)=>Assert.False(Continue(Proof(),ut,Reserve,Reserve,out _));
    [Fact] public void ActualFuelDepletionAndChangedProviderOrHardwareHoldWithoutExtendingProof()
    {
        var p=Proof();double expiry=p.ExpiresUt;
        Assert.False(Continue(p,101,Reserve-1,Reserve,out _));
        Assert.False(ColonyReactorContinuation.CanContinue(p,p.WorldId,p.VesselId,new('d',64),p.ProviderHash,p.RecipeHash,101,Reserve,Reserve,out _));
        Assert.False(ColonyReactorContinuation.CanContinue(p,p.WorldId,p.VesselId,p.HardwareHash,new('d',64),p.RecipeHash,101,Reserve,Reserve,out _));
        Assert.Equal(expiry,p.ExpiresUt);
    }
    [Fact] public void SmallPositiveTrendCannotJustifyLongOfflinePeriod()
    {
        var p=Proof();p.MaximumCoreRiseKelvinPerSecond=.01;
        Assert.Throws<InvalidDataException>(()=>ColonyStateCodec.ValidateReactorContinuation(p));
        p.ExpiresUt=p.ObservedUt+(p.MinimumShutdownMarginK-100)/.01;
        ColonyStateCodec.ValidateReactorContinuation(p);Assert.False(Continue(p,p.ObservedUt,Reserve,Reserve,out _));
        p.MaximumCoreRiseKelvinPerSecond=0;p.MaximumLoopRiseKelvinPerSecond=.001;
        p.ExpiresUt=p.ObservedUt+p.MinimumLoopOperatingMarginK/.001;
        ColonyStateCodec.ValidateReactorContinuation(p);Assert.False(Continue(p,p.ObservedUt,Reserve,Reserve,out _));
    }
    [Fact] public void HeatRejectionMustActuallyCoverFullPowerAndPreserveHeadroom()
    {
        var p=Proof();p.ObservedRejectionKw=p.FullLoadHeatKw-1;Assert.Throws<InvalidDataException>(()=>ColonyStateCodec.ValidateReactorContinuation(p));
        p.ObservedRejectionKw=p.FullLoadHeatKw;p.NominalCoolingKw=p.FullLoadHeatKw*1.09;Assert.Throws<InvalidDataException>(()=>ColonyStateCodec.ValidateReactorContinuation(p));
    }
    [Fact] public void OnlyContinuousStableNativeSamplesQualify()
    {
        var w=new ColonyReactorStabilityWindow();
        for(int t=0;t<30;t++)Assert.False(w.Observe("actual-loop",t,750,750,true));
        Assert.True(w.Observe("actual-loop",30,750,750,true));Assert.Equal(30,w.Seconds);
        Assert.False(w.Observe("actual-loop",31,750.1,750,true));Assert.Equal(0,w.Seconds);
        Assert.False(w.Observe("actual-loop",32,750.1,750,false));Assert.Equal(0,w.Seconds);
    }
    [Fact] public void UnobservedGapQuickloadOrChangedConfigurationResetsEvidence()
    {
        var w=new ColonyReactorStabilityWindow();w.Observe("world-a",0,750,750,true);w.Observe("world-a",10,750,750,true);
        Assert.False(w.Observe("world-a",21,750,750,true));Assert.Equal(0,w.Seconds);
        Assert.False(w.Observe("world-b",22,750,750,true));Assert.Equal(0,w.Seconds);
        Assert.False(w.Observe("world-b",1,750,750,true));Assert.Equal(0,w.Seconds);
    }
    [Fact] public void OnlyFreshLoadedFailureInExactContextMayRevokeSavedAuthority()
    {
        Assert.True(ColonyReactorContinuation.ShouldRevokeProof("selected/epoch","selected/epoch",true,true,110,100));
        Assert.False(ColonyReactorContinuation.ShouldRevokeProof("selected/epoch","other/epoch",true,true,110,100));
        Assert.False(ColonyReactorContinuation.ShouldRevokeProof("selected/epoch","selected/epoch",false,true,110,100));
        Assert.False(ColonyReactorContinuation.ShouldRevokeProof("selected/epoch","selected/epoch",true,false,110,100));
        Assert.False(ColonyReactorContinuation.ShouldRevokeProof("selected/epoch","selected/epoch",true,true,111,100));
        Assert.False(ColonyReactorContinuation.ShouldRevokeProof("selected/epoch","selected/epoch",true,true,99,100));
        Assert.False(ColonyReactorContinuation.ShouldRevokeProof("selected/epoch","selected/epoch",true,true,double.NaN,100));
        Assert.True(ColonyReactorContinuation.ShouldRevokeProof("selected/epoch","selected/epoch",true,true,10000,100,true));
        Assert.False(ColonyReactorContinuation.ShouldRevokeProof("selected/epoch","other/epoch",true,true,10000,100,true));
        Assert.False(ColonyReactorContinuation.ShouldRevokeProof("selected/epoch","selected/epoch",true,true,99,100,true));
    }
    [Fact] public void PersistedProofIsDetachedFromObserverAndRetainsExactModelTerms()
    {
        var proof=Proof();var state=new ColonyState {WorldId=proof.WorldId,Colonies=[new(){Id=Guid.NewGuid().ToString("D"),Name="Pure proof fixture",Site=new(){Body="Minmus",RadiusMeters=400},
            Facilities=[new(){Id=proof.VesselId,VesselId=proof.VesselId,Qualification=new(){ReactorContinuation=proof}}]}]};
        var copy=ColonyStateCodec.Copy(state);var saved=copy.Colonies.Single().Facilities.Single().Qualification.ReactorContinuation!;
        Assert.NotSame(proof,saved);Assert.Equal(proof.ExpiresUt,saved.ExpiresUt);Assert.Equal(proof.Model,saved.Model);
        proof.ExpiresUt=101;Assert.NotEqual(proof.ExpiresUt,saved.ExpiresUt);
        var reload=ColonyStateCodec.Deserialize(ColonyStateCodec.Serialize(copy));Assert.Equal(saved.ExpiresUt,reload.Colonies.Single().Facilities.Single().Qualification.ReactorContinuation!.ExpiresUt);
    }
}
