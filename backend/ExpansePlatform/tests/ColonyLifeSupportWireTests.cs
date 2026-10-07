using System.Text.Json;
using Expanse.Clock.Core;
namespace Expanse.Clock.Tests;
public sealed class ColonyLifeSupportWireTests
{
    static ColonyVessel Vessel()=>new("e79c13cb-6a7f-4053-925c-afd903382380","Synthetic habitat","Minmus","Flats",1,2,"loaded",5,[],[],
        LifeSupport:new("partial","accepted quantities; independent streams",100,new(100,50,2,1,5,2,.5,.0025,.0025,.00125,.00125,.002,0),null),
        PowerAverage:new(1,30,90,60,60,60,10,600,300,10,5,"observed","fulfilled-part-requests","accepted fulfilled requests"));
    static ClockSample Sample(ColonyVessel vessel)=>new(1,"clockSample",1,Guid.NewGuid(),Guid.NewGuid(),@"C:\SyntheticKSP","Fixture","Fixture",100,true,"Flight",false,null,null,Colony:new("observed",null,100,[vessel]));
    [Fact] public void FullSampleAndViewRetainAmountsIntervalsUnknownEcAndLegitimateMulchZero()
    {
        var sample=Sample(Vessel());var decoded=ClockProtocol.DecodeClockSample(ClockProtocol.Encode(sample).AsSpan(4).ToArray());
        Assert.Equal("observed",decoded.Colony!.Status);Assert.Equal(0,decoded.Colony.Vessels[0].LifeSupport!.Supply!.MulchProduced);Assert.Null(decoded.Colony.Vessels[0].LifeSupport!.CrewElectricity);Assert.Equal(50,decoded.Colony.Vessels[0].LifeSupport!.Supply!.ProcessedEndUt);
        var view=new ClockView(1,"clockView","live",decoded,.2,true,Colony:decoded.Colony);var frame=ClockProtocol.EncodeView(view);var replay=JsonSerializer.Deserialize<ClockView>(frame.AsSpan(4),ClockProtocol.JsonOptions)!;ClockProtocol.Validate(replay);Assert.Equal(10,replay.Colony!.Vessels[0].PowerAverage!.AgeRealSeconds);Assert.Equal(10,replay.Colony.Vessels[0].PowerAverage!.GenerationEcPerSecond);
    }
    [Fact] public void InvalidNewFieldPreservesClockThroughExistingHostFallback()
    {var v=Vessel();var sample=Sample(v with {LifeSupport=v.LifeSupport! with {Supply=v.LifeSupport.Supply! with {SuppliesConsumed=99}}});var decoded=ClockProtocol.DecodeClockSample(ClockProtocol.Encode(sample).AsSpan(4).ToArray());Assert.Equal("unavailable",decoded.Colony!.Status);Assert.Equal(100,decoded.UtSeconds);Assert.True(decoded.ActiveWorld);}
    [Fact] public void LegacyWireWithoutAdditionsStillValid()
    {var decoded=ClockProtocol.DecodeClockSample(ClockProtocol.Encode(Sample(Vessel() with {LifeSupport=null,PowerAverage=null})).AsSpan(4).ToArray());Assert.Equal("observed",decoded.Colony!.Status);Assert.Null(decoded.Colony.Vessels[0].LifeSupport);Assert.Null(decoded.Colony.Vessels[0].PowerAverage);}
    [Fact] public void CrowdedNewFieldsPreserve256KiBLimitAndCensusFallbackIdentity()
    {
        var vessels=Enumerable.Range(0,128).Select(i=>Vessel() with {VesselId=Guid.NewGuid().ToString(),Name=new string('x',100),Tanks=Enumerable.Range(0,24).Select(j=>new ColonyTank(new string((char)('a'+j),50),1,2,null,null,null)).ToArray()}).ToArray();
        var colony=new ColonySnapshot("observed",null,100,vessels,VesselCensus:new("complete",vessels.Select(v=>v.VesselId).ToArray(),8,"fixture census"));var view=new ClockView(1,"clockView","live",Sample(vessels[0]),0,true,Colony:colony);var frame=ClockProtocol.EncodeView(view);
        Assert.True(frame.Length<=256*1024+4);var decoded=JsonSerializer.Deserialize<ClockView>(frame.AsSpan(4),ClockProtocol.JsonOptions)!;ClockProtocol.Validate(decoded);Assert.Equal(100,decoded.Sample!.UtSeconds);Assert.Equal(8,decoded.Colony!.VesselCensus!.ObservationSequence);
    }
}
