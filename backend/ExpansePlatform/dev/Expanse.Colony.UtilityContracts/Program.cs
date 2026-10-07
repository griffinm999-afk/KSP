using Mono.Cecil;
if(args.Length!=1 || !Path.IsPathFullyQualified(args[0]))throw new ArgumentException("Pass the actual installed KSP root for read-only contract inspection.");
var ksp=AssemblyDefinition.ReadAssembly(Path.Combine(args[0],"KSP_x64_Data","Managed","Assembly-CSharp.dll"));
var brp=AssemblyDefinition.ReadAssembly(Path.Combine(args[0],"GameData","BackgroundResourceProcessing","Plugins","BackgroundResourceProcessing.dll"));
var usi=AssemblyDefinition.ReadAssembly(Path.Combine(args[0],"GameData","000_USITools","USITools.dll"));
var systemHeat=AssemblyDefinition.ReadAssembly(Path.Combine(args[0],"GameData","SystemHeat","Plugin","SystemHeat.dll"));
int checks=0;
void Check(bool condition,string label){if(!condition)throw new Exception("Installed utility contract mismatch: "+label);checks++;Console.WriteLine("PASS "+label);}
TypeDefinition Type(AssemblyDefinition assembly,string name)=>assembly.MainModule.Types.Single(t=>t.FullName==name);
bool Member(TypeDefinition type,string name)=>type.Fields.Any(f=>f.Name==name && f.IsPublic) || type.Properties.Any(p=>p.Name==name && p.GetMethod?.IsPublic==true);
var processor=Type(brp,"BackgroundResourceProcessing.BackgroundResourceProcessor");
Check(brp.Name.Version==new Version(0,2,7,0),"Exact supported BRP assembly version");
Check(new[]{"Converters","Inventories","LastChangepoint"}.All(n=>Member(processor,n)),"Actual current background recipe and time witnesses exposed");
Check(processor.Methods.Single(m=>m.Name=="LoadVessel").Body.Instructions.Any(i=>i.Operand is MethodReference m && m.Name=="ClearVesselState"),"Loaded native restore clears background recipes; no loaded-recipe certification assumption");
var update=processor.Methods.Single(m=>m.Name=="UpdateBackgroundState");
Check(update.IsPublic && update.Body.Instructions.Any(i=>i.Operand is FieldReference f && f.FullName=="System.Boolean Vessel::loaded") && update.Body.Instructions.Any(i=>i.Operand is MethodReference m && m.Name=="UpdateState"),"Unloaded catch-up delegated to installed native producer");
var converter=Type(brp,"BackgroundResourceProcessing.Core.ResourceConverter");
Check(new[]{"Inputs","Outputs","FlightId","Rate","Behaviour"}.All(n=>Member(converter,n)),"Exact actual converter identity/input/output witnesses available");
var generator=Type(ksp,"ModuleGenerator");
Check(new[]{"isAlwaysActive","isThrottleControlled","generatorIsActive","efficiency"}.All(n=>Member(generator,n)),"Actual continuous stock generator state available");
var specialist=Type(ksp,"BaseConverter");
Check(Member(specialist,"UseSpecialistBonus") && Member(specialist,"ExperienceEffect") && Type(ksp,"ProtoCrewMember").Methods.Any(m=>m.Name=="HasEffect" && m.IsPublic),"Real configured converter experience effect and actual Kerbal effect qualification available");
var repair=usi.MainModule.Types.Single(t=>t.Name=="ModuleAutoRepairer");
Check(Member(repair,"RepairRange"),"Installed actual Engineer workshop repair range available");
var core=Type(ksp,"ModuleCoreHeat");
Check(new[]{"CoreTemperature","CoreShutdownTemp","CoreTempGoal","MaxCoolant"}.All(n=>Member(core,n)),"Passive RTG thermal model and actual core temperature available");
var configs=Directory.GetFiles(Path.Combine(args[0],"GameData","BackgroundResourceProcessing","Config"),"*.cfg",SearchOption.AllDirectories).Select(File.ReadAllText);
Check(configs.Any(s=>s.Contains("name = ModuleGenerator") && s.Contains("adapter = BackgroundGenericConverter") && s.Contains("!%isThrottleControlled && (%isAlwaysActive || %generatorIsActive)")),"Installed continuous no-throttle native generator adapter condition");
foreach(var name in new[]{"ModuleTripLogger","ModuleStructuralNode","ModulePartVariants","FXModuleLookAtConstraint"})
Check(!Type(ksp,name).Methods.Where(m=>m.HasBody).SelectMany(m=>m.Body.Instructions).Any(i=>i.Operand is MethodReference m && (m.Name.Contains("RequestResource") || m.DeclaringType.Name=="ModuleResourceHandler" && m.Name.StartsWith("UpdateModuleResource"))),"Reviewed passive stock "+name+" has no native resource consumption path");
Check(systemHeat.Name.Version==new Version(0,9,1,0),"Exact supported SystemHeat assembly version");
var reactor=Type(systemHeat,"SystemHeat.ModuleSystemHeatFissionReactor");
Check(new[]{"Enabled","Hibernating","GeneratesElectricity","CoreIntegrity","Efficiency","ElectricalGeneration","HeatGeneration","InternalCoreTemperature","CurrentSafetyOverride","CriticalTemperature","NominalTemperature","LastUpdateTime"}.All(n=>Member(reactor,n)),"Actual reactor state, curves, core thresholds and update time available");
Check(new[]{"heatModule","inputs","outputs","fuelCheckPassed"}.All(n=>reactor.Fields.Any(f=>f.Name==n && f.IsFamily)),"Exact read-only native protected fuel and loop witnesses available");
Check(reactor.Methods.Single(m=>m.Name=="HandleResourceActivities").Body.Instructions.Any(i=>i.Operand is MethodReference m && m.Name=="RequestResource") && reactor.Methods.Single(m=>m.Name=="CheckFull").Body.Instructions.Any(i=>i.Operand is FieldReference f && f.FullName=="System.Double PartResource::maxAmount"),"Native reactor owns fuel/production and checks its local waste room");
var radiator=Type(systemHeat,"SystemHeat.ModuleSystemHeatRadiator");
Check(radiator.BaseType.FullName=="ModuleActiveRadiator" && new[]{"temperatureCurve","affectedByAtmosphere","atmosphereCurve","affectedByAcceleration","accelerationCurve"}.All(n=>Member(radiator,n)) && radiator.Fields.Any(f=>f.Name=="radiativeFlux" && f.IsFamily),"Actual active radiator curve/environment/observed flux witnesses available");
Check(Member(Type(systemHeat,"SystemHeat.HeatLoop"),"LoopModules") && Member(Type(systemHeat,"SystemHeat.ModuleSystemHeat"),"Loop"),"Exact physical heat-loop module membership available");
Check(Member(Type(ksp,"PartModule"),"PersistentId") && Type(ksp,"PartModule").Methods.Any(m=>m.Name=="GetPersistentId" && m.IsPublic),"Native thermal module public PersistentId getter and BRP identity API available");
Check(reactor.Methods.Any(m=>m.Name=="EnableReactor" && m.IsPublic && m.Parameters.Count==0) && reactor.Methods.Any(m=>m.Name=="SetManualControl" && m.IsPublic && m.Parameters.Count==1 && m.Parameters[0].ParameterType.FullName=="System.Boolean"),"Installed native initial activation and manual-control APIs available");
Check(reactor.Fields.Any(f=>f.Name=="CurrentReactorThrottle" && f.IsPublic && f.FieldType.FullName=="System.Single" && f.CustomAttributes.Any(a=>a.AttributeType.Name=="KSPField")) && Member(reactor,"CurrentHeatGeneration"),"Real public persisted PAW throttle setting and current native heat-generation witness available");
Check(Type(ksp,"ModuleActiveRadiator").Methods.Any(m=>m.Name=="Activate" && m.IsPublic && m.Parameters.Count==0),"Installed native active-radiator activation event available");
var consumer=Type(usi,"USITools.ModuleLogisticsConsumer");var logisticsCheck=consumer.Methods.Single(m=>m.Name=="CheckLogistics");
Check(logisticsCheck.Parameters.Select(p=>p.Name).SequenceEqual(new[]{"resList","output"}) && logisticsCheck.Body.Instructions.Any(i=>i.Operand is MethodReference m && m.Name=="FetchResources") && logisticsCheck.Body.Instructions.Any(i=>i.Operand is MethodReference m && m.Name=="StoreResources"),"Exact passive USI transfer callback wraps native source debit and receiver credit");
Check(consumer.Methods.Single(m=>m.Name=="StoreResources").Body.Instructions.Any(i=>i.OpCode.Code==Mono.Cecil.Cil.Code.Stfld && i.Operand is FieldReference f && f.FullName=="System.Double PartResource::amount"),"Native nearby delivery writes tank amounts directly, outside stock request telemetry");
Check(specialist.Methods.Single(m=>m.Name=="FixedUpdate").Body.Instructions.Any(i=>i.Operand is MethodReference m && m.Name=="PrepareRecipe") && specialist.Methods.Single(m=>m.Name=="FixedUpdate").Body.Instructions.Any(i=>i.Operand is MethodReference m && m.Name=="GetEfficiencyMultiplier") && specialist.Fields.Any(f=>f.Name=="_preCalculateEfficiency" && f.IsFamily),"Final native recipe and post-recipe multiplier have separate observed boundaries");
Check(Type(usi,"USITools.USI_Converter").Methods.Single(m=>m.Name=="PrepareRecipe").Body.Instructions.Any(i=>i.Operand is MethodReference m && m.DeclaringType.Name.StartsWith("AbstractSwapOption")),"USI final recipe includes current installed swap-option selection");
Check(new[]{Type(ksp,"ModuleResourceConverter"),Type(ksp,"ModuleResourceHarvester"),Type(usi,"USITools.USI_Converter"),Type(usi,"USITools.USI_Harvester")}.All(t=>t.Methods.Single(m=>m.Name=="PrepareRecipe").IsFamily),"Native PrepareRecipe methods are protected; observer lookup must include nonpublic instance methods");
var activation=specialist.Methods.Single(m=>m.Name=="UpdateConverterStatus").Body.Instructions;
Check(activation.Any(i=>i.OpCode.Code==Mono.Cecil.Cil.Code.Stfld && i.Operand is FieldReference f && f.Name=="DirtyFlag" && i.Previous.Operand is FieldReference previous && previous.Name=="IsActivated"),"Stock DirtyFlag is previous activation state, not a recipe configuration dirty flag");
bool ContinuousCallsResources(TypeDefinition type)
{
 var visited=new HashSet<MethodDefinition>();
 bool Visit(MethodDefinition method)
 {
  if(!visited.Add(method) || !method.HasBody)return false;
  foreach(var i in method.Body.Instructions)
  {
   if(i.Operand is not MethodReference called)continue;
   if(called.Name=="RequestResource" || called.Name.StartsWith("UpdateModuleResource") || called.Name is "ConsumeCharge" or "TakeResources")return true;
   if(called.DeclaringType.FullName==type.FullName)
   {var own=type.Methods.SingleOrDefault(m=>m.FullName==called.FullName);if(own!=null && Visit(own))return true;}
  }
  return false;
 }
 return type.Methods.Where(m=>m.Name is "Update" or "FixedUpdate" or "LateUpdate" or "OnUpdate" or "OnFixedUpdate").Any(Visit);
}
foreach(var name in new[]{"ModuleSystemHeatFissionFuelContainer","ModuleSystemHeatColorAnimator"})
 Check(!ContinuousCallsResources(systemHeat.MainModule.Types.Single(t=>t.Name==name)),"Reviewed installed SystemHeat "+name+" has no continuous resource-consumption callback");
var thermalFixed=Type(systemHeat,"SystemHeat.SystemHeatVessel").Methods.Single(m=>m.Name=="FixedUpdate");
Check(thermalFixed.HasBody && thermalFixed.Body.Instructions.Any(i=>i.Operand is MethodReference m && m.Name=="Simulate") && !thermalFixed.Body.Instructions.Any(i=>i.Operand is FieldReference f && f.Name=="packed"),"Native loaded SystemHeat vessel update calls thermal simulation without a packed exclusion");
foreach(var name in new[]{"ModuleWeightDistributableCargo","ModuleWeightDistributor","USI_ModuleRecycleBin","USI_SwapController","USI_SwappableBay","USI_ConverterSwapOption"})
 Check(!ContinuousCallsResources(usi.MainModule.Types.Single(t=>t.Name==name)),"Reviewed installed USI "+name+" has no continuous resource-consumption callback");
var firespitter=AssemblyDefinition.ReadAssembly(Path.Combine(args[0],"GameData","Firespitter","Plugins","Firespitter.dll"));
Check(firespitter.Name.Version==new Version(7,3,7660,26532) && new[]{"FSfuelSwitch","FStextureSwitch2"}.All(n=>!ContinuousCallsResources(firespitter.MainModule.Types.Single(t=>t.Name==n))),"Exact installed Firespitter tank/texture configuration has no continuous EC callback");
var cryoPath=Directory.GetFiles(Path.Combine(args[0],"GameData"),"SimpleBoiloff.dll",SearchOption.AllDirectories).Single();var cryo=AssemblyDefinition.ReadAssembly(cryoPath);var cryoType=Type(cryo,"SimpleBoiloff.ModuleCryoTank");
Check(cryo.Name.Version==new Version(0,2,1,0) && Member(cryoType,"CoolingEnabled") && cryoType.Methods.Single(m=>m.Name=="FixedUpdate").Body.Instructions.Any(i=>i.Operand is FieldReference f && f.Name=="CoolingEnabled"),"Exact installed cryogenic EC update is gated by actual CoolingEnabled mode");
Check(Type(ksp,"Part").Fields.Any(f=>f.Name=="crewTransferAvailable" && f.IsPublic) && Type(ksp,"CrewTransfer").Methods.Single(m=>m.Name=="MoveCrewTo").Body.Instructions.Any(i=>i.Operand is MethodReference m && m.Name=="RemoveCrewmember") && Type(ksp,"Part").Methods.Single(m=>m.Name=="AddCrewmemberAt").Body.Instructions.Any(i=>i.Operand is MethodReference m && m.Name=="RegisterExperienceTraits"),"Actual stock crew cabin transfer uses removal/addition and registers native workplace traits");
string HashFile(string path)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
Check(HashFile(Path.Combine(args[0],"GameData","000_USITools","USITools.dll"))=="B67965E8C41A79634B17216E987222F57D4A5E1D584FA2FF92A803F72243FAC0","Source15 exact audited USITools bytes");
var booster=Type(usi,"USITools.USI_EfficiencyBoosterSwapOption");
Check(booster.BaseType.FullName=="USITools.USI_ConverterSwapOption"&&!ContinuousCallsResources(booster)&&booster.Methods.Where(m=>!m.IsConstructor).All(m=>m.Name is "ApplyConverterChanges" or "GetInfo"),"Exact booster is a dependent option without independent lifecycle/resource tick");
Check(booster.Methods.Single(m=>m.Name=="ApplyConverterChanges").Body.Instructions.Any(i=>i.Operand is MethodReference m&&m.DeclaringType.FullName=="USITools.USI_ConverterSwapOption"&&m.Name=="ApplyConverterChanges")&&Type(usi,"USITools.USI_SwapController").Fields.Any(f=>f.Name=="_converters"&&f.IsPrivate)&&Type(usi,"USITools.USI_SwappableBay").Fields.Any(f=>f.Name=="_controller"&&f.IsPrivate),"Native dependent recipe, controller-owner and bay-controller boundaries available");
var animationType=Type(usi,"USITools.USIAnimation");
Check(!ContinuousCallsResources(animationType)&&animationType.Fields.Any(f=>f.Name=="_hasBeenInitialized"&&f.IsPrivate)&&animationType.Fields.Any(f=>f.Name=="_Modules"&&f.IsPrivate)&&Member(animationType,"isDeployed")&&Member(animationType,"partialDeployCostPaid"),"USIAnimation recurring tick has no resource callback; actual initialized/deployed/owner witnesses remain required");
Check(animationType.Methods.Single(m=>m.Name=="DeployModule").Body.Instructions.Any(i=>i.Operand is MethodReference m&&m.Name=="CheckAndConsumeResources")&&animationType.Methods.Single(m=>m.Name=="EnableModules").Body.Instructions.Any(i=>i.Operand is MethodReference m&&m.Name=="EnableModule"),"Deployment consumes resources and changes owner state; utility exception grants no deployment");
var antennaPath=Path.Combine(args[0],"GameData","CommNetAntennasConsumptor","Plugins","CommNetAntennasConsumptor.dll");
var antennaAssembly=AssemblyDefinition.ReadAssembly(antennaPath);
Check(antennaAssembly.Name.Version==new Version(3,5,8,0)&&HashFile(antennaPath)=="B2FD7F5E640A179FBDA130726C43AC142247A2AFC7B2B33F2513EDE3105276B0","Source15 exact audited antenna identity and bytes");
var antennaType=Type(antennaAssembly,"CommNetAntennasConsumptor.ModuleGeneratorAntenna");
Check(antennaType.BaseType.FullName=="ModuleGenerator"&&!antennaType.Methods.Any(m=>m.Name=="FixedUpdate")&&antennaType.Fields.Any(f=>f.Name=="moduleDT"&&f.IsPrivate),"Antenna inherits exact stock input tick and exposes actual paired transmitter witness");
Check(antennaType.Methods.Single(m=>m.Name=="LateUpdate").Body.Instructions.Any(i=>i.Operand is MethodReference m&&m.Name=="CanComm")&&antennaType.Methods.Single(m=>m.Name=="LateUpdate").Body.Instructions.Any(i=>i.Operand is MethodReference m&&m.Name=="Activate"),"CanComm activation transitions require full rated current demand rather than inactive-sample credit");
Check(!ContinuousCallsResources(Type(antennaAssembly,"CommNetAntennasConsumptor.ModuleAntennaToggler")),"Exact antenna toggler has no independent continuous resource tick");
var generatorTick=Type(ksp,"ModuleGenerator").Methods.Single(m=>m.Name=="FixedUpdate").Body.Instructions.ToArray();
int InputCallIndex(string name)=>Array.FindIndex(generatorTick,i=>i.Operand is MethodReference m&&m.Name==name);
Check(InputCallIndex("UpdateModuleResourceInputs")>=0&&InputCallIndex("ApplyEfficiencyAdjustments")>InputCallIndex("UpdateModuleResourceInputs")&&generatorTick.Any(i=>i.OpCode.Code==Mono.Cecil.Cil.Code.Stfld&&i.Operand is FieldReference f&&f.Name=="throttle"&&i.Previous.OpCode.Code==Mono.Cecil.Cil.Code.Ldc_R4&&(float)i.Previous.Operand==1),"Unthrottled stock generator requests at multiplier1 before output-efficiency adjustments");
Check(Type(ksp,"ModuleResourceHandler").Methods.Where(m=>m.Name=="UpdateModuleResourceInputs"&&m.HasBody).Any(m=>m.Body.Instructions.Any(i=>i.Operand is FieldReference f&&f.Name=="rate")&&m.Body.Instructions.Any(i=>i.Operand is MethodReference c&&c.Name=="RequestResource")&&m.Body.Instructions.Any(i=>i.Operand is MethodReference c&&c.Name=="get_fixedDeltaTime")),"Installed handler requests actual rate × multiplier × fixedDeltaTime");
Check(Type(ksp,"ModuleDeployableRadiator").BaseType.FullName=="ModuleDeployablePart"&&!ContinuousCallsResources(Type(ksp,"ModuleDeployableRadiator")),"Exact stock radiator deployment type is a dependent controller; native thermal owner remains mandatory");
Console.WriteLine(checks+" read-only installed utility API/config contracts passed. This establishes adapter compatibility; actual loaded/packed/unloaded resource and thermal acceptance remains separate.");
