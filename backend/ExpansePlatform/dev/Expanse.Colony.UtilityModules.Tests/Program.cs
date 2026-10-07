using Expanse.WorldBridge;
using System.Reflection;

int checks=0;
void Check(string name,Action action){action();checks++;Console.WriteLine("PASS "+name);}
void Assert(bool b){if(!b)throw new Exception("Unexpected utility authority");}
void Reject(string name,Action<Fixture> change,string kind="swap")=>Check(name,()=>{var f=new Fixture();change(f);Assert(!f.Allowed(kind));});
Check("Dependent option observes exact inactive owner without mutation",()=>{var f=new Fixture();var before=f.Owner._swapOption;Assert(f.Allowed("swap"));Assert(ReferenceEquals(before,f.Owner._swapOption)&&!f.Owner.IsActivated&&f.NativeCalls==0);});
Check("Selected active owner requires accounted final native recipe",()=>{var f=new Fixture();f.Owner.IsActivated=true;Assert(f.Allowed("swap"));f.Accounted.Remove(f.Owner);Assert(!f.Allowed("swap"));});
Check("Unselected option remains dependent on current owner recipe",()=>{var f=new Fixture();var other=f.Add(new USITools.USI_ConverterSwapOption());f.Controller.Loadouts.Add(other);f.Bay.currentLoadout=1;f.Owner._swapOption=other;Assert(f.Allowed("swap"));});
Reject("Unloaded prefab cannot certify dependent option",f=>f.Vessel.loaded=false);
Reject("Wrong exact identity holds",f=>f.Option.TestIdentity="USITools.USI_EfficiencyBoosterSwapOption|USITools|2.0.0.0");
Reject("Independent resource input holds",f=>f.Option.resHandler.inputResources.Add(new ModuleResource()));
Reject("Independent output holds",f=>f.Option.resHandler.outputResources.Add(new ModuleResource()));
Reject("Missing controller holds",f=>f.Part.Modules.Remove(f.Controller));
Reject("Duplicate controller holds",f=>f.Add(new USITools.USI_SwapController()));
Reject("Changed native controller inventory holds",f=>f.Controller._converters.Clear());
Reject("Missing native controller owner list holds",f=>f.Controller._converters=null);
Reject("Changed loadout inventory holds",f=>f.Controller.Loadouts.Clear());
Reject("Missing bay holds",f=>f.Part.Modules.Remove(f.Bay));
Reject("Wrong bay controller holds",f=>f.Bay._controller=new USITools.USI_SwapController());
Reject("Out-of-range bay index holds",f=>f.Bay.moduleIndex=2);
Reject("Negative loadout index holds",f=>f.Bay.currentLoadout=-1);
Reject("Changed selection holds until fresh recipe accounting",f=>f.Owner._swapOption=null);
Reject("Missing owner accounting holds",f=>f.Accounted.Clear());
Reject("Different owner part holds",f=>f.Owner.part=new Part());
Reject("Different physical vessel holds",f=>f.Part.vessel=new Vessel());
Reject("Duplicate native module reference holds",f=>f.Part.Modules.Add(f.Option));
Check("Antenna full current input bounds both active and inactive states",()=>{var f=new Fixture();foreach(bool active in new[]{false,true}){f.Antenna.generatorIsActive=active;Assert(f.Demand(out var d)&&d==.0190365393871588);}Assert(f.NativeCalls==0);});
Check("A changed observed rate updates demand immediately",()=>{var f=new Fixture();Assert(f.Demand(out var before));f.Antenna.resHandler.inputResources[0].rate=1;Assert(f.Demand(out var after)&&before!=after&&after==1);});
Check("Unknown generator identity never grants accounting",()=>{var f=new Fixture();f.Antenna.TestIdentity="unknown";Assert(!f.Demand(out _));});
Check("Unloaded antenna prefab is not actual demand evidence",()=>{var f=new Fixture();f.Vessel.loaded=false;Assert(!f.Demand(out _));});
foreach(var bad in new[]{double.NaN,double.PositiveInfinity,-1d,1e7})Check("Malformed antenna rate "+bad,()=>{var f=new Fixture();f.Antenna.resHandler.inputResources[0].rate=bad;Assert(!f.Demand(out var d)&&d==0);});
Check("Throttle-controlled antenna holds",()=>{var f=new Fixture();f.Antenna.isThrottleControlled=true;Assert(!f.Demand(out _));});
Check("Unknown resource handler holds",()=>{var f=new Fixture();f.Antenna.resHandler=new CustomHandler();Assert(!f.Demand(out _));});
Check("Missing resource handler holds",()=>{var f=new Fixture();f.Antenna.resHandler=null;Assert(!f.Demand(out _));});
Check("Different native resource-handler owner holds",()=>{var f=new Fixture();f.Antenna.resHandler.partModule=f.Owner;Assert(!f.Demand(out _));});
Check("Null resource row holds",()=>{var f=new Fixture();f.Antenna.resHandler.inputResources.Add(null);Assert(!f.Demand(out _));});
Check("Unknown material generator input holds",()=>{var f=new Fixture();f.Antenna.resHandler.inputResources[0].name="Fuel";Assert(!f.Demand(out _));});
Check("Unknown antenna output holds",()=>{var f=new Fixture();f.Antenna.resHandler.outputResources.Add(new ModuleResource());Assert(!f.Demand(out _));});
Check("Empty antenna input inventory holds",()=>{var f=new Fixture();f.Antenna.resHandler.inputResources.Clear();Assert(!f.Demand(out _));});
Check("Antenna oversized inventory holds",()=>{var f=new Fixture();for(int i=0;i<65;i++)f.Antenna.resHandler.inputResources.Add(new ModuleResource());Assert(!f.Demand(out _));});
Check("Multiple current EC rows bound once",()=>{var f=new Fixture();f.Antenna.resHandler.inputResources.Add(new ModuleResource{rate=1});Assert(f.Demand(out var d)&&d==1.0190365393871588);});
Check("Toggler requires exact currently bound accounted owner",()=>{var f=new Fixture();Assert(f.Allowed("toggle"));f.Accounted.Remove(f.Antenna);Assert(!f.Allowed("toggle"));});
Reject("Unbound actual transmitter holds",f=>f.Antenna.moduleDT=null,"toggle");
Reject("Different bound transmitter holds",f=>f.Antenna.moduleDT=new ModuleDataTransmitter(),"toggle");
Reject("Ambiguous transmitter holds",f=>f.Add(new ModuleDataTransmitter()),"toggle");
Reject("Toggler independent resource handler holds",f=>f.Toggle.resHandler.inputResources.Add(new ModuleResource()),"toggle");
Check("Settled deployed animation requires current actual owner inventory",()=>{var f=new Fixture();Assert(f.Allowed("animation"));f.Accounted.Remove(f.Owner);Assert(!f.Allowed("animation"));});
Reject("Undeployed animation holds",f=>f.Animation.isDeployed=false,"animation");
Reject("Uninitialized animation holds",f=>f.Animation._hasBeenInitialized=false,"animation");
Reject("Moving deployment holds",f=>f.Animation.DeployAnimation.isPlaying=true,"animation");
Reject("Incomplete animation state holds",f=>f.Animation.DeployAnimation.State.normalizedTime=.5,"animation");
Reject("Nonfinite deployment state holds",f=>f.Animation.DeployAnimation.State.normalizedTime=double.NaN,"animation");
Reject("Partial deployment resource payment holds",f=>f.Animation.partialDeployCostPaid=.1,"animation");
Reject("Missing initialized owner list holds",f=>f.Animation._Modules=null,"animation");
Reject("Changed native animated owner list holds",f=>f.Animation._Modules.Add(new AnimatedOwner()),"animation");
Reject("No actual recipe/reactor owner holds",f=>f.Part.Modules.Remove(f.Owner),"animation");
Check("Exact settled radiator controller requires qualified actual owner",()=>{var f=new Fixture();Assert(f.Allowed("radiator"));f.Reactor.Qualified=false;Assert(!f.Allowed("radiator"));});
Reject("Retracted radiator holds",f=>f.Radiator.deployState=ModuleDeployablePart.DeployState.RETRACTED,"radiator");
Reject("Unaccounted radiator owner holds",f=>f.Accounted.Remove(f.HeatRadiator),"radiator");
Reject("No qualified thermal-owner witness holds",f=>f.Reactor.Accounted.Clear(),"radiator");
Reject("Duplicate thermal owner holds",f=>f.Add(new SystemHeat.ModuleSystemHeatRadiator()),"radiator");
Reject("Radiator controller independent resources hold",f=>f.Radiator.resHandler.inputResources.Add(new ModuleResource()),"radiator");
Console.WriteLine(checks+" source-linked detached utility-module checks passed. Identities/KSP objects are provider stubs; no native acceptance.");

sealed class Fixture
{
 public Vessel Vessel=new Vessel();public Part Part=new Part();public HashSet<PartModule> Accounted=new();public ColonyRuntime.ReactorUtilityWitness Reactor=new();public int NativeCalls=0;
 public USITools.USI_EfficiencyBoosterSwapOption Option;public USITools.USI_Converter Owner;public USITools.USI_SwapController Controller;public USITools.USI_SwappableBay Bay;
 public CommNetAntennasConsumptor.ModuleGeneratorAntenna Antenna;public CommNetAntennasConsumptor.ModuleAntennaToggler Toggle;public ModuleDataTransmitter Transmitter;public USITools.USIAnimation Animation;public ModuleDeployableRadiator Radiator;public SystemHeat.ModuleSystemHeatRadiator HeatRadiator;
 public Fixture(){Part.vessel=Vessel;Vessel.parts.Add(Part);Option=Add(new USITools.USI_EfficiencyBoosterSwapOption());Owner=Add(new USITools.USI_Converter{_swapOption=Option});Controller=Add(new USITools.USI_SwapController{Loadouts=new(){Option},_converters=new(){Owner}});Bay=Add(new USITools.USI_SwappableBay{_controller=Controller});Antenna=Add(new CommNetAntennasConsumptor.ModuleGeneratorAntenna());Antenna.resHandler.inputResources.Add(new ModuleResource{rate=.0190365393871588});Transmitter=Add(new ModuleDataTransmitter());Antenna.moduleDT=Transmitter;Toggle=Add(new CommNetAntennasConsumptor.ModuleAntennaToggler());Animation=Add(new USITools.USIAnimation());Radiator=Add(new ModuleDeployableRadiator());HeatRadiator=Add(new SystemHeat.ModuleSystemHeatRadiator());Reactor.Qualified=true;Reactor.Accounted.Add(HeatRadiator);Accounted.UnionWith(new PartModule[]{Owner,Antenna,Transmitter,HeatRadiator});}
 public T Add<T>(T module) where T:PartModule {module.part=Part;module.resHandler.partModule=module;Part.Modules.Add(module);return module;}
 public bool Allowed(string kind)=>ColonyRuntime.Dependent(kind=="swap"?Option:kind=="toggle"?Toggle:kind=="animation"?Animation:Radiator,Vessel,Accounted,Reactor);
 public bool Demand(out double d)=>ColonyRuntime.Demand(Antenna,Vessel,out d);
}
public class PartModule {public Part part;public ModuleResourceHandler resHandler=new();public string TestIdentity;}
public class ModuleResourceHandler {public PartModule partModule;public List<ModuleResource> inputResources=new(),outputResources=new();}
public class CustomHandler:ModuleResourceHandler{}
public class ModuleResource {public string name="ElectricCharge";public double rate;}
public class ModuleGenerator:PartModule {public bool isThrottleControlled,generatorIsActive;public void FixedUpdate(){}}
public class BaseConverter:PartModule {public bool IsActivated;}
public class ModuleDataTransmitter:PartModule{}
public interface IAnimatedModule{}
public class AnimatedOwner:PartModule,IAnimatedModule{}
public class Part {public Vessel vessel;public List<PartModule> Modules=new();}
public class Vessel {public bool loaded=true;public List<Part> parts=new();}
public class ModuleDeployablePart:PartModule {public enum DeployState{EXTENDED,RETRACTED}public DeployState deployState=DeployState.EXTENDED;}
public class ModuleDeployableRadiator:ModuleDeployablePart{}
public class FakeAnimation {public bool isPlaying;public FakeAnimationState State=new();public FakeAnimationState this[string key]=>State;}
public class FakeAnimationState {public double normalizedTime=1;}
namespace USITools {
 public interface ISwappableConverter {bool IsStandalone{get;}}
 public class AbstractSwapOption:PartModule{}
 public class USI_ConverterSwapOption:AbstractSwapOption{}
 public class USI_EfficiencyBoosterSwapOption:USI_ConverterSwapOption{}
 public class USI_Converter:BaseConverter,ISwappableConverter {public bool IsStandalone=>false;public AbstractSwapOption _swapOption;}
 public class USI_SwapController:PartModule {public List<AbstractSwapOption> Loadouts;public List<ISwappableConverter> _converters;}
 public class USI_SwappableBay:PartModule {public int moduleIndex,currentLoadout;public USI_SwapController _controller;}
 public class USIAnimation:PartModule {public bool _hasBeenInitialized=true,isDeployed=true;public double partialDeployCostPaid;public string deployAnimationName="deploy";public FakeAnimation DeployAnimation=new();public List<IAnimatedModule> _Modules=new();}
}
namespace CommNetAntennasConsumptor {public class ModuleGeneratorAntenna:ModuleGenerator{public ModuleDataTransmitter moduleDT;}public class ModuleAntennaToggler:PartModule{}}
namespace SystemHeat {public class ModuleSystemHeatRadiator:PartModule{}}
namespace Expanse.WorldBridge {
 public partial class ColonyRuntime {
  public sealed class ReactorUtilityWitness {public bool Qualified;public HashSet<PartModule> Accounted=new();}
  static string UtilityModuleIdentity(PartModule m)=>m.TestIdentity??(m.GetType().FullName+"|"+(m.GetType().FullName.StartsWith("USITools.")?"USITools|1.0.0.0":m.GetType().FullName.StartsWith("CommNet")?"CommNetAntennasConsumptor|3.5.8.0":"Assembly-CSharp|0.0.0.0"));
  static object ReviewedPrivateField(object o,string n)=>o.GetType().GetField(n,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance)?.GetValue(o);
  static bool Finite(double d)=>!double.IsNaN(d)&&!double.IsInfinity(d);
  public static bool Dependent(PartModule m,Vessel v,HashSet<PartModule> a,ReactorUtilityWitness r)=>TryDependentUtilityModule(m,v,a,r);
  public static bool Demand(PartModule m,Vessel v,out double d)=>TryAntennaUtilityDemand(m,v,out d);
 }
}
