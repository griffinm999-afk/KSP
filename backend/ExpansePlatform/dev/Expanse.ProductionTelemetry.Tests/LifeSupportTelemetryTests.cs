using Expanse.WorldBridge;
using Core=Expanse.Clock.Core;

namespace Expanse.Clock.Tests;
public sealed class LifeSupportTelemetryTests
{
    [Fact] public void ExactNativeGetterReferenceIsOneUseAndOwnerBound()
    {
        var scope=new LifeSupportTelemetryScope();var owner=new object();var recipe=new object();var f=scope.Enter(owner,"load1",true);
        scope.Bind(owner,recipe,false);Assert.False(scope.Consume(new object(),"load1",recipe,out _));Assert.False(scope.Consume(owner,"load2",recipe,out _));Assert.False(scope.Consume(owner,"load1",new object(),out _));
        Assert.True(scope.Consume(owner,"load1",recipe,out bool ec));Assert.False(ec);Assert.False(scope.Consume(owner,"load1",recipe,out _));scope.Exit(f);
    }
    [Fact] public void NestedSentinelAndSameOwnerScopeCannotBorrowRecipes()
    {
        var scope=new LifeSupportTelemetryScope();var owner=new object();var recipe=new object();var outer=scope.Enter(owner,"a",true);scope.Bind(owner,recipe,true);
        var sentinel=scope.Enter(owner,"a",false);Assert.False(scope.Consume(owner,"a",recipe,out _));scope.Exit(sentinel);
        var child=scope.Enter(owner,"a",true);Assert.False(scope.Consume(owner,"a",recipe,out _));scope.Exit(child);Assert.True(scope.Consume(owner,"a",recipe,out bool ec));Assert.True(ec);scope.Exit(outer);
    }
    [Fact] public void ResetCannotRestoreOldRecipeFromFinalizer()
    {var scope=new LifeSupportTelemetryScope();var owner=new object();var recipe=new object();var f=scope.Enter(owner,"a",true);scope.Bind(owner,recipe,false);scope.Reset();scope.Exit(f);Assert.False(scope.Consume(owner,"a",recipe,out _));}
    [Theory][InlineData(100,100,2,true)][InlineData(100,50,2,true)][InlineData(100,101,2,false)][InlineData(1,1,2,false)][InlineData(100,100,0,false)][InlineData(100,100,double.NaN,false)][InlineData(double.PositiveInfinity,100,2,false)]
    public void OriginalCatchUpIntervalIsValidatedWithoutRelabeling(double observed,double end,double seconds,bool valid)=>Assert.Equal(valid,LifeSupportTelemetryScope.Interval(observed,end,seconds));
    [Theory][InlineData(0,0,0)][InlineData(0.5,0.5,1)][InlineData(1,0.4,0.8)]
    public void SupplyBrokerAcceptedQuantitiesAndIndependentEcFailure(double supplies,double mulch,double factor)
    {
        var ledger=new ProductionTelemetryLedger();var broker=new object();var part=new object();ledger.Reset("a");var supply=ledger.Enter("a",broker,part,"supply",100,2,true);
        ledger.Record(broker,part,"Supplies",1,supplies,false);ledger.Record(broker,part,"Mulch",1,-mulch,true);Assert.True(ledger.Complete(supply,true,factor));
        var ec=ledger.Enter("a",broker,part,"ec",100,2,true);ledger.Record(broker,part,"ElectricCharge",2,1,false);Assert.False(ledger.Complete(ec,false,1));
        Assert.True(supply.Valid);Assert.Equal(supplies,supply.Inputs["Supplies"]);Assert.Equal(mulch,supply.Outputs["Mulch"]);Assert.False(ec.Valid);
    }
    static Core.LifeSupportSupplyStream Supply()=>new(100,50,2,1,5,2,.5,.0025,.0025,.00125,.00125,.002,.001);
    static Core.ColonyLifeSupportTelemetry Value()=>new("partial","accepted",100,Supply(),null);
    [Fact] public void WireAcceptsHistoricalCatchUpAndMissingEcWithoutInventingZero()=>Core.ColonyLifeSupportTelemetryProtocol.Validate(Value(),null,"loaded",100,5);
    [Fact] public void WireRejectsStaleEpochCoverageAndImpossibleAcceptedTotals()
    {
        foreach(var s in new[]{Supply() with {SampleUt=89},Supply() with {ProcessedEndUt=101},Supply() with {CrewCount=4},Supply() with {SuppliesConsumed=1},Supply() with {ConfiguredSuppliesPerSecond=.1},Supply() with {TimeFactor=3},Supply() with {CaptureSequence=0}})
            Assert.Throws<InvalidDataException>(()=>Core.ColonyLifeSupportTelemetryProtocol.Validate(Value() with {Supply=s},null,"loaded",100,5));
        Assert.Throws<InvalidDataException>(()=>Core.ColonyLifeSupportTelemetryProtocol.Validate(Value(),null,"snapshot",100,5));
    }
    [Fact] public void SupplyZeroAndEcZeroAreLegitimateQualifiedObservations()
    {Core.ColonyLifeSupportTelemetryProtocol.Validate(Value() with {Supply=Supply() with {SuppliesConsumed=0,MulchProduced=0,TimeFactor=0},CrewElectricity=new(100,100,2,2,5,0,.05,0)},null,"loaded",100,5);}
}
