using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Expanse.WorldBridge
{
    public sealed partial class ColonyRuntime
    {
        // A synchronous saved-state model. It retains no loaded recipe, module
        // pointer binding, animation time or temperature as a current observation.
        sealed class UnloadedUtilityDependencies
        {
            internal bool Valid;
            internal string Reason="Actual unloaded utility owner observation is unavailable.";
            internal string Evidence="Unloaded saved-state dependency model unavailable; no loaded terminal animation is inferred.";
            internal Vessel Vessel;
            internal double Ut;
            internal object Processor;
            internal object[] Converters;
            internal ConfigNode[] Adapters;
            internal ProtoPartSnapshot[] Parts;
            readonly List<string> witnesses=new List<string>();

            internal ProtoPartSnapshot Part(int index)=>Valid && index>=0 && index<Parts.Length ? Parts[index] : null;
            static string Value(ConfigNode node,string name)
            {
                if(node==null || node.GetValues(name).Length!=1)throw new InvalidOperationException("Saved/configured "+name+" is absent or ambiguous.");
                return node.GetValue(name);
            }
            static bool Bool(ConfigNode node,string name)
            {
                if(!bool.TryParse(Value(node,name),out bool result))throw new InvalidOperationException("Saved "+name+" is malformed.");return result;
            }
            static double Number(ConfigNode node,string name)
            {
                if(!double.TryParse(Value(node,name),NumberStyles.Float,CultureInfo.InvariantCulture,out double result) || !Finite(result))throw new InvalidOperationException("Saved/configured "+name+" is invalid.");return result;
            }
            static int Integer(ConfigNode node,string name)
            {
                if(!int.TryParse(Value(node,name),NumberStyles.Integer,CultureInfo.InvariantCulture,out int result))throw new InvalidOperationException("Saved/configured "+name+" is invalid.");return result;
            }
            static string ConfigText(ConfigNode node,string name,string fallback)
            {
                return node.HasValue(name) ? Value(node,name) : fallback;
            }
            static bool ConfigBool(ConfigNode node,string name,bool fallback)
                =>node.HasValue(name) ? Bool(node,name) : fallback;
            static double ConfigNumber(ConfigNode node,string name,double fallback)
                =>node.HasValue(name) ? Number(node,name) : fallback;

            internal void Validate()
            {
                if(Vessel==null || Vessel.loaded || Vessel.protoVessel?.protoPartSnapshots==null || !RemoteBrpInventoryGateway.SupportedProviderAvailable || Processor==null ||
                    Processor.GetType().FullName!="BackgroundResourceProcessing.BackgroundResourceProcessor" || Processor.GetType().Assembly.GetName().Name!="BackgroundResourceProcessing" || Processor.GetType().Assembly.GetName().Version!=new Version(0,2,7,0))throw new InvalidOperationException("Supported current unloaded proto/BRP authority is absent.");
                var attached=UtilityRead(Vessel,"vesselModules") as IEnumerable;
                if(attached==null || !ReferenceEquals(UtilityRead(Processor,"Vessel"),Vessel) || attached.Cast<object>().Count(x=>x!=null && x.GetType().FullName=="BackgroundResourceProcessing.BackgroundResourceProcessor")!=1 ||
                    !attached.Cast<object>().Any(x=>ReferenceEquals(x,Processor)))throw new InvalidOperationException("Attached native BRP owner changed.");
                double age=Ut-UtilityNumber(Processor,"LastChangepoint",double.NaN);
                if(!Finite(Ut) || !Finite(age) || age<0 || age>10)throw new InvalidOperationException("BRP dependency state is not current after native catch-up.");
                Parts=Vessel.protoVessel.protoPartSnapshots.ToArray();
                if(Parts.Length==0 || Parts.Length>512 || Parts.Any(p=>p?.partInfo?.partPrefab==null || p.partInfo.partConfig==null || p.persistentId==0 || p.flightID==0) ||
                    Parts.Select(p=>p.persistentId).Distinct().Count()!=Parts.Length || Parts.Select(p=>p.flightID).Distinct().Count()!=Parts.Length)throw new InvalidOperationException("Bounded distinct actual proto part identities are unavailable.");
                var ids=new HashSet<uint>();
                foreach(var part in Parts)
                {
                    var native=part.partInfo.partPrefab.Modules.Cast<PartModule>().ToArray();var saved=part.modules.ToArray();var config=part.partInfo.partConfig.GetNodes("MODULE");
                    if(native.Length>128 || saved.Length>128 || saved.Length<native.Length || native.Any(m=>m==null))throw new InvalidOperationException("Actual proto/configured module inventory is incomplete or exceeds its bound.");
                    var configMap=UtilityNativeConfigurationMap(part.partInfo);
                    for(int i=0;i<saved.Length;i++)
                    {
                        if(saved[i]==null || saved[i].moduleValues==null)throw new InvalidOperationException("Actual saved module is absent.");
                        if(i>=native.Length)
                        {
                            if(saved[i].moduleName!="ColonyPlacementMarker" && saved[i].moduleName!="ExpanseColonyPlacementMarker")throw new InvalidOperationException("Actual proto has an unknown added module.");
                        }
                        else if(saved[i].moduleName!=native[i].moduleName || configMap[i]>=0 && Value(config[configMap[i]],"name")!=native[i].moduleName)throw new InvalidOperationException("Actual proto/native/configured module order changed.");
                        if(saved[i].moduleValues.HasValue("persistentId"))
                        {
                            if(!uint.TryParse(Value(saved[i].moduleValues,"persistentId"),NumberStyles.Integer,CultureInfo.InvariantCulture,out uint id) || id!=0 && !ids.Add(id))throw new InvalidOperationException("Actual native module identity is invalid or duplicated.");
                        }
                    }
                }
                var enumerable=UtilityRead(Processor,"Converters") as IEnumerable;
                if(enumerable==null)throw new InvalidOperationException("Current native BRP recipes are unavailable.");
                Converters=enumerable.Cast<object>().Take(513).ToArray();
                if(Converters.Length>512 || Converters.Any(c=>c==null))throw new InvalidOperationException("Actual native BRP recipe inventory exceeds its bound.");
                foreach(var converter in Converters)
                {
                    if(!(UtilityRead(converter,"FlightId") is uint flight) || flight==0 || !(UtilityRead(converter,"ModuleId") is uint mid) || mid==0 ||
                        Parts.Count(p=>p.flightID==flight && p.modules.Any(m=>m.moduleValues.GetValue("persistentId")==mid.ToString(CultureInfo.InvariantCulture)))!=1)
                        throw new InvalidOperationException("Current BRP recipe lacks one exact actual proto module owner.");
                    var owner=Parts.Single(p=>p.flightID==flight);var index=owner.modules.FindIndex(m=>m.moduleValues.GetValue("persistentId")==mid.ToString(CultureInfo.InvariantCulture));
                    RequireRecipe(converter,owner,index,false);
                }
                // FullDemandAccounted also covers converters without a
                // deployment dependency (the feed/fertilizer templates). An
                // absent active recipe cannot silently become zero EC demand.
                foreach(var part in Parts)
                for(int i=0;i<part.partInfo.partPrefab.Modules.Count;i++)
                    if(part.partInfo.partPrefab.Modules[i] is BaseConverter)ConverterOwner(part,i);
                Valid=true;Reason="Current actual proto/native module identities and BRP owner inventory observed.";
            }
            int Index(ProtoPartSnapshot part,PartModule module)
            {
                if(!Valid || part==null || !Parts.Any(p=>ReferenceEquals(p,part)))throw new InvalidOperationException("Actual proto part is outside this observation.");
                var native=part.partInfo.partPrefab.Modules.Cast<PartModule>().ToArray();var matches=Enumerable.Range(0,native.Length).Where(i=>ReferenceEquals(native[i],module)).ToArray();
                if(matches.Length!=1)throw new InvalidOperationException("Configured native module is ambiguous.");return matches[0];
            }
            ConfigNode Config(ProtoPartSnapshot part,int index)=>UtilityNativeConfiguration(part.partInfo,index);
            ConfigNode Saved(ProtoPartSnapshot part,int index)=>part.modules[index].moduleValues;
            object[] Recipes(ProtoPartSnapshot part,int index,bool inactive=false)
            {
                if(inactive && !Saved(part,index).HasValue("persistentId"))return new object[0]; // native BRP assigns IDs only when recording a behaviour
                if(!uint.TryParse(Value(Saved(part,index),"persistentId"),NumberStyles.Integer,CultureInfo.InvariantCulture,out uint id) || id==0)throw new InvalidOperationException("Native recipe owner lacks a nonzero saved module identity.");
                var recipes=Converters.Where(c=>UtilityRead(c,"FlightId") is uint flight && flight==part.flightID && UtilityRead(c,"ModuleId") is uint mid && mid==id).Take(17).ToArray();
                if(recipes.Length>16)throw new InvalidOperationException("Native owner recipe multiplicity exceeds its bound.");
                foreach(var recipe in recipes)RequireRecipe(recipe,part,index,true);
                return recipes;
            }
            static void RequireRecipe(object recipe,ProtoPartSnapshot part,int index,bool constantOwner)
            {
                double rate=UtilityNumber(recipe,"Rate",double.NaN);var behaviour=UtilityRead(recipe,"Behaviour");
                if(!Finite(rate) || rate<0 || rate>1 || behaviour==null || Convert.ToString(UtilityRead(behaviour,"SourceModule"),CultureInfo.InvariantCulture)!=part.modules[index].moduleName)throw new InvalidOperationException("Current BRP recipe provenance/rate changed.");
                var type=behaviour.GetType();string provider=type.Assembly.GetName().Name+"|"+type.Assembly.GetName().Version;
                if((provider!="BackgroundResourceProcessing|0.2.7.0" || constantOwner && type.FullName!="BackgroundResourceProcessing.Behaviour.ConstantConverter") &&
                    (provider!="Expanse.BrpColony|1.0.0.0" || type.FullName!=ProportionalBehaviour))throw new InvalidOperationException("Current BRP behaviour provider identity is unsupported.");
                Ratios(recipe,"Inputs");Ratios(recipe,"Outputs"); // every recipe before the aggregate can sum any EC input
            }
            static object[] Ratios(object recipe,string name)
            {
                var list=UtilityRead(recipe,name) as IEnumerable;if(list==null)throw new InvalidOperationException("Current native recipe "+name+" are absent.");
                var rows=list.Cast<object>().Take(65).Select(e=>UtilityRead(e,"Value")).ToArray();
                if(rows.Length>64 || rows.Any(r=>r==null || string.IsNullOrWhiteSpace(Convert.ToString(UtilityRead(r,"ResourceName"),CultureInfo.InvariantCulture)) ||
                    !Finite(UtilityNumber(r,"Ratio",double.NaN)) || UtilityNumber(r,"Ratio",double.NaN)<0))throw new InvalidOperationException("Current native recipe ratios are invalid or exceed bounds.");return rows;
            }
            ConfigNode Adapter(string name,string adapter,string active,string[] fields)
            {
                var matches=Adapters.Where(n=>n!=null && n.GetValue("name")==name).Take(2).ToArray();
                if(matches.Length!=1)throw new InvalidOperationException("Exact unique background adapter for "+name+" is unavailable.");
                var node=matches[0];
                if(Value(node,"adapter")!=adapter || (active==null ? node.HasValue("ActiveCondition") : Value(node,"ActiveCondition")!=active) || node.GetNodes().Length!=0 ||
                    node.values.Cast<ConfigNode.Value>().Any(v=>!fields.Contains(v.name)) || node.values.Cast<ConfigNode.Value>().GroupBy(v=>v.name).Any(g=>g.Count()!=1))throw new InvalidOperationException("Background adapter terms for "+name+" differ from the reviewed native model.");return node;
            }
            static void NoOwnResources(ConfigNode config,bool swapTerms=false)
            {
                var terms=config.GetNodes("INPUT_RESOURCE").Concat(config.GetNodes("OUTPUT_RESOURCE")).ToArray();
                // USI option ResourceName/Ratio nodes are recipe terms. Native
                // stock resHandler consumes only name-based nodes, not these.
                if(config.GetNodes("RESOURCE").Length!=0 || !swapTerms && terms.Length!=0 || swapTerms && terms.Any(n=>n.HasValue("name") && ConfigText(n,"name","")!=""))throw new InvalidOperationException("Dependency has configured independent resource demand.");
            }
            void Transmitter(ProtoPartSnapshot part,int index)
            {
                var native=part.partInfo.partPrefab.Modules[index];
                if(UtilityModuleIdentity(native)!="ModuleDataTransmitter|Assembly-CSharp|0.0.0.0" || Saved(part,index).GetNodes("CommsData").Length!=0)throw new InvalidOperationException("Actual saved transmission is queued or its native implementation is unknown.");
                Bool(Saved(part,index),"isEnabled"); // actual saved enable state, no prefab busy flag
            }
            bool ConverterOwner(ProtoPartSnapshot part,int index)
            {
                var native=part.partInfo.partPrefab.Modules[index];var saved=Saved(part,index);string identity=UtilityModuleIdentity(native);
                if(identity=="ModuleScienceConverter|Assembly-CSharp|0.0.0.0")
                {
                    var config=Config(part,index);NoOwnResources(config);
                    if(ConfigBool(config,"AlwaysActive",false))throw new InvalidOperationException("Always-active science conversion cannot certify inactive demand.");
                    Bool(saved,"isEnabled"); // Actual saved enable state must be known.
                    if(Bool(saved,"IsActivated") || Recipes(part,index,true).Length!=0)
                        throw new InvalidOperationException("Saved stock science conversion is active or retains a contradictory current BRP recipe; independent demand qualification is required.");
                    // Stock BaseConverter prepares/runs its EC recipe only while
                    // activated. This proves inactive demand, not science output.
                    return true;
                }
                if(identity=="NearFutureElectrical.FissionReactor|NearFutureElectrical|1.0.0.0")
                {
                    if(Bool(saved,"IsActivated") || Number(saved,"AvailablePower")!=0 || Recipes(part,index,true).Length!=0)throw new InvalidOperationException("Legacy NFE saved reactor is active, distributing heat, or retains a native BRP recipe.");
                    // Exact inactive legacy stack/qualified SystemHeat ownership
                    // is checked by the utility observation, not a prefab state.
                    return true;
                }
                if(identity=="WOLF.WOLF_HopperModule|USI_WOLF|1.0.0.0")
                {
                    bool hopperEnabled=Bool(saved,"isEnabled"),hopperActive=Bool(saved,"IsActivated"),connected=Bool(saved,"IsConnectedToDepot");
                    var hopperRecipes=Recipes(part,index,!hopperActive);
                    if(!hopperActive){if(hopperRecipes.Length!=0)throw new InvalidOperationException("Inactive saved WOLF hopper retains a contradictory native recipe.");return true;}
                    if(!hopperEnabled||!connected||Value(saved,"HopperId").Length==0||Value(saved,"DepotBody").Length==0||Value(saved,"DepotBiome").Length==0||hopperRecipes.Length!=1)
                        throw new InvalidOperationException("Active saved WOLF hopper lacks exact connected native owner state/recipe.");
                    var hopperAdapter=Adapter("WOLF_HopperModule","BackgroundResourceConverter",null,new[]{"name","adapter","UsePreparedRecipe"});
                    if(!Bool(hopperAdapter,"UsePreparedRecipe"))throw new InvalidOperationException("WOLF hopper background recipe is not native prepared conversion.");
                    // The native adapter owns physical output/catch-up. Current
                    // paid WOLF allocation/loadout remains checked by production.
                    return true;
                }
                if(identity!="USITools.USI_Converter|USITools|1.0.0.0")throw new InvalidOperationException("Dependency converter owner identity is unsupported.");
                bool enabled=Bool(saved,"isEnabled"),active=Bool(saved,"IsActivated");var recipes=Recipes(part,index,!active);
                if(!active){if(recipes.Length!=0)throw new InvalidOperationException("Inactive actual saved converter retains a contradictory native BRP recipe.");return true;}
                if(!enabled || recipes.Length==0)throw new InvalidOperationException("Active actual saved converter lacks an enabled current native BRP owner recipe.");
                var matches=Adapters.Where(n=>n!=null && n.GetValue("name")=="USI_Converter").Take(2).ToArray();
                if(matches.Length!=1)throw new InvalidOperationException("Exact unique USI background adapter unavailable.");
                string adapter=matches[0].GetValue("adapter");
                if(adapter!="BackgroundResourceConverter" && adapter!="ExpanseColonyProportionalAdapter")throw new InvalidOperationException("USI background adapter identity changed.");
                var node=Adapter("USI_Converter",adapter,null,new[]{"name","adapter","UsePreparedRecipe"});
                if(!Bool(node,"UsePreparedRecipe") || adapter=="ExpanseColonyProportionalAdapter" && ProportionalType("Expanse.BrpColony.ExpanseColonyProportionalAdapter")==null)throw new InvalidOperationException("Prepared native USI background ownership is unsupported.");
                uint id=uint.Parse(Value(saved,"persistentId"),CultureInfo.InvariantCulture);
                foreach(var recipe in recipes)RequireProportionalObservation(recipe,Vessel,part,id,Ut,Processor);
                return true;
            }
            void Owner(ProtoPartSnapshot part,int index,ReactorUtilityWitness reactors)
            {
                var native=part.partInfo.partPrefab.Modules[index];
                if(native is BaseConverter){ConverterOwner(part,index);return;}
                string type=native.GetType().FullName;
                if(type=="SystemHeat.ModuleSystemHeatFissionReactor")
                {
                    if(!reactors.Qualified || !reactors.Accounted.Contains(native) || Recipes(part,index).Length!=1)throw new InvalidOperationException("Exact current BRP reactor owner lacks qualified unchanged thermal continuation.");return;
                }
                throw new InvalidOperationException("Dependency native animated owner is unsupported.");
            }
            void Witness(ProtoPartSnapshot part,int index,string family)
            {
                if(witnesses.Count>=128)throw new InvalidOperationException("Saved-state dependency witness bound exceeded.");
                witnesses.Add(part.flightID+":"+index+":"+family+":"+HashReactorText(part.partInfo.partConfig.ToString())+":"+HashReactorText(Saved(part,index).ToString()));
                Evidence="Modeled actual saved-state dependencies="+witnesses.Count+"; current proto/configuration witness="+HashReactorText(string.Join("|",witnesses))+". Zero independent dependency EC only; active native BRP owners are accounted separately. No current loaded terminal animation, crew capacity or temperature is inferred.";
            }
            void RequireSelectedSwapRecipe(ProtoPartSnapshot part,int owner,int option)
            {
                if(!Bool(Saved(part,owner),"IsActivated"))return;
                var expected=new SortedDictionary<string,double>(StringComparer.Ordinal);
                foreach(string direction in new[]{"INPUT_RESOURCE","OUTPUT_RESOURCE"})
                foreach(var row in Config(part,option).GetNodes(direction))
                {
                    string name=Value(row,"ResourceName"),flow=ConfigText(row,"FlowMode","NULL");double ratio=Number(row,"Ratio");
                    string key=direction+":"+name+":"+flow;
                    if(string.IsNullOrWhiteSpace(name) || ratio<1e-20 || ratio>1e9 || expected.ContainsKey(key))throw new InvalidOperationException("Selected native swap recipe is zero, duplicated or outside reviewed terms.");
                    expected.Add(key,ratio);
                }
                if(expected.Count==0 || expected.Count>128)throw new InvalidOperationException("Selected native swap recipe terms are unavailable.");
                var recipes=Recipes(part,owner);if(recipes.Length!=1)throw new InvalidOperationException("Selected swap mode requires one current native prepared recipe.");
                var actual=new SortedDictionary<string,double>(StringComparer.Ordinal);
                foreach(string direction in new[]{"Inputs","Outputs"})
                foreach(var row in Ratios(recipes[0],direction))
                {
                    string name=Convert.ToString(UtilityRead(row,"ResourceName"),CultureInfo.InvariantCulture),flow=Convert.ToString(UtilityRead(row,"FlowMode"),CultureInfo.InvariantCulture);
                    string key=(direction=="Inputs" ? "INPUT_RESOURCE:" : "OUTPUT_RESOURCE:")+name+":"+flow;double ratio=UtilityNumber(row,"Ratio",double.NaN);
                    if(ratio<1e-20 || ratio>1e9 || actual.ContainsKey(key))throw new InvalidOperationException("Current selected native swap recipe terms are ambiguous.");actual.Add(key,ratio);
                }
                if(!expected.Keys.SequenceEqual(actual.Keys))throw new InvalidOperationException("Current native recipe resource/direction/flow terms contradict the actual saved loadout.");
                // Reviewed BRP prepared native recipe multiplies all inputs and
                // outputs by one positive factor. Bound roundoff from division
                // and the two multiplications; no quantity or EC is clamped.
                double factor=actual.First().Value/expected.First().Value;
                if(!Finite(factor) || factor<=0 || factor>1e9)throw new InvalidOperationException("Current native swap recipe scale is invalid.");
                foreach(var row in expected)
                {
                    double scaled=row.Value*factor,value=actual[row.Key];
                    if(!Finite(scaled) || Math.Abs(value-scaled)>8*2.2204460492503131e-16*Math.Max(value,scaled))throw new InvalidOperationException("Current native recipe ratios contradict the actual saved loadout.");
                }
            }
            internal bool TryDependency(ProtoPartSnapshot part,PartModule module,ReactorUtilityWitness reactors,out string reason)
            {
                reason=Reason;
                try
                {
                    int index=Index(part,module);var config=Config(part,index);var saved=Saved(part,index);var native=part.partInfo.partPrefab.Modules.Cast<PartModule>().ToArray();
                    string identity=UtilityModuleIdentity(module);
                    NoOwnResources(config,identity=="USITools.USI_EfficiencyBoosterSwapOption|USITools|1.0.0.0");if(!Bool(saved,"isEnabled"))throw new InvalidOperationException("Actual saved dependency is not enabled.");
                    if(identity=="USITools.USIAnimation|USITools|1.0.0.0")
                    {
                        bool deployed=Bool(saved,"isDeployed");if(Number(saved,"partialDeployCostPaid")!=0)throw new InvalidOperationException("Actual saved animation has incomplete deployment costs.");
                        var owners=Enumerable.Range(0,native.Length).Where(i=>native[i] is IAnimatedModule || native[i] is BaseConverter || native[i].GetType().FullName=="SystemHeat.ModuleSystemHeatFissionReactor").ToArray();
                        if(owners.Length==0)
                        {
                            if(ConfigText(config,"ResourceCosts","")!="" || ConfigText(config,"secondaryAnimationName","")!="" || ConfigNumber(config,"inflatedMultiplier",-1)!=-1 || ConfigBool(config,"shedOnInflate",false) || native.Any(m=>m is ModuleControlSurface))throw new InvalidOperationException("Ownerless saved animation is not the reviewed no-cost passive configuration.");
                        }
                        else
                        {
                            if(!deployed)throw new InvalidOperationException("Actual saved ownerful animation is not deployed.");
                            foreach(int owner in owners)Owner(part,owner,reactors);
                        }
                    }
                    else if(identity=="ModuleDeployableRadiator|Assembly-CSharp|0.0.0.0")
                    {
                        if(Value(saved,"deployState")!="EXTENDED" || !reactors.Qualified)throw new InvalidOperationException("Actual saved radiator deployment or thermal continuation is unqualified.");
                        var owners=Enumerable.Range(0,native.Length).Where(i=>native[i].GetType().FullName=="SystemHeat.ModuleSystemHeatRadiator").ToArray();
                        if(owners.Length!=1 || !reactors.Accounted.Contains(native[owners[0]]) || !Bool(Saved(part,owners[0]),"IsCooling"))throw new InvalidOperationException("Actual saved deployable radiator lacks its cooling owner.");
                        Adapter("ModuleSystemHeatRadiator","BackgroundGenericConverter","%IsCooling",new[]{"name","adapter","ActiveCondition"});
                        if(Recipes(part,owners[0]).Length!=1)throw new InvalidOperationException("Actual cooling radiator lacks one current native BRP owner recipe.");
                    }
                    else if(identity=="CommNetAntennasConsumptor.ModuleAntennaToggler|CommNetAntennasConsumptor|3.5.8.0")
                    {
                        Bool(saved,"AntennaEnabled");var generators=Enumerable.Range(0,native.Length).Where(i=>UtilityModuleIdentity(native[i])==ColonyUtilityModuleBounds.AntennaIdentity).ToArray();
                        var transmitters=Enumerable.Range(0,native.Length).Where(i=>native[i] is ModuleDataTransmitter).ToArray();
                        if(generators.Length!=1 || transmitters.Length!=1)throw new InvalidOperationException("Saved antenna dependency has ambiguous native owners.");
                        Transmitter(part,transmitters[0]);AntennaDemand(part,generators[0]);
                    }
                    else if(identity=="USITools.USI_EfficiencyBoosterSwapOption|USITools|1.0.0.0")
                    {
                        var controllers=Enumerable.Range(0,native.Length).Where(i=>native[i] is USITools.USI_SwapController).ToArray();
                        var options=Enumerable.Range(0,native.Length).Where(i=>native[i] is USITools.AbstractSwapOption).ToArray();
                        var bays=Enumerable.Range(0,native.Length).Where(i=>native[i] is USITools.USI_SwappableBay).ToArray();
                        var owners=Enumerable.Range(0,native.Length).Where(i=>native[i] is USITools.ISwappableConverter && !ConfigBool(Config(part,i),"IsStandaloneConverter",false)).ToArray();
                        if(controllers.Length!=1 || UtilityModuleIdentity(native[controllers[0]])!="USITools.USI_SwapController|USITools|1.0.0.0" || options.Length==0 || options.Length>128 || bays.Length==0 || bays.Length>16 || owners.Length!=bays.Length || options.Count(i=>i==index)!=1)throw new InvalidOperationException("Saved native swap topology is ambiguous.");
                        var seen=new HashSet<int>();
                        foreach(int bay in bays)
                        {
                            if(UtilityModuleIdentity(native[bay])!="USITools.USI_SwappableBay|USITools|1.0.0.0")throw new InvalidOperationException("Saved swap bay implementation changed.");
                            int owner=Config(part,bay).HasValue("moduleIndex") ? Integer(Config(part,bay),"moduleIndex") : 0,loadout=Integer(Saved(part,bay),"currentLoadout");
                            if(owner<0 || owner>=owners.Length || !seen.Add(owner) || loadout<0 || loadout>=options.Length)throw new InvalidOperationException("Actual saved native bay/loadout identity is invalid.");
                            if(UtilityModuleIdentity(native[options[loadout]])!="USITools.USI_ConverterSwapOption|USITools|1.0.0.0" && UtilityModuleIdentity(native[options[loadout]])!="USITools.USI_EfficiencyBoosterSwapOption|USITools|1.0.0.0")throw new InvalidOperationException("Actual selected swap option identity is unsupported.");
                            ConverterOwner(part,owners[owner]);RequireSelectedSwapRecipe(part,owners[owner],options[loadout]);
                        }
                    }
                    else throw new InvalidOperationException("Saved dependency implementation is unsupported.");
                    Witness(part,index,module.moduleName);reason="Modeled current saved-state dependency has zero independent EC; exact native owners accounted separately.";return true;
                }
                catch(Exception ex){reason="Unloaded dependency hold: "+Bound(ex.Message,384);return false;}
            }
            double AntennaDemand(ProtoPartSnapshot part,int index)
            {
                var native=part.partInfo.partPrefab.Modules[index];var config=Config(part,index);var saved=Saved(part,index);
                if(UtilityModuleIdentity(native)!=ColonyUtilityModuleBounds.AntennaIdentity || ConfigBool(config,"isThrottleControlled",false) || config.GetNodes("OUTPUT_RESOURCE").Length!=0 || config.GetNodes("RESOURCE").Length!=0)throw new InvalidOperationException("Configured antenna generator implementation or handler is unsupported.");
                Adapter("ModuleGeneratorAntenna","BackgroundGenericConverter","!%isThrottleControlled && (%isAlwaysActive || %generatorIsActive)",new[]{"name","adapter","ActiveCondition"});
                bool enabled=Bool(saved,"isEnabled"),active=Bool(saved,"generatorIsActive") || ConfigBool(config,"isAlwaysActive",false);
                var inputs=config.GetNodes("INPUT_RESOURCE");if(inputs.Length==0 || inputs.Length>64)throw new InvalidOperationException("Configured antenna demand exceeds its bound.");
                var definition=PartResourceLibrary.Instance==null ? null : PartResourceLibrary.Instance.GetDefinition("ElectricCharge");
                if(definition==null || !VesselWideAntennaFlow(definition.resourceFlowMode))throw new InvalidOperationException("Current ElectricCharge definition has no reviewed vessel-wide feed.");
                double bound=0,nativeBound=0;var flows=new List<ResourceFlowMode>();
                foreach(var input in inputs)
                {
                    double rate=Number(input,"rate");if(Value(input,"name")!="ElectricCharge" || rate<0 || rate>1e6)throw new InvalidOperationException("Configured antenna demand is invalid.");
                    ResourceFlowMode flow=definition.resourceFlowMode;
                    if(input.HasValue("resourceFlowMode") && (!Enum.TryParse(Value(input,"resourceFlowMode"),out flow) || !Enum.IsDefined(typeof(ResourceFlowMode),flow)))throw new InvalidOperationException("Configured antenna resource flow is invalid.");
                    if(!VesselWideAntennaFlow(flow))throw new InvalidOperationException("Configured antenna input lacks a reviewed vessel-wide feed.");flows.Add(flow);bound+=rate;
                    var decoded=new ResourceRatio("ElectricCharge",rate,false,flow);var savedRatio=new ConfigNode("INPUT_RESOURCE");
                    decoded.Save(savedRatio);decoded.Load(savedRatio);nativeBound+=decoded.Ratio;
                }
                if(!Finite(bound) || bound>1e6 || !Finite(nativeBound) || nativeBound>1e6)throw new InvalidOperationException("Configured antenna upper bound is invalid.");
                var recipes=Recipes(part,index,!active);
                if(!active){if(recipes.Length!=0)throw new InvalidOperationException("Inactive actual saved antenna retains a contradictory BRP recipe.");return Math.Max(bound,nativeBound);}
                if(!enabled || recipes.Length!=1 || Ratios(recipes[0],"Outputs").Length!=0)throw new InvalidOperationException("Active actual saved antenna lacks one supported native EC-only recipe.");
                var actual=Ratios(recipes[0],"Inputs");double actualBound=actual.Sum(r=>UtilityNumber(r,"Ratio",double.NaN));
                // Stock ResourceRatio.Load parses saved Ratio as float before
                // widening to double. Accept only that exact native projection
                // or the original loaded rate, with the larger demand bound.
                if(actual.Length!=inputs.Length || flows.Distinct().Count()!=1 || actual.Any(r=>Convert.ToString(UtilityRead(r,"ResourceName"),CultureInfo.InvariantCulture)!="ElectricCharge" ||
                    Convert.ToString(UtilityRead(r,"FlowMode"),CultureInfo.InvariantCulture)!=flows[0].ToString()) || actualBound!=bound && actualBound!=nativeBound)throw new InvalidOperationException("Actual native antenna EC/flow recipe differs from its configured conservative bound.");
                return Math.Max(bound,nativeBound); // unthrottled upper bound, never credit a smaller observed Rate
            }
            static bool VesselWideAntennaFlow(ResourceFlowMode flow)=>flow==ResourceFlowMode.ALL_VESSEL || flow==ResourceFlowMode.ALL_VESSEL_BALANCE ||
                flow==ResourceFlowMode.STAGE_PRIORITY_FLOW || flow==ResourceFlowMode.STAGE_PRIORITY_FLOW_BALANCE;
            internal bool TryAntennaDemand(ProtoPartSnapshot part,PartModule module,out double demand)
            {
                demand=0;try{demand=AntennaDemand(part,Index(part,module));return true;}catch{return false;}
            }
        }
        static UnloadedUtilityDependencies ObserveUnloadedUtilityDependencies(Vessel vessel,double ut,object processor,ConfigNode[] adapters)
        {
            var result=new UnloadedUtilityDependencies {Vessel=vessel,Ut=ut,Processor=processor,Adapters=adapters??new ConfigNode[0]};
            try{result.Validate();}catch(Exception ex){result.Valid=false;result.Reason="Unloaded utility owner hold: "+Bound(ex.Message,512);}return result;
        }
    }
}
