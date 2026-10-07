using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Expanse.WorldBridge
{
    internal sealed class LifeSupportNativeSample
    {
        public object Owner,Settings,Status;public ResourceConverter Converter;public Part CrewPart;public IResourceBroker Broker;
        public int Crew;public float Supply,Waste,Ec,Recycler;public double EndUt;public bool Electricity;public string Key;
    }
    public sealed partial class WorldBridgeAddon
    {
        readonly LifeSupportTelemetryScope lifeSupportScopes=new LifeSupportTelemetryScope();
        readonly Dictionary<Vessel,ColonyBrokerSample[]> lifeSupportSamples=new Dictionary<Vessel,ColonyBrokerSample[]>();
        readonly PowerAverageAccumulator powerAverage=new PowerAverageAccumulator();
        sealed class AverageMode {public ProductionTelemetryMode Mode;public long Epoch;public bool Packed;}
        readonly Dictionary<Vessel,AverageMode> averageModes=new Dictionary<Vessel,AverageMode>();
        static double RealSeconds=>System.Diagnostics.Stopwatch.GetTimestamp()/(double)System.Diagnostics.Stopwatch.Frequency;
        internal static object LifeSupportField(object owner,string name)
        {if(owner==null)return null;var type=owner as Type;for(var t=type??owner.GetType();t!=null;t=t.BaseType){var field=t.GetField(name,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static|BindingFlags.DeclaredOnly);if(field!=null)return field.GetValue(type==null?owner:null);}return null;}
        void ResetLifeSupportTelemetry(){lifeSupportSamples.Clear();lifeSupportScopes.Reset();averageModes.Clear();powerAverage.Reset(ProductionContext);}
        bool EnsureAverageMode(Vessel vessel)
        {
            try
            {
            if(vessel==null||!vessel.loaded)return false;var mode=GetProductionMode(vessel);
            if(mode==null||!mode.TryCapture(vessel.packed,out long epoch))return false;
            if(averageModes.TryGetValue(vessel,out var old)&&(!ReferenceEquals(old.Mode,mode)||old.Epoch!=epoch||old.Packed!=vessel.packed))powerAverage.Invalidate(vessel.id.ToString("D"));
            if(averageModes.Count>=128&&!averageModes.ContainsKey(vessel))return false;
            averageModes[vessel]=new AverageMode{Mode=mode,Epoch=epoch,Packed=vessel.packed};return true;
            }
            catch{powerAverage.Reset(ProductionContext);averageModes.Clear();return false;}
        }
        internal static LifeSupportTelemetryScope.Frame BeginLifeSupportScope(object owner)
        {
            var o=Current;if(o==null||o.productionContext==null||System.Threading.Thread.CurrentThread.ManagedThreadId!=o.productionThread)return null;
            try
            {
                double ut=Planetarium.GetUniversalTime();if(o.productionContext!=o.ProductionContext||IsFinite(o.productionUt)&&ut<o.productionUt)o.ResetProductionTelemetry();
                var vessel=LifeSupportVessel(owner);if(vessel!=null)o.lifeSupportSamples.Remove(vessel);
                return o.lifeSupportScopes.Enter(owner,o.ProductionContext,HighLogic.LoadedSceneIsFlight&&!o.loadUnresolved&&owner?.GetType()==ColonyRuntime.LifeSupportOwnerType&&vessel!=null&&vessel.loaded&&!vessel.isEVA);
            }
            catch{return o.lifeSupportScopes.Enter(null,o.ProductionContext,false);}
        }
        internal static void EndLifeSupportScope(LifeSupportTelemetryScope.Frame frame){try{Current?.lifeSupportScopes.Exit(frame);}catch{}}
        internal static void BindLifeSupportRecipe(object owner,ConversionRecipe recipe,bool electricity){try{Current?.lifeSupportScopes.Bind(owner,recipe,electricity);}catch{}}
        static Vessel LifeSupportVessel(object owner)=>owner is VesselModule?LifeSupportField(owner,"vessel") as Vessel:null;
        static double? NativeProducerProcessedEnd(BaseConverter module,double observed,double seconds)
        {
            try
            {
            // Reviewed stock GetDeltaTime advances this field by the original
            // processed dt, which can end before current UT during catch-up.
            if(module.GetType().GetMethod("GetDeltaTime",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,Type.EmptyTypes,null)?.DeclaringType!=typeof(BaseConverter))return null;
            var end=LifeSupportField(module,"lastUpdateTime");return end is double value&&LifeSupportTelemetryScope.Interval(observed,value,seconds)?(double?)value:null;
            }
            catch{return null;}
        }
        static LifeSupportNativeSample ReadLifeSupportState(object owner)
        {
            var ownerType=ColonyRuntime.LifeSupportOwnerType;if(owner==null||owner.GetType()!=ownerType)return null;
            var scenario=LifeSupportField(ownerType.Assembly.GetType("LifeSupport.LifeSupportScenario"),"<Instance>k__BackingField");
            var settings=LifeSupportField(LifeSupportField(scenario,"<settings>k__BackingField"),"_Settings");var status=LifeSupportField(owner,"_vesselStatus");
            if(settings==null||status==null)return null;
            var s=new LifeSupportNativeSample{Owner=owner,Settings=settings,Status=status,Converter=LifeSupportField(owner,"_resourceConverter") as ResourceConverter,
                Broker=LifeSupportField(owner,"_resourceBroker") as IResourceBroker,CrewPart=LifeSupportField(owner,"_crewPart") as Part,Crew=(int)LifeSupportField(owner,"_currentCrewCount"),EndUt=(double)LifeSupportField(owner,"LastUpdateTime"),
                Supply=(float)LifeSupportField(settings,"<SupplyAmount>k__BackingField"),Waste=(float)LifeSupportField(settings,"<WasteAmount>k__BackingField"),Ec=(float)LifeSupportField(settings,"<ECAmount>k__BackingField"),Recycler=(float)LifeSupportField(status,"<RecyclerMultiplier>k__BackingField")};
            if(s.Crew<=0||s.Crew>4096||!IsFinite(s.Supply)||s.Supply<0||!IsFinite(s.Waste)||s.Waste<0||!IsFinite(s.Ec)||s.Ec<0||!IsFinite(s.Recycler)||s.Recycler<0||s.Recycler>1)return null;
            s.Key=s.Crew+"/"+s.Supply.ToString("R",CultureInfo.InvariantCulture)+"/"+s.Waste.ToString("R",CultureInfo.InvariantCulture)+"/"+s.Ec.ToString("R",CultureInfo.InvariantCulture)+"/"+s.Recycler.ToString("R",CultureInfo.InvariantCulture);return s;
        }
        static bool SameLifeSupportState(LifeSupportNativeSample s)
        {
            var now=ReadLifeSupportState(s?.Owner);return now!=null&&now.Key==s.Key&&ReferenceEquals(now.Settings,s.Settings)&&ReferenceEquals(now.Status,s.Status)&&ReferenceEquals(now.Converter,s.Converter)&&ReferenceEquals(now.CrewPart,s.CrewPart)&&ReferenceEquals(now.Broker,s.Broker);
        }
        LifeSupportNativeSample QualifyLifeSupport(ResourceConverter converter,double seconds,ConversionRecipe recipe,Part part,PartModule module,float efficiency,double ut)
        {
            var f=lifeSupportScopes.Current;if(f==null||!ReferenceEquals(module,null)||efficiency!=1||!lifeSupportScopes.Consume(f.Owner,ProductionContext,recipe,out bool ec))return null;
            var s=ReadLifeSupportState(f.Owner);var vessel=LifeSupportVessel(f.Owner);
            if(s==null||ReferenceEquals(vessel,null)||!vessel.loaded||vessel.isEVA||!ReferenceEquals(s.Converter,converter)||!ReferenceEquals(s.CrewPart,part)||ReferenceEquals(part,null)||!ReferenceEquals(part.vessel,vessel)||s.Broker?.GetType()!=typeof(ResourceBroker)||!ReferenceEquals(LifeSupportField(converter,"_broker"),s.Broker)||!LifeSupportTelemetryScope.Interval(ut,s.EndUt,seconds))return null;
            if(recipe?.Inputs==null||recipe.Outputs==null||recipe.Requirements==null||recipe.Requirements.Count!=0||recipe.Inputs.Count!=1||recipe.Outputs.Count!=(ec?0:1))return null;
            Func<ResourceRatio,string,double,bool> match=(r,name,rate)=>r.ResourceName==name&&r.FlowMode==ResourceFlowMode.ALL_VESSEL&&r.DumpExcess&&IsFinite(r.Ratio)&&r.Ratio==rate&&r.Ratio<=1e12;
            if(!match(recipe.Inputs[0],ec?"ElectricCharge":"Supplies",ec?(double)(s.Ec*s.Crew):(double)(s.Supply*s.Crew*s.Recycler))||!ec&&!match(recipe.Outputs[0],"Mulch",(double)(s.Waste*s.Crew*s.Recycler)))return null;
            s.Electricity=ec;return s;
        }
        bool LifeSupportModeMatches(ColonyBrokerSample sample)
            =>sample?.LifeSupport!=null&&!ReferenceEquals(sample.Vessel,null)&&ReferenceEquals(LifeSupportVessel(sample.LifeSupport.Owner),sample.Vessel)&&sample.Vessel.loaded&&!sample.Vessel.isEVA&&
                productionModes.TryGetValue(sample.Vessel,out var mode)&&ReferenceEquals(mode,sample.Mode)&&mode.Matches(sample.ModeEpoch,sample.Packed,sample.Vessel.packed)&&SameLifeSupportState(sample.LifeSupport);
        void StoreLifeSupportSample(ColonyBrokerSample sample)
        {
            if(!lifeSupportSamples.TryGetValue(sample.Vessel,out var slots)){if(lifeSupportSamples.Count>=128)return;slots=new ColonyBrokerSample[2];lifeSupportSamples.Add(sample.Vessel,slots);}slots[sample.LifeSupport.Electricity?1:0]=sample;
            if(sample.Packed&&sample.LifeSupport.Electricity)RecordAverage(sample,sample.LifeSupport.EndUt,"supported-native-callbacks",true);
        }
        static bool LifeSupportAmountsMatch(ColonyBrokerSample sample)
        {
            Func<Dictionary<string,double>,ColonyProductionRateTelemetry[],bool> within=(amounts,rates)=>amounts.All(p=>rates.Any(r=>r.Resource==p.Key)&&p.Value<=rates.Where(r=>r.Resource==p.Key).Sum(r=>r.UnitsPerSecond)*sample.Frame.Seconds+Math.Max(1e-9,p.Value*1e-6));
            return within(sample.Frame.Inputs,sample.Prepared.Rates.Inputs)&&within(sample.Frame.Outputs,sample.Prepared.Rates.Outputs);
        }
        void RecordAverage(ColonyBrokerSample sample,double end,string basis,bool partial)
        {
            if(!IsSurfaceSettled(sample.Vessel.situation,sample.Vessel.Landed,sample.Vessel.Splashed)||!EnsureAverageMode(sample.Vessel))return;
            sample.Frame.Outputs.TryGetValue("ElectricCharge",out double generation);sample.Frame.Inputs.TryGetValue("ElectricCharge",out double consumption);
            bool ec=sample.Prepared.Rates.Inputs.Any(r=>r.Resource=="ElectricCharge")||sample.Prepared.Rates.Outputs.Any(r=>r.Resource=="ElectricCharge");if(!ec)return;
            powerAverage.Add(sample.Vessel.id.ToString("D"),end-sample.Frame.Seconds,end,generation,consumption,basis,partial,sample.Frame.CaptureSequence);
            powerAverage.Tick(ProductionContext,RealSeconds,sample.Frame.Ut);
        }
        ColonyLifeSupportTelemetry ObserveLifeSupportTelemetry(Vessel vessel,double ut)
        {
            ColonyRuntime.EnsureLifeSupportObserver();
            var result=new ColonyLifeSupportTelemetry{Status="unavailable",ObservedUt=ut,Reason=ColonyRuntime.LifeSupportHookFailure??"No current qualified loaded USI callback; unloaded and EVA crew remain unobserved."};
            try
            {
            if(!lifeSupportSamples.TryGetValue(vessel,out var slots))return result;
            for(int i=0;i<2;i++)
            {
                var sample=slots[i];if(!LifeSupportModeMatches(sample)||sample.LifeSupport.Crew!=vessel.GetCrewCount()||!ProductionTelemetryLedger.Fresh(sample.Frame,ProductionContext,sample.LifeSupport.Key,ut,true))continue;
                var s=sample.LifeSupport;var f=sample.Frame;
                if(i==0){f.Inputs.TryGetValue("Supplies",out double used);f.Outputs.TryGetValue("Mulch",out double mulch);result.Supply=new LifeSupportSupplyStream{SampleUt=f.Ut,ProcessedEndUt=s.EndUt,IntervalGameSeconds=f.Seconds,CaptureSequence=f.CaptureSequence,TimeFactor=sample.TimeFactor,CrewCount=s.Crew,RecyclerMultiplier=s.Recycler,
                    GrossSuppliesPerSecond=(double)(s.Supply*s.Crew),GrossMulchPerSecond=(double)(s.Waste*s.Crew),ConfiguredSuppliesPerSecond=sample.Prepared.Rates.Inputs[0].UnitsPerSecond,ConfiguredMulchPerSecond=sample.Prepared.Rates.Outputs[0].UnitsPerSecond,SuppliesConsumed=used,MulchProduced=mulch};}
                else{f.Inputs.TryGetValue("ElectricCharge",out double used);result.CrewElectricity=new LifeSupportEcStream{SampleUt=f.Ut,ProcessedEndUt=s.EndUt,IntervalGameSeconds=f.Seconds,CaptureSequence=f.CaptureSequence,TimeFactor=sample.TimeFactor,CrewCount=s.Crew,ConfiguredEcPerSecond=sample.Prepared.Rates.Inputs[0].UnitsPerSecond,ElectricityConsumed=used};}
            }
            if(result.Supply!=null||result.CrewElectricity!=null){result.Status="partial";result.Reason="Independent accepted native Supplies/Mulch and crew EC callbacks; configured demand is intent. Catch-up intervals are historical. Other crew remain unobserved.";}return result;
            }
            catch{result.Status=result.Supply!=null||result.CrewElectricity!=null?"partial":"unavailable";result.Reason="USI observation incomplete; missing streams remain unknown.";return result;}
        }
        static string LifeSupportJson(ColonyLifeSupportTelemetry value)
        {
            if(value==null)return "null";var b=new StringBuilder("{\"status\":"+Q(value.Status)+",\"reason\":"+Q(value.Reason)+",\"observedUt\":"+N(value.ObservedUt)+",\"supply\":");
            var s=value.Supply;b.Append(s==null?"null":"{"+LifeSupportCommonJson(s.SampleUt,s.ProcessedEndUt,s.IntervalGameSeconds,s.CaptureSequence,s.CrewCount,s.TimeFactor)+",\"recyclerMultiplier\":"+N(s.RecyclerMultiplier)+",\"grossSuppliesPerSecond\":"+N(s.GrossSuppliesPerSecond)+",\"grossMulchPerSecond\":"+N(s.GrossMulchPerSecond)+",\"configuredSuppliesPerSecond\":"+N(s.ConfiguredSuppliesPerSecond)+",\"configuredMulchPerSecond\":"+N(s.ConfiguredMulchPerSecond)+",\"suppliesConsumed\":"+N(s.SuppliesConsumed)+",\"mulchProduced\":"+N(s.MulchProduced)+"}");
            var e=value.CrewElectricity;b.Append(",\"crewElectricity\":").Append(e==null?"null":"{"+LifeSupportCommonJson(e.SampleUt,e.ProcessedEndUt,e.IntervalGameSeconds,e.CaptureSequence,e.CrewCount,e.TimeFactor)+",\"configuredEcPerSecond\":"+N(e.ConfiguredEcPerSecond)+",\"electricityConsumed\":"+N(e.ElectricityConsumed)+"}");return b.Append('}').ToString();
        }
        static string LifeSupportCommonJson(double sample,double end,double seconds,long sequence,int crew,double factor)=>"\"sampleUt\":"+N(sample)+",\"processedEndUt\":"+N(end)+",\"intervalGameSeconds\":"+N(seconds)+",\"captureSequence\":"+sequence+",\"crewCount\":"+crew+",\"timeFactor\":"+N(factor);
        static string PowerAverageJson(PowerAverageTelemetry p)=>p==null?"null":"{\"windowId\":"+p.WindowId+",\"windowStartUt\":"+N(p.WindowStartUt)+",\"windowEndUt\":"+N(p.WindowEndUt)+",\"windowGameSeconds\":"+N(p.WindowGameSeconds)+",\"coveredGameSeconds\":"+N(p.CoveredGameSeconds)+",\"reportRealSeconds\":"+N(p.ReportRealSeconds)+",\"ageRealSeconds\":"+N(p.AgeRealSeconds)+",\"generationEc\":"+N(p.GenerationEc)+",\"consumptionEc\":"+N(p.ConsumptionEc)+",\"generationEcPerSecond\":"+N(p.GenerationEcPerSecond)+",\"consumptionEcPerSecond\":"+N(p.ConsumptionEcPerSecond)+",\"status\":"+Q(p.Status)+",\"basis\":"+Q(p.Basis)+",\"reason\":"+Q(p.Reason)+"}";
    }
}
