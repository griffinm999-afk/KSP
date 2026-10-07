using System.Text.Json;
using Expanse.Clock.Core;

var baseline=JsonSerializer.Deserialize<ClockView>(File.ReadAllText(args[0]),ClockProtocol.JsonOptions)!;
var original=baseline.Colony!;
int Bytes<T>(T value)=>JsonSerializer.SerializeToUtf8Bytes(value,ClockProtocol.JsonOptions).Length;
ColonyProductionRateTelemetry Rate(string resource,double rate)=>new(resource,rate,"ALL_VESSEL",false);
ColonyProductionModuleTelemetry Module(bool loaded,int index,double ut)=>new((uint)(index+1),null,index,"USITools.USI_Converter",
    "Synthetic payload fixture",index,index,"Fixture",new string('a',64),true,true,loaded?"loaded-broker":"background-model",
    new([Rate("Water",.0026)],[Rate("Supplies",.00026)],[]),
    loaded?new(ut,1.5,1,new([Rate("Water",.0039)],[Rate("Supplies",.00039)],[])):null,
    loaded?new(ut,1,1.5,[Rate("Water",.003)],[Rate("Supplies",.0003)],1):null,
    loaded?null:new(ut,"BOUNDARY",[Rate("Water",.001)],[Rate("Supplies",.0001)]),null,"Synthetic values; not live game production.",null);
var augmented=original with {Vessels=original.Vessels.Select(v=>v with {Production=new("partial","Synthetic injected observations; physical frame is read-only evidence.",
    original.ObservedUt!.Value,Enumerable.Range(0,8).Select(i=>Module(v.ObservationBasis=="loaded",i,original.ObservedUt.Value)).ToArray())}).ToArray()};
var source=baseline with {Colony=augmented};
ClockProtocol.Validate(source);
var covered=new HashSet<string>();int minRows=int.MaxValue,maxBytes=0,maxProductionBytes=0;
for(int turn=0;turn<original.Vessels.Length;turn++)
{
    var shifted=source with {Colony=augmented with {VesselCensus=augmented.VesselCensus! with {ObservationSequence=turn+1}}};
    byte[] frame=ClockProtocol.EncodeView(shifted);
    var decoded=JsonSerializer.Deserialize<ClockView>(frame.AsSpan(4),ClockProtocol.JsonOptions)!;
    ClockProtocol.Validate(decoded);
    if(decoded.Colony!.VesselCensus!.Status!="complete"||decoded.Colony.VesselCensus.VesselIds.Length!=original.VesselCensus!.VesselIds.Length)
        throw new Exception("Current census was lost");
    if(decoded.Colony.Vessels.Length!=original.Vessels.Length)throw new Exception("Current physical vessels were lost");
    int rows=0;
    foreach(var v in decoded.Colony.Vessels)
    {
        rows+=v.Production!.Modules.Length;if(v.Production.Modules.Length>0)covered.Add(v.VesselId);
        if(v.CrewRosterComplete!=original.Vessels.Single(s=>s.VesselId==v.VesselId).CrewRosterComplete)throw new Exception("Roster changed");
        foreach(var m in v.Production.Modules)
            if(m.Background is null&&m.Achieved is null)throw new Exception("Delivered/modeled provenance lost");
    }
    minRows=Math.Min(minRows,rows);maxBytes=Math.Max(maxBytes,frame.Length-4);
    maxProductionBytes=Math.Max(maxProductionBytes,decoded.Colony.Vessels.Sum(v=>Bytes(v.Production)));
}
if(covered.Count!=original.Vessels.Length)throw new Exception("Current-frame tail starvation");
if(augmented.Vessels.Any(v=>v.Production!.Modules.Length!=8))throw new Exception("Source mutation");
Console.WriteLine(JsonSerializer.Serialize(new {physicalBaselineViewBytes=Bytes(baseline),sampleBytes=Bytes(baseline.Sample),colonyBytes=Bytes(original),wolfBytes=Bytes(original.Wolf),
    censusBytes=Bytes(original.VesselCensus),injectedUnboundedBytes=Bytes(source),candidateMaximumBodyBytes=maxBytes,maximumProductionSectionBytes=maxProductionBytes,minimumRowsPerFrame=minRows,
    vesselsCovered=covered.Count,censusIds=original.VesselCensus!.VesselIds.Length,rateEvidence="All injected rates synthetic; no claim of live output."},new JsonSerializerOptions{WriteIndented=true}));
