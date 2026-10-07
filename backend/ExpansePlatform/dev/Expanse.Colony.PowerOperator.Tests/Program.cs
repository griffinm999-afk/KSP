// The actual paid-operator predicate is source-linked. These are detached KSP
// provider-contract stubs, not native activation, thermal or game acceptance.
using Expanse.Domain.Colonies;
using Expanse.WorldBridge;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;

int passed=0;
void Check(string name,Action action){action();passed++;Console.WriteLine("PASS "+name);}
void Assert(bool value,string message){if(!value)throw new Exception(message);}
void Reject(string name,Action<Fixture> change,bool unloaded=false)=>Check(name,()=>{var f=new Fixture();if(unloaded)f.Unload();change(f);Assert(!f.Allowed(out var evidence),"Unexpected operator authority: "+evidence);Assert(evidence=="","Rejected predicate exposed success evidence.");});

Check("Paid commissioning cabin qualifies before staffing/utility certification",()=>{var f=new Fixture();Assert(!f.Facility.Qualification.StaffingQualified&&!f.Facility.Qualification.PowerReliable,"Fixture prequalified.");Assert(f.Allowed(out var e),"Initial real paid workplace unavailable.");Assert(e.Contains("Modeled paid Engineer")&&e.Contains("No native specialist bonus or generation"),"Modeled employment hidden.");});
Check("Anchored packed loaded cabin preserves the same workplace",()=>{var f=new Fixture();f.Vessel.packed=true;Assert(f.Allowed(out _),"Packed loaded exact provider rejected.");});
Check("Case normalized craft SHA remains exact",()=>{var f=new Fixture();f.Marker.templateSha256=f.Marker.templateSha256.ToUpperInvariant();Assert(f.Allowed(out _),"Paid SHA case mismatch.");});
Check("Observation grants no power, homes, bonus, activation or state mutation",()=>{var f=new Fixture();var before=JsonSerializer.Serialize(f.State);Assert(f.Allowed(out _),"Fixture rejected.");Assert(before==JsonSerializer.Serialize(f.State),"Accepted graph mutated.");Assert(!f.Reactor.Enabled&&f.Reactor.NativeEventCalls==0,"Native reactor invoked.");Assert(f.Facility.CertifiedHomes==0&&!f.Facility.Qualification.PowerReliable&&!f.Facility.Qualification.HeatSafe,"Utility or housing fabricated.");});
Reject("Adopted compatible PDU gains no paid employment",f=>f.Facility.State="adopted");
Reject("Unpaid construction gains no employment",f=>f.Order.FundsPaid=false);
Reject("Unconsumed manufacturing materials gain no employment",f=>f.Order.MaterialsConsumed=false);
Reject("Order awaiting placement cannot qualify",f=>f.Order.State="placing");
Reject("Held placement cannot qualify",f=>f.Order.Placement.Phase="RecoveryHold");
Reject("A different construction order cannot qualify",f=>f.Facility.ConstructionOrderId="other-order");
Reject("A different paid facility cannot qualify",f=>f.Order.FacilityId="other-facility");
Reject("A different paid plot cannot qualify",f=>f.Order.PlotId="other-plot");
Reject("Changed paid template hash cannot qualify",f=>f.Order.TemplateHash=new string('d',64));
Reject("A compatible arbitrary template is excluded",f=>f.Template.Id=f.Facility.TemplateId=f.Order.TemplateId="arbitrary-power");
Reject("Multiple operator seats cannot be inferred",f=>f.Template.Workers=f.Facility.RequiredWorkers=2);
Reject("Scientist employment cannot be inferred",f=>f.Template.WorkerTrait=f.Facility.RequiredTrait="Scientist");
Reject("Changed reviewed craft bytes cannot qualify",f=>f.Facility.CraftSha256=new string('c',64));
Reject("Stale placement witness hash cannot qualify",f=>f.Order.Placement.AfterWitness="different-anchored-receipt");
Reject("Changed placement operation cannot qualify",f=>f.Marker.operationId="different-operation");
Reject("Wrong world marker cannot qualify",f=>f.Marker.worldId="other-world");
Reject("Wrong colony marker cannot qualify",f=>f.Marker.colonyId="other-colony");
Reject("Wrong plot marker cannot qualify",f=>f.Marker.plotId="other-plot");
Reject("Changed request cannot qualify",f=>f.Marker.requestFingerprint="other-fingerprint");
Reject("Wrong craft part cannot qualify",f=>f.Marker.craftPartId=103);
Reject("Missing marker cannot qualify",f=>f.Part.Modules.Remove(f.Marker));
Reject("Duplicate marker cannot qualify",f=>f.Part.Modules.Add(f.Marker));
Reject("A command cabin is not a power operator cabin",f=>f.Part.partInfo.name="Ranger.CommPak");
Reject("Changed physical crew capacity cannot qualify",f=>f.Part.CrewCapacity=4);
Reject("Membership loss cannot qualify",f=>f.Facility.PartIds.Add(99));
Reject("Unexpected actual part cannot qualify",f=>f.Vessel.parts.Add(new Part{persistentId=99,vessel=f.Vessel}));
Reject("Changed expected part cardinality cannot qualify",f=>f.Template.ExpectedPartCount=2);
Reject("Different vessel object on part cannot qualify",f=>f.Part.vessel=new Vessel());
Reject("Different body cannot qualify",f=>f.Vessel.mainBody.bodyName="Mun");
Reject("Airborne package cannot qualify",f=>f.Vessel.LandedOrSplashed=false);
Reject("Selected game replacement cannot qualify",f=>HighLogic.CurrentGame=new object());
Reject("No selected ready runtime cannot qualify",f=>f.Runtime.Ready=false);
Reject("Wrong actual SystemHeat version cannot qualify",f=>f.Reactor.ReviewedVersion=false);
Reject("Missing actual heat module cannot qualify",f=>f.Part.Modules.Remove(f.Heat));
Reject("Duplicate actual reactor cannot qualify",f=>f.Part.Modules.Add(f.Reactor));
Reject("Same native module identity cannot qualify",f=>f.Reactor.PersistentId=f.Heat.PersistentId);
Reject("Absent native module identity cannot qualify",f=>f.Reactor.PersistentId=0);
Reject("Different bound heat module cannot qualify",f=>f.Reactor.heatModule=new SystemHeat.ModuleSystemHeat());
Reject("Different active loop cannot qualify",f=>f.Heat.Loop.LoopModules.Clear());
Reject("Duplicate loop membership cannot qualify",f=>f.Heat.Loop.LoopModules.Add(f.Heat));
Reject("Unheld Foundation cannot qualify",f=>f.Vessel.FoundationHeld=false);
Reject("Different Foundation cannot qualify",f=>f.Vessel.Foundation="different-foundation");
Reject("Unstable actual Foundation cannot qualify",f=>f.Vessel.PositionError=.011);
Reject("Nonfinite actual Foundation cannot qualify",f=>f.Vessel.AngleError=double.NaN);

Check("Exact saved operational evidence can retain modeled unloaded staffing",()=>{var f=new Fixture();f.Unload();Assert(f.Allowed(out var e),"Saved exact staffing rejected.");Assert(e.Contains("Retained modeled")&&e.Contains("separately qualified")&&e.Contains("no prefab-only staffing"),"Retained proof mislabeled.");});
Reject("Proto commissioning never creates first staffing authority",f=>f.Facility.State="commissioning",true);
Reject("Proto staffing requires prior actual staffing proof",f=>f.Facility.Qualification.StaffingQualified=false,true);
Reject("Proto staffing requires prior anchored proof",f=>f.Facility.Qualification.PlacementStable=false,true);
Reject("Proto staffing requires sealed real reactor continuation",f=>f.Facility.Qualification.ReactorContinuation=null,true);
Reject("Proto proof must belong to exact world",f=>f.Facility.Qualification.ReactorContinuation.WorldId="other-world",true);
Reject("Proto proof must belong to exact vessel",f=>f.Facility.Qualification.ReactorContinuation.VesselId=Guid.NewGuid().ToString("D"),true);
Reject("Changed saved native identity revokes retained workplace",f=>f.Saved("ModuleSystemHeatFissionReactor").moduleValues.SetValue("persistentId","999"),true);
Reject("Same saved native identity cannot qualify even resealed",f=>{f.Saved("ModuleSystemHeatFissionReactor").moduleValues.SetValue("persistentId","21");f.Seal();},true);
Reject("Missing saved native identity cannot qualify even resealed",f=>{f.Saved("ModuleSystemHeatFissionReactor").moduleValues.SetValue("persistentId","0");f.Seal();},true);
Reject("Prefab alone cannot certify unloaded cabin",f=>{f.Vessel.protoVessel.protoPartSnapshots[0].modules.Remove(f.Saved("ModuleSystemHeatFissionReactor"));f.Seal();},true);
Reject("Duplicate saved module cannot qualify even resealed",f=>{f.Vessel.protoVessel.protoPartSnapshots[0].modules.Add(f.Saved("ModuleSystemHeat"));f.Seal();},true);
Reject("Changed saved paid marker cannot qualify",f=>f.Saved(nameof(ColonyPlacementMarker)).moduleValues.SetValue("operationId","other-operation"),true);
Reject("Saved hardware settings mismatch cannot retain proof",f=>f.Vessel.HardwareRevision++,true);
Reject("Changed installed provider cannot retain staffing",f=>f.Heat.ReviewedVersion=false,true);
Reject("Lost proto membership cannot retain staffing",f=>f.Vessel.protoVessel.protoPartSnapshots.Clear(),true);
Check("Unloaded staffing never measures temperatures or grants power",()=>{var f=new Fixture();f.Unload();f.Facility.Qualification.PowerReliable=false;f.Facility.Qualification.HeatSafe=false;f.Facility.Qualification.ReactorContinuation.ExpiresUt=0;var before=JsonSerializer.Serialize(f.State);Assert(f.Allowed(out _),"Employment incorrectly coupled to thermal guarantee.");Assert(before==JsonSerializer.Serialize(f.State)&&!f.Facility.Qualification.PowerReliable&&!f.Facility.Qualification.HeatSafe,"Retained staffing requalified utilities.");});
Check("No kOS processor means no global setting read",()=>{int reads=0;var observation=new ColonyUtilityInstructionObservation(()=>{reads++;return 200;});Assert(reads==0,"Eager assembly/global setting lookup.");});
Check("All facility and distribution peers share exactly one lookup",()=>{int reads=0;var observation=new ColonyUtilityInstructionObservation(()=>{reads++;return 200;});for(int i=0;i<25;i++)Assert(observation.TryDemand(.001,.00001,100000,.02,out _),"Valid peer demand rejected.");Assert(reads==1,"Repeated assembly scan across peers.");});
Check("Failed global lookup is not retried in the same observation",()=>{int reads=0;var observation=new ColonyUtilityInstructionObservation(()=>{reads++;return null;});Assert(!observation.TryDemand(.001,0,0,.02,out _)&&!observation.TryDemand(.001,0,0,.02,out _),"Failure fabricated bound.");Assert(reads==1,"Failure retried per processor.");});
Check("Throwing global lookup fails closed once",()=>{int reads=0;var observation=new ColonyUtilityInstructionObservation(()=>{reads++;throw new Exception("lookup unavailable");});Assert(!observation.TryDemand(.001,0,0,.02,out _)&&!observation.TryDemand(.001,0,0,.02,out _),"Exception fabricated bound.");Assert(reads==1,"Throwing lookup retried.");});
Check("Next observation reads changed global setting",()=>{double setting=200;var first=new ColonyUtilityInstructionObservation(()=>setting);Assert(first.TryDemand(.001,0,0,.02,out var before)&&before==10,"Initial bound incorrect.");setting=1000;Assert(first.TryDemand(.001,0,0,.02,out var retained)&&retained==10,"Observation mutated its selected setting.");var next=new ColonyUtilityInstructionObservation(()=>setting);Assert(next.TryDemand(.001,0,0,.02,out var after)&&after==50,"Persistent global cache survived observation.");});
Check("Each module rate disk and current fixed timestep stay fresh",()=>{var observation=new ColonyUtilityInstructionObservation(()=>200);Assert(observation.TryDemand(.001,.00001,100000,.02,out var first)&&first==11,"First processor demand incorrect.");Assert(observation.TryDemand(.002,.00002,200000,.04,out var next)&&next==14,"Peer used cached module rate/disk/timestep.");Assert(!observation.TryDemand(double.NaN,.00001,100000,.02,out _)&&!observation.TryDemand(.001,.00001,100000,0,out _)&&!observation.TryDemand(.001,.00001,100000,double.NaN,out _),"Invalid current module data certified.");});
Check("Unbounded or nonfinite global setting is unqualified",()=>{foreach(double invalid in new[]{0d,100001d,double.NaN,double.PositiveInfinity})Assert(!new ColonyUtilityInstructionObservation(()=>invalid).TryDemand(.001,0,0,.02,out _),"Unbounded global setting accepted.");});
Check("Negative rates and disk cannot be masked by another positive term",()=>{var observation=new ColonyUtilityInstructionObservation(()=>200);Assert(!observation.TryDemand(-.001,.001,100000,.02,out _),"Negative instruction rate masked by positive disk demand.");Assert(!observation.TryDemand(.001,-.00001,50000,.02,out _),"Negative byte rate masked by positive processor demand.");Assert(!observation.TryDemand(.001,.00001,-1,.02,out _),"Negative disk capacity masked by minimum capacity clamp.");});
Check("Zero native rates and zero disk remain valid",()=>{var observation=new ColonyUtilityInstructionObservation(()=>200);Assert(observation.TryDemand(0,0,0,.02,out var zero)&&zero==0,"Actual zero demand rejected.");Assert(observation.TryDemand(0,.00001,0,.02,out var bytes)&&bytes==.5,"Zero disk minimum bound changed.");Assert(observation.TryDemand(.001,0,0,.02,out var cpu)&&cpu==10,"Zero byte rate rejected.");});
void RejectBank(string name,Action<BankFixture> change)=>Check(name,()=>{var f=new BankFixture();change(f);Assert(!f.Allowed(out var e)&&e=="","Bank unexpectedly qualified: "+e);});
Check("Exact paid19-member Ranger cabin is reachable independently of power qualification",()=>{var f=new BankFixture();Assert(f.Allowed(out var e),"Paid bank workplace rejected.");Assert(e.Contains("craft105")&&e.Contains("no native bonus, refill, replacement, home or unloaded remote grid"),"Scope not explicit.");Assert(!f.Inner.Facility.Qualification.PowerReliable&&!f.Inner.Facility.Qualification.StaffingQualified,"Authority leaked into qualification.");});
Check("Repeated bank observation does not mutate receipts, staffing or native hardware",()=>{var f=new BankFixture();var before=JsonSerializer.Serialize(f.Inner.State);for(int i=0;i<3;i++)Assert(f.Allowed(out _),"Repeated observation rejected.");Assert(before==JsonSerializer.Serialize(f.Inner.State)&&f.Converters.All(c=>c.NativeCalls==0),"Observation performed effects.");});
RejectBank("Bank requires current selected game",f=>HighLogic.CurrentGame=new object());
RejectBank("Bank requires ready source context",f=>f.Inner.Runtime.Ready=false);
RejectBank("Bank cannot use unloaded prefab workplace",f=>f.Inner.Vessel.loaded=false);
RejectBank("Bank does not infer packed workplace",f=>f.Inner.Vessel.packed=true);
RejectBank("Bank requires landed body",f=>f.Inner.Vessel.LandedOrSplashed=false);
RejectBank("Bank rejects altered template hash",f=>f.Inner.Facility.TemplateHash=new string('c',64));
RejectBank("Bank rejects altered craft bytes",f=>f.Inner.Facility.CraftSha256=new string('c',64));
RejectBank("Bank cannot infer homes from spare seats",f=>f.Inner.Template.Homes=1);
RejectBank("Bank rejects changed worker trait",f=>f.Inner.Template.WorkerTrait="Scientist");
RejectBank("Bank requires funds receipt",f=>f.Inner.Order.FundsPaid=false);
RejectBank("Bank requires materials receipt",f=>f.Inner.Order.MaterialsConsumed=false);
RejectBank("Bank rejects development-only facility authority",f=>f.Inner.Facility.DevelopmentOnly=true);
RejectBank("Bank requires exact paid build funds",f=>f.Inner.Order.Funds--);
RejectBank("Bank requires fully consumed funds",f=>f.Inner.Order.FundsConsumed--);
RejectBank("Bank requires exact paid labor funds",f=>f.Inner.Order.LaborFunds--);
RejectBank("Bank requires exact work requirement",f=>f.Inner.Order.WorkRequired--);
RejectBank("Bank requires completed work",f=>f.Inner.Order.WorkCompleted--);
RejectBank("Bank rejects invented excess completed work",f=>f.Inner.Order.WorkCompleted++);
RejectBank("Bank requires exact current escrow witness",f=>f.Inner.Order.Placement.EscrowWitness=new string('e',64));
void RejectResealedBank(string name,Action<BankFixture> change)=>RejectBank(name,f=>{change(f);f.Inner.Order.Placement.EscrowWitness=ColonyEngine.ConstructionEscrowWitness(f.Inner.State,f.Inner.Order.Id);});
RejectResealedBank("Resealing cannot grant partially completed work",f=>f.Inner.Order.WorkCompleted--);
RejectResealedBank("Resealing cannot grant incomplete cash consumption",f=>f.Inner.Order.FundsConsumed--);
RejectResealedBank("Resealing cannot turn4Pu payment into80Pu",f=>f.Inner.Order.Materials.Single(x=>x.Resource=="Plutonium-238").Amount=4000000);
Check("Actual pure fuel forecast covers seven-day declared envelope conservatively",()=>{double total=0;for(int i=0;i<4;i++){Assert(ColonyFixedGeneratorBounds.TryFuelBound(20,20,20,1e-6,50,1,1,151200,out var g,out _,out _),"Actual bound held.");total+=g;}Assert(total>=10+1.1*150&&total<200,"Declared budget lacks conservative headroom.");});
Check("Original2Pu packs cannot substitute for fully paid bank envelope",()=>{double total=0;for(int i=0;i<4;i++){Assert(ColonyFixedGeneratorBounds.TryFuelBound(2,20,20,1e-6,50,1,1,151200,out var g,out _,out _),"Actual low-stock bound held.");total+=g;}Assert(total<10+1.1*150,"Legacy low stock supplied fictional bank budget.");});
Check("Finite bank cannot promise the same envelope indefinitely",()=>{Assert(ColonyFixedGeneratorBounds.TryFuelBound(20,20,20,1e-6,50,1,1,2500000,out var g,out _,out _),"Finite source absent.");Assert(4*g<10+1.1*150,"Finite aging fuel retained excessive authority.");});
RejectBank("Bank order must pay full80Pu",f=>f.Inner.Order.Materials.Single(x=>x.Resource=="Plutonium-238").Amount-=1000000);
RejectBank("Bank order rejects duplicate paid-resource rows",f=>f.Inner.Order.Materials.Add(new MaterialRequirement{Resource="Plutonium-238",Amount=1}));
RejectBank("Bank rejects inherited2Pu startup terms",f=>f.Inner.Template.StartupContents.Single(x=>x.CraftPartId==101&&x.ResourceName=="Plutonium-238").Amount=2000000);
RejectBank("Bank rejects mismatched embedded fuel charge",f=>f.Inner.Template.EmbeddedContents.Single(x=>x.Resource=="Plutonium-238").Amount=4000000);
RejectBank("Bank rejects changed battery startup",f=>f.Inner.Template.StartupContents.Single(x=>x.CraftPartId==106).Amount=3999000000);
RejectBank("Bank rejects unanchored order",f=>f.Inner.Order.Placement.Phase="Created");
RejectBank("Bank rejects changed paid operation",f=>f.Inner.Order.Placement.OperationId="other");
RejectBank("Bank rejects changed witness",f=>f.Inner.Facility.PlacementWitnessHash=new string('d',64));
RejectBank("Bank rejects missing actual member",f=>f.Inner.Vessel.parts.RemoveAt(0));
RejectBank("Bank rejects foreign membership",f=>f.Inner.Facility.PartIds[0]=9999);
RejectBank("Bank rejects duplicate native IDs",f=>f.Inner.Vessel.parts[0].persistentId=f.Inner.Vessel.parts[1].persistentId);
RejectBank("Bank requires cabin105 actual identity",f=>f.Inner.Part.partInfo.name="Duna.PDU");
RejectBank("Bank requires actual4-seat cabin",f=>f.Inner.Part.CrewCapacity=2);
RejectBank("Bank rejects changed pack part",f=>f.Parts[101].partInfo.name="Duna.PDU");
RejectBank("Bank rejects changed spacer part",f=>f.Parts[118].partInfo.name="strutCube");
RejectBank("Bank rejects duplicate paid craft IDs",f=>f.Parts[101].Modules.OfType<ColonyPlacementMarker>().Single().craftPartId=102);
RejectBank("Bank rejects changed marker context",f=>f.Parts[117].Modules.OfType<ColonyPlacementMarker>().Single().worldId="other");
RejectBank("Bank rejects changed marker owner",f=>f.Parts[117].Modules.OfType<ColonyPlacementMarker>().Single().part=f.Parts[116]);
RejectBank("Bank rejects additional marker",f=>f.Parts[101].Modules.Add(f.Parts[102].Modules.OfType<ColonyPlacementMarker>().Single()));
RejectBank("Bank requires actual converter on every pack",f=>f.Parts[101].Modules.RemoveAll(x=>x is USITools.USI_Converter));
RejectBank("Bank rejects duplicate converter",f=>f.Parts[101].Modules.Add(new USITools.USI_Converter{part=f.Parts[101]}));
RejectBank("Bank repeats exact fixed hardware guard",f=>f.Converters[0].ReviewedVersion=false);
RejectBank("Bank rejects changed recipe guard",f=>f.Converters[0].RecipeChanged=true);
RejectBank("Bank rejects foreign converter owner",f=>f.Converters[0].part=f.Parts[102]);
RejectBank("Bank requires held real foundation",f=>f.Inner.Vessel.FoundationHeld=false);
RejectBank("Bank rejects shifted foundation",f=>f.Inner.Vessel.PositionError=.011);
RejectBank("Bank rejects stale foundation identity",f=>f.Inner.Vessel.Foundation="other");
Console.WriteLine($"{passed}/{passed} source-linked paid operator and observation-local kOS boundary checks passed. Bank provider stubs verify paid identity/guard boundaries; actual fixed hardware, native staffing/crew transfer and loaded→packed→BRP acceptance remain unproved; no measured speedup claim.");

sealed class Fixture
{
    public ColonyRuntime Runtime;
    public ColonyState State=new ColonyState{WorldId="world"};
    public ColonyRecord Colony=new ColonyRecord{Id="colony",Site=new ColonySite{Body="Duna"}};
    public ColonyTemplate Template=new ColonyTemplate{Id="power-duna-v1",Hash=new string('a',64),CraftSha256=new string('b',64),Workers=1,WorkerTrait="Engineer",ExpectedPartCount=1};
    public Vessel Vessel=new Vessel{id=Guid.NewGuid(),loaded=true,LandedOrSplashed=true,mainBody=new CelestialBody{bodyName="Duna"},FoundationHeld=true,Foundation="foundation"};
    public Part Part=new Part{persistentId=10,CrewCapacity=2,partInfo=new AvailablePart{name="Duna.PDU"}};
    public ColonyFacility Facility;
    public ConstructionOrder Order;
    public ColonyPlacementMarker Marker;
    public SystemHeat.ModuleSystemHeatFissionReactor Reactor=new SystemHeat.ModuleSystemHeatFissionReactor{PersistentId=20};
    public SystemHeat.ModuleSystemHeat Heat=new SystemHeat.ModuleSystemHeat{PersistentId=21,Loop=new NativeLoop()};
    public Fixture()
    {
        HighLogic.CurrentGame=new object();Runtime=new ColonyRuntime(State,HighLogic.CurrentGame,Template);ColonyRuntime.Current=Runtime;
        Part.vessel=Vessel;Vessel.parts.Add(Part);Part.partInfo.partPrefab=Part;
        Facility=new ColonyFacility{Id=Vessel.id.ToString("D"),VesselId=Vessel.id.ToString("D"),TemplateId=Template.Id,TemplateHash=Template.Hash,CraftSha256=Template.CraftSha256,
            State="commissioning",RequiredWorkers=1,RequiredTrait="Engineer",ConstructionOrderId="order",PlotId="plot",PlacementOperationId="placement",PlacementRequestFingerprint="fingerprint",
            PartIds=new List<uint>{10},FoundationId="foundation",PlacementWitnessHash=ColonyStateCodec.Hash(Encoding.UTF8.GetBytes("anchored-receipt"))};
        Order=new ConstructionOrder{Id="order",ColonyId=Colony.Id,FacilityId=Facility.Id,FundsPaid=true,MaterialsConsumed=true,State="commissioning",TemplateId=Template.Id,TemplateHash=Template.Hash,PlotId="plot",
            Placement=new ColonyConstructionPlacementIntent{Phase="Anchored",OperationId="placement",RequestFingerprint="fingerprint",AfterWitness="anchored-receipt"}};
        State.Colonies.Add(Colony);Colony.Facilities.Add(Facility);State.Construction.Add(Order);
        Marker=new ColonyPlacementMarker{part=Part,craftPartId=104,worldId=State.WorldId,colonyId=Colony.Id,plotId="plot",operationId="placement",requestFingerprint="fingerprint",templateSha256=Template.CraftSha256};
        Reactor.part=Part;Heat.part=Part;Reactor.heatModule=Heat;Heat.Loop.LoopModules.Add(Heat);
        Part.Modules.AddRange(new PartModule[]{Reactor,Heat,Marker});
    }
    public bool Allowed(out string evidence)=>ColonyRuntime.Operator(Facility,Vessel,10,Template,out evidence);
    public ProtoPartModuleSnapshot Saved(string name)=>Vessel.protoVessel.protoPartSnapshots[0].modules.Single(m=>m.moduleName==name);
    public void Unload()
    {
        Facility.State=Order.State="operational";Facility.Qualification.StaffingQualified=Facility.Qualification.PlacementStable=true;
        var proto=new ProtoPartSnapshot{persistentId=Part.persistentId,partInfo=Part.partInfo};
        foreach(var module in Part.Modules)
        {
            var snapshot=new ProtoPartModuleSnapshot{moduleName=module.moduleName};snapshot.moduleValues.SetValue("persistentId",module.PersistentId.ToString(CultureInfo.InvariantCulture));
            if(module==Marker){snapshot.moduleValues.SetValue("craftPartId","104");snapshot.moduleValues.SetValue("worldId",Marker.worldId);snapshot.moduleValues.SetValue("colonyId",Marker.colonyId);
                snapshot.moduleValues.SetValue("plotId",Marker.plotId);snapshot.moduleValues.SetValue("operationId",Marker.operationId);snapshot.moduleValues.SetValue("requestFingerprint",Marker.requestFingerprint);snapshot.moduleValues.SetValue("templateSha256",Marker.templateSha256);}
            proto.modules.Add(snapshot);
        }
        Vessel.protoVessel=new ProtoVessel();Vessel.protoVessel.protoPartSnapshots.Add(proto);Vessel.loaded=false;
        Facility.Qualification.ReactorContinuation=new ColonyReactorContinuationProof{WorldId=State.WorldId,VesselId=Vessel.id.ToString("D")};Seal();
    }
    public void Seal()=>Facility.Qualification.ReactorContinuation.HardwareHash=ColonyRuntime.Hardware(Vessel);
}

sealed class BankFixture
{
    public Fixture Inner=new Fixture();public Dictionary<uint,Part> Parts=new();public List<USITools.USI_Converter> Converters=new();
    public BankFixture()
    {
        var bytes=File.ReadAllBytes("ExpansePlatform/package/GameData/ExpanseWorldBridge/Templates/power-ranger-bank-v1.manifest.json");
        var source=JsonSerializer.Deserialize<ColonyTemplate>(bytes);var target=Inner.Template;
        foreach(var p in typeof(ColonyTemplate).GetProperties())p.SetValue(target,p.GetValue(source));
        Inner.Facility.TemplateId=target.Id;Inner.Facility.TemplateHash=target.Hash;Inner.Facility.CraftSha256=target.CraftSha256;
        Inner.Order.TemplateId=target.Id;Inner.Order.TemplateHash=target.Hash;
        Inner.Order.Materials=target.Materials.Concat(target.EmbeddedContents).Select(x=>new MaterialRequirement{Resource=x.Resource,Amount=x.Amount}).ToList();
        Inner.Order.Funds=Inner.Order.FundsConsumed=target.BuildFunds;Inner.Order.LaborFunds=target.LaborFunds;Inner.Order.WorkRequired=Inner.Order.WorkCompleted=target.LaborSeconds;
        Inner.Order.Placement.EscrowWitness=ColonyEngine.ConstructionEscrowWitness(Inner.State,Inner.Order.Id);
        Inner.Vessel.parts.Clear();Inner.Facility.PartIds.Clear();
        for(uint id=100;id<=118;id++)
        {
            var part=id==105?Inner.Part:new Part();part.persistentId=id==105?10:id+1000;part.vessel=Inner.Vessel;
            part.partInfo=new AvailablePart{name=ColonyRuntime.BankName(id),partPrefab=part};part.CrewCapacity=id==105?4:0;part.Modules.Clear();
            var marker=new ColonyPlacementMarker{part=part,craftPartId=id,worldId=Inner.State.WorldId,colonyId=Inner.Colony.Id,plotId=Inner.Facility.PlotId,operationId=Inner.Facility.PlacementOperationId,
                requestFingerprint=Inner.Facility.PlacementRequestFingerprint,templateSha256=target.CraftSha256};part.Modules.Add(marker);
            if(id>=101&&id<=104){var converter=new USITools.USI_Converter{part=part};part.Modules.Add(converter);Converters.Add(converter);}
            Inner.Vessel.parts.Add(part);Inner.Facility.PartIds.Add(part.persistentId);Parts.Add(id,part);
        }
    }
    public bool Allowed(out string evidence)=>Inner.Allowed(out evidence);
}

public static class HighLogic{public static object CurrentGame;}
public sealed class CelestialBody{public string bodyName;}
public sealed class Vessel
{
    public Guid id;public bool loaded,packed,LandedOrSplashed,FoundationHeld;public string Foundation;public double PositionError,AngleError;public int HardwareRevision;
    public CelestialBody mainBody;public List<Part> parts=new List<Part>();public ProtoVessel protoVessel;
}
public sealed class Part{public uint persistentId;public int CrewCapacity;public AvailablePart partInfo;public Vessel vessel;public List<PartModule> Modules=new List<PartModule>();}
public sealed class AvailablePart{public string name;public Part partPrefab;}
public class PartModule{public Part part;public uint PersistentId;public bool ReviewedVersion=true;public string moduleName=>GetType().Name;}
public sealed class ProtoVessel{public List<ProtoPartSnapshot> protoPartSnapshots=new List<ProtoPartSnapshot>();}
public sealed class ProtoPartSnapshot{public uint persistentId;public AvailablePart partInfo;public List<ProtoPartModuleSnapshot> modules=new List<ProtoPartModuleSnapshot>();}
public sealed class ProtoPartModuleSnapshot{public string moduleName;public ConfigNode moduleValues=new ConfigNode();}
public sealed class ConfigNode{readonly Dictionary<string,string> values=new Dictionary<string,string>();public string GetValue(string key)=>values.TryGetValue(key,out var value)?value:null;public void SetValue(string key,string value)=>values[key]=value;}
public sealed class NativeLoop{public List<object> LoopModules{get;}=new List<object>();}
namespace SystemHeat
{
    public sealed class ModuleSystemHeatFissionReactor:PartModule{public object heatModule;public bool Enabled;public int NativeEventCalls;}
    public sealed class ModuleSystemHeat:PartModule{public NativeLoop Loop{get;set;}}
}
namespace USITools{public sealed class USI_Converter:PartModule{public bool RecipeChanged;public int NativeCalls;}}
namespace Expanse.WorldBridge
{
    public sealed class ColonyPlacementMarker:PartModule{public uint craftPartId;public string worldId,colonyId,plotId,operationId,requestFingerprint,templateSha256;}
    public static class ColonyPlacementFoundations
    {
        public static bool IsHeld(Vessel v)=>v.FoundationHeld;
        public static bool ReadWitness(Vessel v,out string foundation,out double position,out double angle){foundation=v.Foundation;position=v.PositionError;angle=v.AngleError;return v.FoundationHeld;}
    }
    public sealed partial class ColonyRuntime
    {
        public static ColonyRuntime Current;readonly ColonyState state;readonly object selectedGame;readonly List<ColonyTemplate> templates;public bool Ready=true;
        public ColonyRuntime(ColonyState s,object g,ColonyTemplate t){state=s;selectedGame=g;templates=new List<ColonyTemplate>{t};}
        public static bool Operator(ColonyFacility f,Vessel v,uint p,ColonyTemplate t,out string e)=>TryPaidPowerOperatorCabin(f,v,p,t,out e);
        public static string Hardware(Vessel v)=>ReadReactorHardwareHash(v);
        public static string BankName(uint id)=>RangerBankPartName(id);
        // This detached provider guard is intentionally not a fake native recipe
        // observer. Actual fixed hardware contracts are tested separately.
        static void RequireFixedGeneratorHardware(USITools.USI_Converter module){if(!module.ReviewedVersion||module.RecipeChanged)throw new InvalidOperationException("Actual fixed hardware provider held.");}
        // The actual native hardware sealing implementation is separately tested
        // by reactor contracts. This provider stub exposes its equality boundary.
        static string ReadReactorHardwareHash(Vessel v)=>v.id+"|"+v.HardwareRevision+"|"+string.Join(";",v.protoVessel.protoPartSnapshots.SelectMany(p=>p.modules).Select(m=>m.moduleName+":"+m.moduleValues.GetValue("persistentId")));
        static bool SupportedSystemHeat(PartModule m)=>m.ReviewedVersion&&m.GetType().Namespace=="SystemHeat";
        static uint NativeModuleId(PartModule m)=>m.PersistentId;
        static object ReviewedPrivateField(object o,string n)=>UtilityRead(o,n);
        static object UtilityRead(object o,string n)=>o==null?null:o.GetType().GetField(n,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)?.GetValue(o)??o.GetType().GetProperty(n,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)?.GetValue(o);
        static bool Finite(double n)=>!double.IsNaN(n)&&!double.IsInfinity(n);
    }
}
