using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using USITools;
using USITools.KolonyTools;
using WOLF;

namespace Expanse.WorldBridge
{
    internal sealed class ColonyBrokerSample
    {
        public ProductionTelemetryLedger.Frame Frame;public BaseConverter Module;public ColonyProductionPotential Prepared;public double TimeFactor;public string Status;
        public Vessel Vessel;public ProductionTelemetryMode Mode;public long ModeEpoch;public bool Packed;
        public LifeSupportNativeSample LifeSupport;
        public double? ProcessedEndUt;
    }
    public sealed partial class WorldBridgeAddon
    {
        readonly ProductionTelemetryLedger productionLedger=new ProductionTelemetryLedger();
        readonly Dictionary<BaseConverter,ColonyBrokerSample> productionSamples=new Dictionary<BaseConverter,ColonyBrokerSample>();
        readonly Dictionary<Vessel,ProductionTelemetryMode> productionModes=new Dictionary<Vessel,ProductionTelemetryMode>();
        string productionContext;double productionUt=double.NaN;int productionThread;
        void InstallProductionModeObserver()
        { GameEvents.onVesselGoOnRails.Add(OnProductionGoOnRails);GameEvents.onVesselGoOffRails.Add(OnProductionGoOffRails); }
        void RemoveProductionModeObserver()
        { GameEvents.onVesselGoOnRails.Remove(OnProductionGoOnRails);GameEvents.onVesselGoOffRails.Remove(OnProductionGoOffRails);productionModes.Clear();productionSamples.Clear();ResetLifeSupportTelemetry();ColonyRuntime.StopLifeSupportObserver(); }
        void OnProductionGoOnRails(Vessel vessel){ObserveProductionTransition(vessel,true);}
        void OnProductionGoOffRails(Vessel vessel){ObserveProductionTransition(vessel,false);}
        void ObserveProductionTransition(Vessel vessel,bool targetPacked)
        {
            try {if(vessel==null)return;GetProductionMode(vessel)?.Transition(targetPacked);lifeSupportSamples.Remove(vessel);powerAverage.Invalidate(vessel.id.ToString("D"));}
            catch {productionSamples.Clear();productionModes.Clear();}
        }
        ProductionTelemetryMode GetProductionMode(Vessel vessel)
        {
            if(vessel==null)return null;
            if(productionModes.TryGetValue(vessel,out var mode))return mode;
            if(productionModes.Count>=512)return null;
            mode=new ProductionTelemetryMode();productionModes.Add(vessel,mode);return mode;
        }
        bool ProductionModeMatches(ColonyBrokerSample sample)
        {
            return sample!=null&&sample.Module!=null&&sample.Vessel!=null&&sample.Module.vessel==sample.Vessel&&sample.Vessel.loaded&&
                productionModes.TryGetValue(sample.Vessel,out var mode)&&ReferenceEquals(mode,sample.Mode)&&
                mode.Matches(sample.ModeEpoch,sample.Packed,sample.Vessel.packed);
        }
        string ProductionContext=>sessionId+"/"+loadEpoch+"/"+HighLogic.SaveFolder;
        void ResetProductionTelemetry()
        {productionSamples.Clear();productionModes.Clear();productionLedger.Reset(ProductionContext);productionContext=ProductionContext;productionUt=double.NaN;productionThread=System.Threading.Thread.CurrentThread.ManagedThreadId;ResetLifeSupportTelemetry();}
        void EnsureProductionTelemetry(double ut)
        {
            if(productionContext!=ProductionContext||IsFinite(productionUt)&&ut<productionUt)ResetProductionTelemetry();
            productionUt=ut;ColonyRuntime.EnsureProductionBrokerHook();
            ColonyRuntime.EnsureLifeSupportObserver();powerAverage.Tick(ProductionContext,RealSeconds,ut);
            foreach(var vessel in lifeSupportSamples.Keys.Where(v=>v==null||!v.loaded).ToArray())lifeSupportSamples.Remove(vessel);
            foreach(var vessel in averageModes.Keys.Where(v=>v==null||!v.loaded).ToArray())averageModes.Remove(vessel);
            foreach(var module in productionSamples.Keys.Where(m=>m==null||m.vessel==null||!m.vessel.loaded||ut<productionSamples[m].Frame.Ut||ut-productionSamples[m].Frame.Ut>10).ToArray())productionSamples.Remove(module);
            foreach(var vessel in productionModes.Keys.Where(v=>v==null||!v.loaded).ToArray()){if(vessel!=null)powerAverage.Invalidate(vessel.id.ToString("D"));productionModes.Remove(vessel);}
        }
        internal static ColonyBrokerSample BeginProductionTelemetry(ResourceConverter converter,double seconds,ConversionRecipe recipe,Part part,PartModule module,float efficiency)
        {
            var observer=Current;if(observer==null||observer.productionContext==null||System.Threading.Thread.CurrentThread.ManagedThreadId!=observer.productionThread)return null;
            ColonyBrokerSample sample=null;
            try
            {
                double ut=Planetarium.GetUniversalTime();
                if(observer.productionContext!=observer.ProductionContext||IsFinite(observer.productionUt)&&ut<observer.productionUt)observer.ResetProductionTelemetry();
                observer.productionUt=ut;
                var native=module as BaseConverter;object broker=ReadProductionMember(converter,"_broker");
                bool eligible=HighLogic.LoadedSceneIsFlight&&!observer.loadUnresolved&&native!=null&&native.part==part&&native.IsActivated&&native.isEnabled&&native.vessel!=null&&native.vessel.loaded&&SupportedProductionModule(native)&&broker?.GetType()==typeof(ResourceBroker)&&recipe!=null&&recipe.Inputs!=null&&recipe.Outputs!=null&&recipe.Requirements!=null&&IsFinite(efficiency)&&efficiency>=0&&efficiency<=10000;
                var life=HighLogic.LoadedSceneIsFlight&&!observer.loadUnresolved?observer.QualifyLifeSupport(converter,seconds,recipe,part,module,efficiency,ut):null;
                eligible|=life!=null;
                sample=new ColonyBrokerSample {Module=native,LifeSupport=life};
                if(eligible)
                {
                    sample.Vessel=life==null?native.vessel:LifeSupportVessel(life.Owner);sample.Packed=sample.Vessel.packed;sample.Mode=observer.GetProductionMode(sample.Vessel);
                    eligible=sample.Mode!=null&&sample.Mode.TryCapture(sample.Packed,out sample.ModeEpoch);
                }
                sample.Frame=observer.productionLedger.Enter(observer.ProductionContext,broker,part,"",ut,seconds,eligible);
                if(eligible)sample.Frame.RecipeKey=life==null?LoadedProductionKey(native):life.Key;
                if(native!=null)observer.productionSamples.Remove(native);
                if(!eligible)return sample;
                double requirement=1;
                foreach(var row in recipe.Requirements)
                {
                    if(!IsFinite(row.Ratio)||row.Ratio==0||part.Resources[row.ResourceName]==null)throw new InvalidOperationException("Unresolved requirement.");
                    double fraction=part.Resources[row.ResourceName].amount/Math.Abs(row.Ratio);if(row.Ratio<0)fraction=1-fraction;
                    requirement=Math.Min(requirement,fraction);
                }
                if(!IsFinite(requirement)||requirement<0||requirement>1)throw new InvalidOperationException("Invalid requirement factor.");
                sample.Prepared=new ColonyProductionPotential {SampleUt=ut,EfficiencyMultiplier=efficiency,RequirementMultiplier=requirement,
                    Rates=ProductionVector(recipe.Inputs,recipe.Outputs,recipe.Requirements,efficiency*requirement)};
                if(life==null)sample.ProcessedEndUt=NativeProducerProcessedEnd(native,ut,seconds);
                return sample;
            }
            catch
            {
                if(sample==null)sample=new ColonyBrokerSample();
                if(sample.Frame==null)sample.Frame=observer.productionLedger.Enter(observer.ProductionContext,null,null,"",observer.productionUt,seconds,false);
                sample.Frame.Valid=false;return sample;
            }
        }
        internal static void RecordProductionTelemetry(IResourceBroker broker,Part part,string resource,double requested,double returned,bool output)
        {try {var observer=Current;if(observer!=null&&System.Threading.Thread.CurrentThread.ManagedThreadId==observer.productionThread)observer.productionLedger.Record(broker,part,resource,requested,returned,output);}catch {}}
        internal static void CompleteProductionTelemetry(ColonyBrokerSample sample,bool returned,ConverterResults result)
        {
            try
            {
                var observer=Current;if(observer==null||sample==null)return;
                bool eligible=false;
                try {eligible=sample.Frame!=null&&!observer.loadUnresolved&&observer.productionContext==observer.ProductionContext&&
                    (sample.LifeSupport!=null?observer.LifeSupportModeMatches(sample):observer.ProductionModeMatches(sample)&&sample.Module.isEnabled&&sample.Module.IsActivated&&sample.Frame.RecipeKey==LoadedProductionKey(sample.Module));}
                catch { }
                if(sample.Frame!=null&&!eligible)sample.Frame.Valid=false;
                if(observer.productionLedger.Complete(sample.Frame,returned,result.TimeFactor)&&sample.Prepared!=null&&observer.productionContext==sample.Frame.Context)
                {
                    if(sample.LifeSupport!=null&&(result.TimeFactor>sample.Frame.Seconds+1e-6||!LifeSupportAmountsMatch(sample))){sample.Frame.Valid=false;return;}
                    sample.TimeFactor=result.TimeFactor;sample.Status=Bound(result.Status??"",256);
                    if(sample.LifeSupport!=null){observer.StoreLifeSupportSample(sample);return;}
                    if(sample.Module==null)return;
                    if(observer.productionSamples.Count>=512&&!observer.productionSamples.ContainsKey(sample.Module))return;
                    observer.productionSamples[sample.Module]=sample;
                    if(sample.Packed&&sample.ProcessedEndUt.HasValue)observer.RecordAverage(sample,sample.ProcessedEndUt.Value,"supported-native-callbacks",true);
                }
            }
            catch { }
        }
        static bool SupportedProductionModule(BaseConverter module)
        {
            if(module.GetType()==typeof(USI_Harvester)||module.GetType()==typeof(ModuleResourceHarvester))return true;
            var converter=module as ModuleResourceConverter;
            return converter!=null&&!converter.ConvertByMass&&(module.GetType()==typeof(ModuleResourceConverter)||module.GetType()==typeof(USI_Converter)||module.GetType()==typeof(WOLF_HopperModule));
        }
        static object ReadProductionMember(object owner,string name)
        {
            if(owner==null)return null;
            for(var type=owner.GetType();type!=null;type=type.BaseType){var field=type.GetField(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly);if(field!=null)return field.GetValue(owner);var property=type.GetProperty(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly);if(property!=null&&property.GetIndexParameters().Length==0)return property.GetValue(owner,null);}return null;
        }
        static ColonyProductionRateTelemetry[] ProductionRates(IEnumerable<ResourceRatio> rows,double multiplier)
        {
            var values=rows.ToArray();if(values.Length>16||!IsFinite(multiplier)||multiplier<0||values.Any(r=>string.IsNullOrWhiteSpace(r.ResourceName)||r.ResourceName.Length>80||!IsFinite(r.Ratio)||r.Ratio<0||r.Ratio*multiplier>1e12))throw new InvalidOperationException("Unsupported recipe vector.");
            return values.Select(r=>new ColonyProductionRateTelemetry{Resource=r.ResourceName,UnitsPerSecond=r.Ratio*multiplier,FlowMode=r.FlowMode.ToString(),DumpExcess=r.DumpExcess}).ToArray();
        }
        static ColonyProductionVector ProductionVector(IEnumerable<ResourceRatio> inputs,IEnumerable<ResourceRatio> outputs,IEnumerable<ResourceRatio> requirements,double multiplier=1)
        {
            var required=requirements.ToArray();if(required.Length>16||required.Any(r=>string.IsNullOrWhiteSpace(r.ResourceName)||r.ResourceName.Length>80||!IsFinite(r.Ratio)||Math.Abs(r.Ratio)>1e12))throw new InvalidOperationException("Unsupported requirements.");
            return new ColonyProductionVector{Inputs=ProductionRates(inputs,multiplier),Outputs=ProductionRates(outputs,multiplier),Requirements=required.Select(r=>new ColonyProductionRequirementTelemetry{Resource=r.ResourceName,Amount=r.Ratio}).ToArray()};
        }
        static string ProductionHash(string value){using(var sha=SHA256.Create())return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(value)).Select(b=>b.ToString("x2",CultureInfo.InvariantCulture)));}
        static string ProductionVectorKey(ColonyProductionVector vector)=>"inputs:"+ProductionRateKey(vector.Inputs)+"|outputs:"+ProductionRateKey(vector.Outputs)+"|requirements:"+string.Join("|",vector.Requirements.Select(r=>r.Resource+":"+r.Amount.ToString("R",CultureInfo.InvariantCulture)));
        static string ProductionRateKey(ColonyProductionRateTelemetry[] rows)=>string.Join("|",rows.Select(r=>r.Resource+":"+r.UnitsPerSecond.ToString("R",CultureInfo.InvariantCulture)+":"+r.FlowMode+":"+r.DumpExcess));
        static string LoadedProductionKey(BaseConverter module)
        {
            var vector=ProductionVector(module.inputList,module.outputList,module.reqList);
            var harvest=module as ModuleResourceHarvester;
            return ProductionHash(module.part.vessel.id+"|"+module.part.persistentId+"|"+module.PersistentId+"|"+module.part.Modules.IndexOf(module)+"|"+module.ConverterName+"|"+ProductionVectorKey(vector)+"|"+USI_DifficultyOptions.ConsumeMachineryEnabled+"|"+(harvest==null?"":harvest.ResourceName+"|"+harvest.Efficiency.ToString("R",CultureInfo.InvariantCulture)+"|"+harvest.HarvestThreshold.ToString("R",CultureInfo.InvariantCulture)+"|"+harvest.HarvesterType)+"|"+ProductionBaySelection(module));
        }
        static int ProductionBayOrdinal(BaseConverter module)=>module.part.Modules.Cast<PartModule>().Where(m=>m is ISwappableConverter c&&!c.IsStandalone).TakeWhile(m=>m!=module).Count();
        static PartModule ProductionBay(BaseConverter module)=>!(module is ISwappableConverter swappable)||swappable.IsStandalone?null:module.part.Modules.Cast<PartModule>().FirstOrDefault(m=>(m.moduleName=="USI_SwappableBay"||m.moduleName=="WOLF_HopperBay")&&Convert.ToInt32(ReadProductionMember(m,"moduleIndex")??-1)==ProductionBayOrdinal(module));
        static string ProductionBaySelection(BaseConverter module)=>Convert.ToString(ReadProductionMember(ProductionBay(module),"currentLoadout"),CultureInfo.InvariantCulture);
        ColonyProductionTelemetry ObserveProductionTelemetry(Vessel vessel,double ut)
        {
            var result=new ColonyProductionTelemetry {ObservedUt=ut};EnsureProductionTelemetry(ut);
            try
            {
                if(vessel.loaded&&vessel.parts!=null)
                {
                    foreach(var part in vessel.parts.Where(p=>p!=null))
                    foreach(var module in part.Modules.Cast<PartModule>().OfType<BaseConverter>())
                    {
                        if(result.Modules.Count>=32){result.Status="truncated";result.Reason="More than 32 producing/consuming modules; totals are incomplete.";return result;}
                        var row=LoadedProductionRow(module);result.Modules.Add(row);
                        if(!string.IsNullOrEmpty(ColonyRuntime.ProductionBrokerHookFailure))row.Reason=Bound(ColonyRuntime.ProductionBrokerHookFailure,360);
                        if(!SupportedProductionModule(module)){row.Reason="Unsupported converter implementation or mass-based configured recipe; no achieved rate claimed.";continue;}
                        if(!string.IsNullOrEmpty(ColonyRuntime.ProductionBrokerHookFailure))continue;
                        ColonyBrokerSample sample;
                        if(!productionSamples.TryGetValue(module,out sample)||!ProductionModeMatches(sample)||!ProductionTelemetryLedger.Fresh(sample.Frame,ProductionContext,LoadedProductionKey(module),ut,module.IsActivated&&module.isEnabled))
                        {
                            if(vessel.packed)row.Reason="Packed; delivered rate unobserved. A current qualified native callback is required.";
                            continue;
                        }
                        row.Prepared=CopyProductionPotential(sample.Prepared);row.Achieved=new ColonyProductionAchieved {SampleUt=sample.Frame.Ut,IntervalGameSeconds=sample.Frame.Seconds,TimeFactor=sample.TimeFactor,CaptureSequence=sample.Frame.CaptureSequence,
                            Inputs=DeliveredProductionRates(sample.Prepared.Rates.Inputs,sample.Frame.Inputs,sample.Frame.Seconds),Outputs=DeliveredProductionRates(sample.Prepared.Rates.Outputs,sample.Frame.Outputs,sample.Frame.Seconds)};
                        row.NativeStatus=sample.Status;row.Reason="Latest completed native callback; accepted broker quantities per callback game second. Prepared rates are potential before input/storage limits.";
                    }
                    if(result.Modules.All(r=>r.Reason!="Unsupported converter implementation or mass-based configured recipe; no achieved rate claimed."))result.InventoryStatus="complete-supported";
                }
                else if(vessel.protoVessel!=null)
                {
                    foreach(var part in vessel.protoVessel.protoPartSnapshots.Where(p=>p!=null&&p.partInfo?.partConfig!=null))
                    for(int index=0;index<part.modules.Count;index++)
                    {
                        var saved=part.modules[index];var nodes=part.partInfo.partConfig.GetNodes("MODULE");if(index>=nodes.Length)continue;
                        string name=saved.moduleName;
                        if(name!="USI_Converter"&&name!="WOLF_HopperModule"&&name!="ModuleResourceConverter"&&name!="USI_Harvester"&&name!="ModuleResourceHarvester")continue;
                        if(nodes[index].GetValue("name")!=name){result.Reason="Saved/config module mapping differs; some converter recipes were withheld.";continue;}
                        if(result.Modules.Count>=32){result.Status="truncated";result.Reason="More than 32 saved converters; totals are incomplete.";return result;}
                        var row=ProtoProductionRow(part,saved,index,nodes);result.Modules.Add(row);PopulateBackgroundProduction(vessel,part,row,ut);
                    }
                }
                else {result.Status="unavailable";result.Reason="Neither loaded modules nor saved part snapshots are available.";}
            }
            catch(Exception ex){result.Status=result.Modules.Count==0?"unavailable":"partial";result.Reason="Production observation incomplete: "+Bound(ex.Message,300);}
            return result;
        }
        static ColonyProductionRateTelemetry[] DeliveredProductionRates(ColonyProductionRateTelemetry[] recipe,Dictionary<string,double> amounts,double seconds)
            =>recipe.GroupBy(r=>r.Resource,StringComparer.Ordinal).Select(g=>new ColonyProductionRateTelemetry{Resource=g.Key,FlowMode=g.First().FlowMode,DumpExcess=g.Any(r=>r.DumpExcess),UnitsPerSecond=(amounts.TryGetValue(g.Key,out double amount)?amount:0)/seconds}).ToArray();
        static ColonyProductionModuleTelemetry LoadedProductionRow(BaseConverter module)
        {
            var part=module.part;var row=new ColonyProductionModuleTelemetry {PartId=part.persistentId,ModuleId=module.PersistentId==0?(uint?)null:module.PersistentId,ModuleIndex=part.Modules.IndexOf(module),ModuleType=Bound(module.GetType().FullName,128),PartName=Bound(part.partInfo?.title??part.name,100),Recipe=Bound(module.ConverterName??module.moduleName,100),Enabled=module.isEnabled,Activated=module.IsActivated,Basis="loaded-broker",Reason="Awaiting a current completed native callback; activation alone is not delivered output.",Configured=new ColonyProductionVector()};
            if(SupportedProductionModule(module))row.Configured=ProductionVector(module.inputList,module.outputList,module.reqList);
            int converterIndex=ProductionBayOrdinal(module);
            var bay=ProductionBay(module);
            if(bay!=null){row.BayIndex=converterIndex;row.SelectedLoadout=Convert.ToInt32(ReadProductionMember(bay,"currentLoadout"));}
            if(module is ModuleResourceHarvester harvest)
            {
                row.Harvester=new ColonyProductionHarvester{Resource=Bound(harvest.ResourceName,80),Efficiency=harvest.Efficiency,HarvestThreshold=harvest.HarvestThreshold,HarvesterType=harvest.HarvesterType};
                row.Configured.Outputs=new ColonyProductionRateTelemetry[0];
                row.Reason="Harvester configured output is abundance-dependent; efficiency is a coefficient, not units/second. Awaiting a native callback.";
            }
            row.RecipeHash=LoadedProductionKey(module);
            if(module is WOLF_HopperModule hopper)row.Hopper=new ColonyProductionHopper{HopperId=Bound(hopper.HopperId,80),Connected=hopper.IsConnectedToDepot,Body=Bound(Convert.ToString(ReadProductionMember(hopper,"DepotBody")),100),Biome=Bound(Convert.ToString(ReadProductionMember(hopper,"DepotBiome")),100),AllocationPoints=ProductionPoints(hopper.InputResources)};
            return row;
        }
        static bool? ProductionBool(ConfigNode values,string name)=>bool.TryParse(values.GetValue(name),out bool parsed)?(bool?)parsed:null;
        static ColonyProductionWolfPoints[] ProductionPoints(string value)
        {
            var pieces=(value??"").Split(',');if(pieces.Length==1&&pieces[0].Length==0)return new ColonyProductionWolfPoints[0];if(pieces.Length>32||pieces.Length%2!=0)throw new InvalidOperationException("Unresolved WOLF allocation.");
            var rows=new List<ColonyProductionWolfPoints>();for(int i=0;i<pieces.Length;i+=2){if(!int.TryParse(pieces[i+1],out int points)||points<0||string.IsNullOrWhiteSpace(pieces[i])||pieces[i].Trim().Length>80)throw new InvalidOperationException("Invalid WOLF points.");rows.Add(new ColonyProductionWolfPoints{Resource=pieces[i].Trim(),Points=points});}return rows.ToArray();
        }
        static ColonyProductionModuleTelemetry ProtoProductionRow(ProtoPartSnapshot part,ProtoPartModuleSnapshot saved,int index,ConfigNode[] nodes)
        {
            var config=nodes[index];var baseConfig=config;bool mass=ProductionBool(config,"ConvertByMass")==true;int converterIndex=nodes.Take(index).Count(n=>(n.GetValue("name")=="USI_Converter"||n.GetValue("name")=="WOLF_HopperModule"||n.GetValue("name")=="USI_Harvester")&&ProductionBool(n,"IsStandaloneConverter")!=true&&ProductionBool(n,"IsStandaloneHarvester")!=true);
            var row=new ColonyProductionModuleTelemetry {PartId=part.persistentId,ModuleId=uint.TryParse(saved.moduleValues.GetValue("persistentId"),out uint id)&&id!=0?(uint?)id:null,ModuleIndex=index,ModuleType=saved.moduleName,PartName=Bound(part.partInfo.title,100),Enabled=ProductionBool(saved.moduleValues,"isEnabled"),Activated=ProductionBool(saved.moduleValues,"IsActivated"),Basis="proto-config",Reason="Saved settings only; no achieved gross rate is available."};
            int bayIndex=Array.FindIndex(nodes,n=>(n.GetValue("name")=="USI_SwappableBay"||n.GetValue("name")=="WOLF_HopperBay")&&int.TryParse(n.GetValue("moduleIndex"),out int bay)&&bay==converterIndex);
            if(bayIndex>=0)
            {
                if(bayIndex>=part.modules.Count||!int.TryParse(part.modules[bayIndex].moduleValues.GetValue("currentLoadout"),out int loadout))throw new InvalidOperationException("Saved bay selection is unresolved.");
                var options=nodes.Where(n=>n.GetValue("name")=="WOLF_HopperSwapOption"||n.GetValue("name")=="USI_ConverterSwapOption"||n.GetValue("name")=="USI_HarvesterSwapOption").ToArray();
                string optionType=saved.moduleName=="WOLF_HopperModule"?"WOLF_HopperSwapOption":saved.moduleName=="USI_Harvester"?"USI_HarvesterSwapOption":"USI_ConverterSwapOption";
                if(loadout<0||loadout>=options.Length||loadout>63||options[loadout].GetValue("name")!=optionType)throw new InvalidOperationException("Saved loadout is unsupported.");config=options[loadout];row.BayIndex=converterIndex;row.SelectedLoadout=loadout;
            }
            row.Recipe=Bound(config.GetValue("ConverterName")??saved.moduleName,100);row.RecipeHash=ProductionHash(config.ToString());
            row.Configured=new ColonyProductionVector {Inputs=ProductionConfigRates(config,"INPUT_RESOURCE"),Outputs=ProductionConfigRates(config,"OUTPUT_RESOURCE"),Requirements=config.GetNodes("REQUIRED_RESOURCE").Select(n=>new ColonyProductionRequirementTelemetry{Resource=n.GetValue("ResourceName"),Amount=ProductionConfigNumber(n,"Ratio")}).ToArray()};
            if(row.Configured.Requirements.Length>16)throw new InvalidOperationException("Requirement vector exceeded bounds.");
            if(mass){row.Configured=new ColonyProductionVector();row.Reason="Mass-based configured recipe withheld; units per second are unresolved.";}
            if(saved.moduleName=="USI_Harvester"||saved.moduleName=="ModuleResourceHarvester")
            {
                row.Harvester=new ColonyProductionHarvester{Resource=Bound(config.GetValue("ResourceName")??baseConfig.GetValue("ResourceName"),80),Efficiency=ProductionConfigNumber(config.HasValue("Efficiency")?config:baseConfig,"Efficiency"),HarvestThreshold=baseConfig.HasValue("HarvestThreshold")?ProductionConfigNumber(baseConfig,"HarvestThreshold"):0,HarvesterType=baseConfig.HasValue("HarvesterType")?(int)ProductionConfigNumber(baseConfig,"HarvesterType"):0};
                row.Configured.Inputs=ProductionConfigRates(config.HasNode("INPUT_RESOURCE")?config:baseConfig,"INPUT_RESOURCE");row.Configured.Outputs=new ColonyProductionRateTelemetry[0];
                row.Reason="Saved harvester recipe and efficiency coefficient only; abundance-dependent prepared and achieved rates require native execution.";
            }
            if(saved.moduleName=="WOLF_HopperModule")row.Hopper=new ColonyProductionHopper {HopperId=Bound(saved.moduleValues.GetValue("HopperId"),80),Connected=ProductionBool(saved.moduleValues,"IsConnectedToDepot"),Body=Bound(saved.moduleValues.GetValue("DepotBody"),100),Biome=Bound(saved.moduleValues.GetValue("DepotBiome"),100),AllocationPoints=ProductionPoints(config.GetValue("InputResources"))};
            return row;
        }
        static double ProductionConfigNumber(ConfigNode node,string name){if(!double.TryParse(node.GetValue(name),NumberStyles.Float,CultureInfo.InvariantCulture,out double value)||!IsFinite(value))throw new InvalidOperationException("Invalid native config rate.");return value;}
        static ColonyProductionRateTelemetry[] ProductionConfigRates(ConfigNode node,string kind)
        {
            var rows=node.GetNodes(kind);if(rows.Length>16)throw new InvalidOperationException("Recipe exceeded rate bound.");
            return rows.Select(n=>new ColonyProductionRateTelemetry{Resource=n.GetValue("ResourceName"),UnitsPerSecond=ProductionConfigNumber(n,"Ratio"),FlowMode=n.GetValue("FlowMode")??"native-default",DumpExcess=ProductionBool(n,"DumpExcess")==true}).ToArray();
        }
        static void PopulateBackgroundProduction(Vessel vessel,ProtoPartSnapshot part,ColonyProductionModuleTelemetry row,double ut)
        {
            if(row.ModuleId==null||row.Activated!=true||row.Enabled!=true)return;
            var modules=ReadProductionMember(vessel,"vesselModules") as IEnumerable;if(modules==null)return;
            var processor=modules.Cast<object>().SingleOrDefault(m=>m?.GetType().FullName=="BackgroundResourceProcessing.BackgroundResourceProcessor");if(processor==null)return;
            double at=Convert.ToDouble(ReadProductionMember(processor,"LastChangepoint"),CultureInfo.InvariantCulture);if(!IsFinite(at)||ut<at||ut-at>10){row.Reason="Current BRP rates unavailable or stale; only saved configuration is shown.";return;}
            var converters=ReadProductionMember(processor,"Converters") as IEnumerable;if(converters==null)return;
            var candidates=converters.Cast<object>().Take(513).ToArray();if(candidates.Length>512)throw new InvalidOperationException("BRP converter collection exceeds bounds.");
            var mapped=candidates.Where(c=>ReadProductionMember(c,"FlightId") is uint flight&&flight==part.flightID&&ReadProductionMember(c,"ModuleId") is uint module&&module==row.ModuleId.Value).Take(17).ToArray();if(mapped.Length==0||mapped.Length>16)return;
            var inputs=new List<ColonyProductionRateTelemetry>();var outputs=new List<ColonyProductionRateTelemetry>();var states=new List<string>();
            foreach(var converter in mapped)
            {
                double fraction=Convert.ToDouble(ReadProductionMember(converter,"Rate"),CultureInfo.InvariantCulture);if(!IsFinite(fraction)||fraction<0||fraction>1)throw new InvalidOperationException("Invalid BRP modeled rate.");
                states.Add(Convert.ToString(ReadProductionMember(converter,"ConstraintState"),CultureInfo.InvariantCulture));
                foreach(var pair in new[]{Tuple.Create("Inputs",inputs),Tuple.Create("Outputs",outputs)})
                {
                    var entries=ReadProductionMember(converter,pair.Item1) as IEnumerable;if(entries==null)throw new InvalidOperationException("Unresolved BRP vector.");
                    var values=entries.Cast<object>().Take(17).Select(e=>ReadProductionMember(e,"Value")).ToArray();
                    if(values.Length>16||values.Any(value=>!(value is ResourceRatio)))throw new InvalidOperationException("Unsupported native BRP ratio shape.");
                    pair.Item2.AddRange(ProductionRates(values.Cast<ResourceRatio>(),fraction));
                }
            }
            row.Background=new ColonyProductionBackgroundRate {SampleUt=at,ConstraintState=Bound(string.Join(",",states.Distinct()),128),Inputs=MergeProductionRates(inputs),Outputs=MergeProductionRates(outputs)};
            row.Basis="background-model";row.Reason="Current BRP simulation rates, including native constraint fractions; not measured broker delivery. Efficiency already baked into ratios.";
        }
        static ColonyProductionRateTelemetry[] MergeProductionRates(IEnumerable<ColonyProductionRateTelemetry> rows)
        {
            var merged=rows.GroupBy(r=>r.Resource,StringComparer.Ordinal).Select(g=>new ColonyProductionRateTelemetry{Resource=g.Key,UnitsPerSecond=g.Sum(r=>r.UnitsPerSecond),FlowMode=g.First().FlowMode,DumpExcess=g.Any(r=>r.DumpExcess)}).ToArray();if(merged.Length>16||merged.Any(r=>!IsFinite(r.UnitsPerSecond)||r.UnitsPerSecond>1e12))throw new InvalidOperationException("BRP output vector exceeds bounds.");return merged;
        }
        static string ProductionRatesJson(ColonyProductionRateTelemetry[] rows)=>"["+string.Join(",",rows.Select(r=>"{\"resource\":"+Q(r.Resource)+",\"unitsPerSecond\":"+N(r.UnitsPerSecond)+",\"flowMode\":"+Q(r.FlowMode)+",\"dumpExcess\":"+B(r.DumpExcess)+"}"))+"]";
        static string ProductionVectorJson(ColonyProductionVector vector)=>"{\"inputs\":"+ProductionRatesJson(vector.Inputs)+",\"outputs\":"+ProductionRatesJson(vector.Outputs)+",\"requirements\":["+string.Join(",",vector.Requirements.Select(r=>"{\"resource\":"+Q(r.Resource)+",\"amount\":"+N(r.Amount)+"}"))+"]}";
        static string ProductionTelemetryJson(ColonyProductionTelemetry production)
        {
            if(production==null)return "null";
            var rows=production.Modules.Select(r=>
                "{\"partId\":"+r.PartId+",\"moduleId\":"+(r.ModuleId.HasValue?r.ModuleId.Value.ToString(CultureInfo.InvariantCulture):"null")+",\"moduleIndex\":"+r.ModuleIndex+",\"moduleType\":"+Q(r.ModuleType)+",\"partName\":"+Q(r.PartName)+",\"bayIndex\":"+(r.BayIndex.HasValue?r.BayIndex.Value.ToString(CultureInfo.InvariantCulture):"null")+",\"selectedLoadout\":"+(r.SelectedLoadout.HasValue?r.SelectedLoadout.Value.ToString(CultureInfo.InvariantCulture):"null")+",\"recipe\":"+Q(r.Recipe)+",\"recipeHash\":"+Q(r.RecipeHash)+",\"enabled\":"+B(r.Enabled)+",\"activated\":"+B(r.Activated)+",\"basis\":"+Q(r.Basis)+",\"configured\":"+ProductionVectorJson(r.Configured)+
                ",\"prepared\":"+(r.Prepared==null?"null":"{\"sampleUt\":"+N(r.Prepared.SampleUt)+",\"efficiencyMultiplier\":"+N(r.Prepared.EfficiencyMultiplier)+",\"requirementMultiplier\":"+N(r.Prepared.RequirementMultiplier)+",\"rates\":"+ProductionVectorJson(r.Prepared.Rates)+"}")+
                ",\"achieved\":"+(r.Achieved==null?"null":"{\"captureSequence\":"+r.Achieved.CaptureSequence+",\"sampleUt\":"+N(r.Achieved.SampleUt)+",\"intervalGameSeconds\":"+N(r.Achieved.IntervalGameSeconds)+",\"timeFactor\":"+N(r.Achieved.TimeFactor)+",\"inputs\":"+ProductionRatesJson(r.Achieved.Inputs)+",\"outputs\":"+ProductionRatesJson(r.Achieved.Outputs)+"}")+
                ",\"background\":"+(r.Background==null?"null":"{\"sampleUt\":"+N(r.Background.SampleUt)+",\"constraintState\":"+Q(r.Background.ConstraintState)+",\"inputs\":"+ProductionRatesJson(r.Background.Inputs)+",\"outputs\":"+ProductionRatesJson(r.Background.Outputs)+"}")+
                ",\"harvester\":"+(r.Harvester==null?"null":"{\"resource\":"+Q(r.Harvester.Resource)+",\"efficiency\":"+N(r.Harvester.Efficiency)+",\"harvestThreshold\":"+N(r.Harvester.HarvestThreshold)+",\"harvesterType\":"+r.Harvester.HarvesterType+"}")+",\"nativeStatus\":"+Q(r.NativeStatus)+",\"reason\":"+Q(r.Reason)+",\"hopper\":"+(r.Hopper==null?"null":"{\"hopperId\":"+Q(r.Hopper.HopperId)+",\"connected\":"+B(r.Hopper.Connected)+",\"body\":"+Q(r.Hopper.Body)+",\"biome\":"+Q(r.Hopper.Biome)+",\"allocationPoints\":["+string.Join(",",r.Hopper.AllocationPoints.Select(p=>"{\"resource\":"+Q(p.Resource)+",\"points\":"+p.Points+"}"))+"]}")+"}");
            return "{\"status\":"+Q(production.Status)+",\"inventoryStatus\":"+Q(production.InventoryStatus)+",\"reason\":"+Q(production.Reason)+",\"observedUt\":"+N(production.ObservedUt)+
                ",\"budgetOmittedModuleCount\":"+production.BudgetOmittedModuleCount+",\"budgetSelectionSequence\":"+
                (production.BudgetSelectionSequence.HasValue?production.BudgetSelectionSequence.Value.ToString(CultureInfo.InvariantCulture):"null")+",\"modules\":["+string.Join(",",rows)+"]}";
        }
    }
}
