using Expanse.WorldBridge;
using Core=Expanse.Clock.Core;
namespace Expanse.Clock.Tests;
public sealed class PowerAverageTelemetryTests
{
    static PowerAverageAccumulator New(){var a=new PowerAverageAccumulator();a.Reset("a");a.Tick("a",0,100);return a;}
    [Fact] public void ParallelCallbacksAndDuplicateCaptureDoNotMultiplyDurationOrAmount()
    {
        var a=New();a.Add("v",100,102,10,2,"supported-native-callbacks",true,1);a.Add("v",100,102,6,4,"supported-native-callbacks",true,2);a.Add("v",100,102,6,4,"supported-native-callbacks",true,2);
        Assert.Null(a.Observe("v",59));a.Tick("a",60,102);var p=a.Observe("v",60)!;Assert.Equal(2,p.CoveredGameSeconds);Assert.Equal(16,p.GenerationEc);Assert.Equal(8,p.GenerationEcPerSecond);Assert.Equal(3,p.ConsumptionEcPerSecond);Assert.Equal("partial",p.Status);
    }
    [Fact] public void PhysicalMeanWeightsOriginalIntervalsAndPreservesZero()
    {var a=New();a.Add("v",100,101,10,3,"fulfilled-part-requests",false,0);a.Add("v",101,104,0,0,"fulfilled-part-requests",false,0);a.Tick("a",60,104);var p=a.Observe("v",60)!;Assert.Equal(2.5,p.GenerationEcPerSecond);Assert.Equal(.75,p.ConsumptionEcPerSecond);Assert.Equal("observed",p.Status);}
    [Fact] public void GapsRemainPartialAndDifferentVesselsShareWindowDenominatorBounds()
    {var a=New();a.Add("v",100,101,10,2,"fulfilled-part-requests",false,0);a.Add("v",103,104,10,2,"fulfilled-part-requests",false,0);a.Add("w",100,104,12,8,"fulfilled-part-requests",false,0);a.Tick("a",60,104);var v=a.Observe("v",60)!;var w=a.Observe("w",60)!;Assert.Equal("partial",v.Status);Assert.Equal(2,v.CoveredGameSeconds);Assert.Equal(4,w.CoveredGameSeconds);Assert.Equal(v.WindowId,w.WindowId);Assert.Equal(v.WindowGameSeconds,w.WindowGameSeconds);Assert.Equal(v.WindowStartUt,w.WindowStartUt);}
    [Fact] public void ResetRollbackModeTransitionAndUnloadingInvalidateCompletedAndInFlightWindow()
    {
        foreach(int mode in new[]{0,1,2}){var a=New();a.Add("v",100,102,4,2,"fulfilled-part-requests",false,0);a.Tick("a",60,102);Assert.NotNull(a.Observe("v",60));if(mode==0)a.Reset("b");else if(mode==1)a.Tick("a",61,99);else a.Invalidate("v");Assert.Null(a.Observe("v",61));}
    }
    [Fact] public void AgeUsesRealTimeAndWindowRemainsStableUntilNextReport()
    {var a=New();a.Add("v",100,102,4,0,"fulfilled-part-requests",false,0);a.Tick("a",60,102);var first=a.Observe("v",60)!;a.Tick("a",70,10000);Assert.Equal(first.WindowId,a.Observe("v",70)!.WindowId);Assert.Equal(10,a.Observe("v",70)!.AgeRealSeconds);Assert.Null(a.Observe("v",181));}
    [Fact] public void PackedPartialWindowCannotBeMislabeledAsCompleteByWire()
    {
        var p=new Core.PowerAverageTelemetry(1,100,104,4,2,60,0,4,2,2,1,"partial","supported-native-callbacks","known owner subtotal");Core.ColonyLifeSupportTelemetryProtocol.Validate(null,p,"loaded",104,5);
        Assert.Throws<InvalidDataException>(()=>Core.ColonyLifeSupportTelemetryProtocol.Validate(null,p with {Status="observed"},"loaded",104,5));
        Assert.Throws<InvalidDataException>(()=>Core.ColonyLifeSupportTelemetryProtocol.Validate(null,p with {AgeRealSeconds=121},"loaded",104,5));
        Assert.Throws<InvalidDataException>(()=>Core.ColonyLifeSupportTelemetryProtocol.Validate(null,p with {GenerationEcPerSecond=3},"loaded",104,5));
    }
    [Fact] public void DifferentPowerBasisInOneWindowIsUnknown()
    {var a=New();a.Add("v",100,101,10,2,"fulfilled-part-requests",false,0);a.Add("v",101,102,10,2,"supported-native-callbacks",true,1);a.Tick("a",60,102);Assert.Null(a.Observe("v",60));}
    [Fact] public void InvalidCaptureAtWindowEdgeCannotClaimCompleteCoverage()
    {var a=New();a.Add("v",100,104,4,2,"fulfilled-part-requests",false,0);a.MarkPartial();a.Tick("a",60,104);Assert.Equal("partial",a.Observe("v",60)!.Status);}
}
