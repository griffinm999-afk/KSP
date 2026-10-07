using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Expanse.Clock.Core;
using Expanse.WorldBridge;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Rejected(ColonyVessel vessel)
{
    try { ClockProtocol.Validate(Sample(vessel)); throw new Exception("Invalid roster was accepted"); }
    catch (InvalidDataException) { }
}
static ClockSample Sample(params ColonyVessel[] vessels) => new(
    1,"clockSample",1,Guid.NewGuid(),Guid.NewGuid(),@"C:\Kerbal Space Program","The Expanse","The Expanse",
    100,true,"FLIGHT",false,null,null,Colony:new ColonySnapshot("observed",null,100,vessels));
static ColonyVessel Vessel(int crew, ColonyCrewMember[]? roster, bool complete, int? seats) =>
    new("vessel-1","Hab","Minmus","Greater Flats",3,3,"snapshot",crew,
        Array.Empty<ColonyTank>(),Array.Empty<ColonyConverter>(),CrewRoster:roster,
        CrewRosterComplete:complete,PhysicalCrewCapacity:seats);

var oldJson = "{\"vesselId\":\"vessel-1\",\"name\":\"Hab\",\"body\":\"Minmus\",\"biome\":\"Greater Flats\",\"latitude\":3,\"longitude\":3,\"observationBasis\":\"snapshot\",\"crew\":1,\"tanks\":[],\"converters\":[]}";
var old = JsonSerializer.Deserialize<ColonyVessel>(oldJson,ClockProtocol.JsonOptions)!;
Check(old.CrewRoster is null && !old.CrewRosterComplete && old.PhysicalCrewCapacity is null,"Old payload defaults");
ClockProtocol.Validate(Sample(old));
var names = new[]{new ColonyCrewMember("Jebediah Kerman","Pilot"),new ColonyCrewMember("Bill Kerman","Engineer")};
var full = Vessel(2,names,true,4);
ClockProtocol.Validate(Sample(full));
var roundTrip = JsonSerializer.Deserialize<ColonyVessel>(JsonSerializer.Serialize(full,ClockProtocol.JsonOptions),ClockProtocol.JsonOptions)!;
Check(roundTrip.CrewRosterComplete && roundTrip.CrewRoster!.Length==2 && roundTrip.PhysicalCrewCapacity==4,"New payload round trip");
ClockProtocol.Validate(Sample(Vessel(2,Array.Empty<ColonyCrewMember>(),false,null)));
ClockProtocol.Validate(Sample(Vessel(0,Array.Empty<ColonyCrewMember>(),true,0)));
Rejected(Vessel(2,new[]{names[0],names[0]},true,4));
Rejected(Vessel(2,names,true,1));
Rejected(Vessel(2,names,false,null));
Rejected(Vessel(2,new[]{names[0]},true,4));

Check(ColonySeatCapacity.TryProto("Duna.PDU",2,null!,null!,out var ordinary)&&ordinary==2,"Ordinary proto capacity");
Check(ColonySeatCapacity.TryProto("KKAOSS.Habitat.MK2.g",4,"0.5","True",out var stowed)&&stowed==0,"Stowed habitat");
Check(ColonySeatCapacity.TryProto("KKAOSS.Habitat.MK2.g",4,"1","False",out var uninitialized)&&uninitialized==0,"Uninitialized habitat");
Check(ColonySeatCapacity.TryProto("KKAOSS.Habitat.MK2.g",4,"1","True",out var deployed)&&deployed==4,"Deployed habitat");
Check(!ColonySeatCapacity.TryProto("KKAOSS.Habitat.MK2.g",4,null!,"True",out _),"Missing deployment stays unknown");
Check(!ColonySeatCapacity.TryProto("KKAOSS.Habitat.MK2.g",4,"1.2","True",out _),"Invalid deployment stays unknown");

var censusTracker = new VesselCensusTracker();
var a = Guid.NewGuid(); var b = Guid.NewGuid(); var c = Guid.NewGuid();
var firstAttempt=censusTracker.Observe("world/epoch","flight",new[]{a,b},new[]{a,b},new[]{a.ToString("D")});
Check(firstAttempt.Status=="unavailable" && firstAttempt.ObservationSequence==1, "First census attempt starts at one");
var complete = censusTracker.Observe("world/epoch","flight",new[]{a,b},new[]{a,b},new[]{a.ToString("D")});
Check(complete.Status=="complete" && complete.VesselIds.Length==2 && complete.ObservationSequence==2, "Filtered colony scan does not narrow complete identity census");
var pausedSameUt=censusTracker.Observe("world/epoch","flight",new[]{a,b},new[]{a,b},Array.Empty<string>());
Check(pausedSameUt.Status=="complete" && pausedSameUt.ObservationSequence==3 && complete.ObservationSequence==2,
    "Paused UT or a cached complete snapshot must not rewrite an earlier sequence");
var newerIncomplete=censusTracker.Observe("world/epoch","flight",new[]{a},new[]{a,b},Array.Empty<string>());
Check(newerIncomplete.Status=="unavailable" && newerIncomplete.ObservationSequence==4 &&
    complete.ObservationSequence<newerIncomplete.ObservationSequence, "A newer incomplete attempt supersedes an older complete proof");
Check(censusTracker.Observe("world/epoch","flight",new[]{a},new[]{a},Array.Empty<string>()).Status=="unavailable", "One missing-ID sample cannot confirm deletion");
var afterRemoval=censusTracker.Observe("world/epoch","flight",new[]{a},new[]{a},Array.Empty<string>());
Check(afterRemoval.Status=="complete" && afterRemoval.ObservationSequence==6 && !afterRemoval.VesselIds.Contains(b.ToString("D")), "Second matching census confirms removal");
var afterRewind=censusTracker.Observe("world/new-epoch","flight",new[]{a,b},new[]{a,b},Array.Empty<string>());
Check(afterRewind.Status=="unavailable" && afterRewind.ObservationSequence==1, "Revert or rewind resets sequence and stability");
Check(censusTracker.Observe("world/new-epoch","flight",new[]{a,b},new[]{a,b},Array.Empty<string>()).Status=="complete", "Reloaded vessel remains active");
Check(censusTracker.Observe("world/new-epoch","flight",new[]{a,c},new[]{a,c},Array.Empty<string>()).Status=="unavailable", "Dock or same-name replacement is provisional");
var replacement=censusTracker.Observe("world/new-epoch","flight",new[]{a,c},new[]{a,c},Array.Empty<string>());
Check(replacement.Status=="complete" && replacement.VesselIds.Contains(c.ToString("D")) && !replacement.VesselIds.Contains(b.ToString("D")), "Replacement identity stays distinct");
var changedScene=censusTracker.Observe("world/new-epoch","space-center",new[]{a,c},new[]{a,c},Array.Empty<string>());
Check(changedScene.Status=="unavailable" && changedScene.ObservationSequence==5, "Scene change resets stability while preserving monotonic sequence");
var missingList=censusTracker.Observe("world/new-epoch","space-center",null!,new[]{a,c},Array.Empty<string>());
Check(missingList.Status=="unavailable" && missingList.ObservationSequence==6, "Missing world list advances sequence without asserting absence");
var tooLarge=censusTracker.Observe("world/new-epoch","space-center",Enumerable.Range(0,513).Select(_=>Guid.NewGuid()).ToArray(),Array.Empty<Guid>(),Array.Empty<string>());
Check(tooLarge.Status=="truncated" && tooLarge.ObservationSequence==7, "Bounded census advances sequence without asserting an oversized world");
var returnToFlight=censusTracker.Observe("world/new-epoch","flight",new[]{a,c},new[]{a,c},Array.Empty<string>());
Check(returnToFlight.Status=="unavailable" && returnToFlight.ObservationSequence==8,
    "Returning to an earlier scene raises its sequence and requires fresh proof");
var restabilizedFlight=censusTracker.Observe("world/new-epoch","flight",new[]{a,c},new[]{a,c},Array.Empty<string>());
Check(restabilizedFlight.Status=="complete" && restabilizedFlight.ObservationSequence==9 &&
    restabilizedFlight.ObservationSequence>replacement.ObservationSequence,
    "Revisited scene complete proof exceeds its old high-water sequence");
var goodId=Guid.NewGuid().ToString("D");
ClockProtocol.Validate(Sample(Vessel(0,Array.Empty<ColonyCrewMember>(),true,0) with {VesselId=goodId}) with {
    Colony=new ColonySnapshot("observed",null,100,new[]{Vessel(0,Array.Empty<ColonyCrewMember>(),true,0) with {VesselId=goodId}},
        VesselCensus:new Expanse.Clock.Core.ColonyVesselCensus("complete",new[]{goodId},1))});
try
{
    ClockProtocol.Validate(Sample(Vessel(0,Array.Empty<ColonyCrewMember>(),true,0) with {VesselId=goodId}) with {
        Colony=new ColonySnapshot("observed",null,100,new[]{Vessel(0,Array.Empty<ColonyCrewMember>(),true,0) with {VesselId=goodId}},
            VesselCensus:new Expanse.Clock.Core.ColonyVesselCensus("complete",new[]{Guid.NewGuid().ToString("D")},1))});
    throw new Exception("Census omitting observed vessel was accepted");
}
catch(InvalidDataException) { }
foreach(long? badSequence in new long?[]{null,-1,9007199254740992L})
{
    try
    {
        ClockProtocol.Validate(Sample(Vessel(0,Array.Empty<ColonyCrewMember>(),true,0) with {VesselId=goodId}) with {
            Colony=new ColonySnapshot("observed",null,100,new[]{Vessel(0,Array.Empty<ColonyCrewMember>(),true,0) with {VesselId=goodId}},
                VesselCensus:new Expanse.Clock.Core.ColonyVesselCensus("complete",new[]{goodId},badSequence))});
        throw new Exception("Missing or unsafe census sequence was accepted");
    }
    catch(InvalidDataException) { }
}
var missingSequence=JsonSerializer.Deserialize<Expanse.Clock.Core.ColonyVesselCensus>(
    "{\"status\":\"complete\",\"vesselIds\":[]}",ClockProtocol.JsonOptions)!;
Check(missingSequence.ObservationSequence is null,"Missing wire sequence remains invalid instead of defaulting to zero");

var censusIds=Enumerable.Range(0,481).Select(_=>Guid.NewGuid().ToString("D")).ToArray();
var many = Enumerable.Range(0,24).Select(i => Vessel(60,Enumerable.Range(0,60)
    .Select(j=>new ColonyCrewMember($"{i}-{j}-"+new string('N',140),"Engineer")).ToArray(),true,60)
    with {VesselId=censusIds[i]}).ToArray();
var view = new ClockView(1,"clockView","live",Sample(many) with { Colony = null },0,true,Colony:new ColonySnapshot("observed",null,100,many,
    VesselCensus:new Expanse.Clock.Core.ColonyVesselCensus("complete",censusIds,7)));
var frame = ClockProtocol.EncodeView(view);
var packed = JsonSerializer.Deserialize<ClockView>(frame.AsSpan(4),ClockProtocol.JsonOptions)!;
Check(frame.Length<=ClockProtocol.MaxFrameBytes+4 && packed.Colony!.Vessels.Length==24 &&
    packed.Colony.Vessels.All(v=>!v.CrewRosterComplete && v.CrewRoster is null) &&
    packed.Colony.VesselCensus?.Status=="complete" && packed.Colony.VesselCensus.VesselIds.Length==481 &&
    packed.Colony.VesselCensus.ObservationSequence==7,
    "Oversize roster fallback preserves vessels and complete census");
bool sawCensusFallback=false;
for(int converterCount=0;converterCount<=12 && !sawCensusFallback;converterCount++)
for(int tankCount=1;tankCount<=24 && !sawCensusFallback;tankCount++)
{
    var tanks=Enumerable.Range(0,tankCount).Select(j=>new ColonyTank("resource-"+j+new string('X',24),0,1,false,false,true)).ToArray();
    var converters=Enumerable.Range(0,converterCount).Select(j=>new ColonyConverter("Fixture "+j,"Recipe",true,
        Enumerable.Range(0,8).Select(k=>"Input-"+k+new string('I',60)).ToArray(),
        Enumerable.Range(0,8).Select(k=>"Output-"+k+new string('O',60)).ToArray())).ToArray();
    var enlarged=many.Select(v=>v with {Tanks=tanks,Converters=converters}).ToArray();
    var candidate=view with {Colony=view.Colony! with {Vessels=enlarged}};
    var bytes=ClockProtocol.EncodeView(candidate);
    var parsed=JsonSerializer.Deserialize<ClockView>(bytes.AsSpan(4),ClockProtocol.JsonOptions)!;
    sawCensusFallback=parsed.Colony?.VesselCensus?.Status=="truncated" &&
        parsed.Colony.VesselCensus.ObservationSequence==7 && parsed.Colony.VesselCensus.VesselIds.Length==0 &&
        parsed.Colony.Vessels.Length==24;
}
Check(sawCensusFallback,"Census size fallback preserves the attempt sequence and vessel rows");
Console.WriteLine("Roster wire, seats, census lifecycle, and 256 KiB fallback passed");
