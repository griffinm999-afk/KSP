using System.Text.Json;
using System.Text.Json.Nodes;
using Expanse.Clock.Core;

namespace Expanse.Clock.Tests;

public sealed class ColonyValidationDiagnosticTests
{
    static ClockSample Sample() => new(1,"clockSample",1,Guid.NewGuid(),Guid.NewGuid(),@"C:\KSP","private-save","private-title",100,true,"FLIGHT",false,null,null,
        Colony:new ColonySnapshot("observed",null,100,[new ColonyVessel("private-id","private-vessel","private-body","private-biome",0,0,"loaded",0,[],[])]));

    [Fact] public void ValidPayloadProducesNoDiagnostic()
    {
        var diagnostics=new List<string>();
        var result=ClockProtocol.DecodeClockSample(JsonSerializer.SerializeToUtf8Bytes(Sample(),ClockProtocol.JsonOptions),diagnostics.Add);
        Assert.Equal("observed",result.Colony!.Status);Assert.Empty(diagnostics);
    }
    [Fact] public void InvalidTankReportsCheckAndIndexWithoutPrivateValues()
    {
        var s=Sample();s=s with {Colony=s.Colony! with {Vessels=[s.Colony!.Vessels[0] with {Tanks=[new ColonyTank("private-resource",2,1,null,null,null)]}]}};
        var diagnostics=new List<string>();
        var result=ClockProtocol.DecodeClockSample(JsonSerializer.SerializeToUtf8Bytes(s,ClockProtocol.JsonOptions),diagnostics.Add);
        Assert.Equal("unavailable",result.Colony!.Status);Assert.Equal(100,result.UtSeconds);
        var d=Assert.Single(diagnostics);Assert.Contains("$.colony.vessels[0]",d);Assert.Contains("Invalid colony tank.",d);Assert.DoesNotContain("private",d);
    }
    [Fact] public void JsonConversionReportsSchemaPathWithoutMessageOrValue()
    {
        var json=JsonNode.Parse(JsonSerializer.Serialize(Sample(),ClockProtocol.JsonOptions))!;
        json["colony"]!["vessels"]![0]!["crew"]="private-invalid-number";
        var diagnostics=new List<string>();
        var result=ClockProtocol.DecodeClockSample(System.Text.Encoding.UTF8.GetBytes(json.ToJsonString()),diagnostics.Add);
        Assert.Equal("unavailable",result.Colony!.Status);
        var d=Assert.Single(diagnostics);Assert.Contains("JsonException",d);Assert.Contains("$.vessels[0].crew",d);Assert.DoesNotContain("private",d);
    }
    [Fact] public void ProductionFailureIdentifiesSourceCheckWithoutResourceValues()
    {
        var s=Sample();s=s with {Colony=s.Colony! with {Vessels=[s.Colony!.Vessels[0] with {Production=new ColonyProductionTelemetry("invalid-private-status","private-reason",100,[])}]}};
        var diagnostics=new List<string>();
        var result=ClockProtocol.DecodeClockSample(JsonSerializer.SerializeToUtf8Bytes(s,ClockProtocol.JsonOptions),diagnostics.Add);
        Assert.Equal("unavailable",result.Colony!.Status);
        var d=Assert.Single(diagnostics);Assert.Contains("Invalid production telemetry (check ",d);Assert.Contains("$.colony.vessels[0]",d);Assert.DoesNotContain("private",d);
    }
    [Fact] public void FailingDiagnosticSinkCannotBreakClockFallback()
    {
        var s=Sample();s=s with {Colony=s.Colony! with {Status="invalid"}};
        var result=ClockProtocol.DecodeClockSample(JsonSerializer.SerializeToUtf8Bytes(s,ClockProtocol.JsonOptions),_=>throw new IOException("private sink error"));
        Assert.Equal("unavailable",result.Colony!.Status);Assert.Equal(100,result.UtSeconds);
    }
    [Fact] public void IdentityDiagnosticEvaluatesEveryPredicateAndReportsOnlyShape()
    {
        var row=new ColonyProductionModuleTelemetry(0,null,160,new string('x',129),new string('x',101),64,64,
            new string('x',101),new string('x',63),true,true,"private-invalid-basis",new([],[],[]),null,null,null,new string('x',257),new string('x',361),null);
        var s=Sample();s=s with {Colony=s.Colony! with {Vessels=[s.Colony!.Vessels[0] with {Production=new ColonyProductionTelemetry("partial","",100,[row])}]}};
        var diagnostics=new List<string>();
        var result=ClockProtocol.DecodeClockSample(JsonSerializer.SerializeToUtf8Bytes(s,ClockProtocol.JsonOptions),diagnostics.Add);
        Assert.Equal("unavailable",result.Colony!.Status);var d=Assert.Single(diagnostics);
        foreach(var field in new[]{"partId","moduleIndex","bayIndex","selectedLoadout","moduleType","partName","recipe","recipeHash","basis","nativeStatus","reason"})Assert.Contains(field+" constraint=",d);
        Assert.Contains("actualLength=101",d);Assert.Contains("$.colony.vessels[0].production.modules[0]",d);
        Assert.DoesNotContain("private",d);Assert.DoesNotContain(new string('x',20),d);
    }
    [Fact] public void EmptyNativeRecipeCaptionDoesNotDiscardValidTelemetry()
    {
        ColonyProductionRateTelemetry[] rates=[new("Supplies",0.01,"NULL",false),new("Recyclables",0,"NULL",true)];
        var row=new ColonyProductionModuleTelemetry(1,null,4,"ModuleResourceConverter","Test",null,null,
            "",new string('a',64),true,true,"loaded-broker",new([],rates,[]),new(100,1,1,new([],rates,[])),new(100,0.02,0.02,[],rates,1),null,null,"Latest callback.",null);
        var s=Sample();s=s with {Colony=s.Colony! with {Vessels=[s.Colony!.Vessels[0] with {Production=new ColonyProductionTelemetry("partial","",100,[row])}]}};
        var diagnostics=new List<string>();var decoded=ClockProtocol.DecodeClockSample(JsonSerializer.SerializeToUtf8Bytes(s,ClockProtocol.JsonOptions),diagnostics.Add);
        Assert.Equal("observed",decoded.Colony!.Status);Assert.Empty(diagnostics);
        var actual=Assert.Single(decoded.Colony.Vessels[0].Production!.Modules);
        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(row with {Recipe="Unnamed native recipe"},ClockProtocol.JsonOptions),
            JsonSerializer.SerializeToNode(actual,ClockProtocol.JsonOptions)));
        Assert.Equal(100,actual.Achieved!.SampleUt);Assert.Equal(1,actual.Achieved.CaptureSequence);
        Assert.Equal(0,actual.Achieved.Outputs[1].UnitsPerSecond);
    }
    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public void NullOrWhitespaceRecipeStillFailsValidation(string? recipe)
    {
        var row=new ColonyProductionModuleTelemetry(1,null,4,"ModuleResourceConverter","Test",null,null,
            recipe!,new string('a',64),true,true,"loaded-broker",new([],[],[]),null,null,null,null,"",null);
        var s=Sample();s=s with {Colony=s.Colony! with {Vessels=[s.Colony!.Vessels[0] with {Production=new ColonyProductionTelemetry("partial","",100,[row])}]}};
        var result=ClockProtocol.DecodeClockSample(JsonSerializer.SerializeToUtf8Bytes(s,ClockProtocol.JsonOptions));
        Assert.Equal("unavailable",result.Colony!.Status);
    }
}
