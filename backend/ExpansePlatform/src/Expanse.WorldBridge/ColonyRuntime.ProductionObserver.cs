using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using Expanse.Domain.Colonies;
using HarmonyLib;

namespace Expanse.WorldBridge
{
    public sealed partial class ColonyRuntime
    {
        sealed class NativeProductionSample
        {
            public BaseConverter Module; public object Option; public string Context,Status;
            public IResourceBroker Broker;
            public double Ut,Seconds,Efficiency;
            public ResourceRatio[] RecipeInputs,RecipeOutputs;
            public readonly Dictionary<string,double> Inputs=new Dictionary<string,double>(StringComparer.Ordinal);
            public readonly Dictionary<string,double> Outputs=new Dictionary<string,double>(StringComparer.Ordinal);
        }
        sealed class ProductionBrokerFrame { public BaseConverter Target;public NativeProductionSample Sample;public ColonyBrokerSample Telemetry;public ProductionBrokerFrame Previous;public bool Finished; }
        [ThreadStatic] static ProductionBrokerFrame productionBrokerFrame;
        static readonly HashSet<BaseConverter> ProductionObservedModules=new HashSet<BaseConverter>();
        static readonly Dictionary<BaseConverter,NativeProductionSample> ProductionNativeSamples=new Dictionary<BaseConverter,NativeProductionSample>();
        static readonly List<MethodInfo> ProductionObserverMethods=new List<MethodInfo>();
        static Harmony productionHarmony;static string productionObserverContext="",productionObserverFailure="";static int productionObserverThread;
        internal static string ProductionBrokerHookFailure=>productionObserverFailure;
        static void EnsureProductionObserver(string context)
        {
            if(productionObserverContext!=context){ProductionObservedModules.Clear();ProductionNativeSamples.Clear();productionBrokerFrame=null;productionObserverContext=context;productionObserverThread=Thread.CurrentThread.ManagedThreadId;}
            EnsureProductionBrokerHook();
        }
        internal static void EnsureProductionBrokerHook()
        {
            if(productionHarmony!=null||productionObserverFailure.Length>0)return;
            try
            {
                productionHarmony=new Harmony("Expanse.WorldBridge.ColonyPhysicalProduction");
                var process=typeof(ResourceConverter).GetMethod("ProcessRecipe",new[]{typeof(double),typeof(ConversionRecipe),typeof(Part),typeof(PartModule),typeof(float)});
                if(process==null||process.ReturnType!=typeof(ConverterResults))throw new MissingMethodException("Installed stock recipe broker boundary changed.");
                ProductionObserverMethods.Add(process);
                productionHarmony.Patch(process,prefix:ProductionPatch(nameof(ProductionProcessPrefix)),postfix:ProductionPatch(nameof(ProductionProcessPostfix)),transpiler:ProductionPatch(nameof(ProductionBrokerTranspiler)),finalizer:ProductionPatch(nameof(ProductionProcessFinalizer)));
            }
            catch(Exception ex){productionObserverFailure="Actual native physical production observer unavailable: "+Bound(ex.Message,256);StopProductionObservers();}
        }
        static HarmonyMethod ProductionPatch(string name)=>new HarmonyMethod(typeof(ColonyRuntime).GetMethod(name,BindingFlags.Static|BindingFlags.NonPublic));
        static void StopProductionObservers()
        {
            try{if(productionHarmony!=null)productionHarmony.UnpatchAll("Expanse.WorldBridge.ColonyPhysicalProduction");}catch { }
            productionHarmony=null;ProductionObserverMethods.Clear();ProductionObservedModules.Clear();ProductionNativeSamples.Clear();productionBrokerFrame=null;productionObserverContext="";
        }
        static void ProductionProcessPrefix(ResourceConverter __instance,double deltaTime,ConversionRecipe recipe,Part resPart,PartModule resModule,float efficiencyBonus,out ProductionBrokerFrame __state)
        {
            var selectedModule=resModule as BaseConverter;
            __state=new ProductionBrokerFrame{Target=selectedModule,Previous=productionBrokerFrame,
                Telemetry=WorldBridgeAddon.BeginProductionTelemetry(__instance,deltaTime,recipe,resPart,resModule,efficiencyBonus)};productionBrokerFrame=__state;
            if(Thread.CurrentThread.ManagedThreadId!=productionObserverThread)return;
            if(selectedModule!=null&&ProductionObservedModules.Contains(selectedModule))ProductionNativeSamples.Remove(selectedModule);
            try
            {
                var module=resModule as BaseConverter;
                if(Thread.CurrentThread.ManagedThreadId!=productionObserverThread||Current==null||Current.ContextKey!=productionObserverContext||module==null||!ProductionObservedModules.Contains(module)||module.part!=resPart||!module.IsActivated||module.vessel==null||!module.vessel.loaded||!Finite(deltaTime)||deltaTime<=0||deltaTime>21600||!Finite(efficiencyBonus)||efficiencyBonus<0||efficiencyBonus>10000||ReviewedPrivateField(__instance,"_broker")?.GetType()!=typeof(ResourceBroker)||recipe?.Inputs==null||recipe.Outputs==null||recipe.Inputs.Count>16||recipe.Outputs.Count>16)return;
                if(recipe.Inputs.Concat(recipe.Outputs).Any(r=>string.IsNullOrEmpty(r.ResourceName)||!Finite(r.Ratio)||r.Ratio<0))return;
                __state.Sample=new NativeProductionSample{Module=module,Broker=(IResourceBroker)ReviewedPrivateField(__instance,"_broker"),Option=ReviewedPrivateField(module,"_swapOption"),Context=productionObserverContext,Ut=Planetarium.GetUniversalTime(),Seconds=deltaTime,Efficiency=efficiencyBonus,RecipeInputs=recipe.Inputs.ToArray(),RecipeOutputs=recipe.Outputs.ToArray()};
            }
            catch { }
        }
        static void ProductionProcessPostfix(ConverterResults __result,ProductionBrokerFrame __state)
        {
            WorldBridgeAddon.CompleteProductionTelemetry(__state?.Telemetry,true,__result);
            try
            {
                if(__state?.Sample!=null&&Current!=null&&Current.ContextKey==__state.Sample.Context&&ProductionObservedModules.Contains(__state.Sample.Module)&&Finite(__result.TimeFactor)&&__result.TimeFactor>=0)
                {__state.Sample.Status=Bound(__result.Status??"",256);ProductionNativeSamples[__state.Sample.Module]=__state.Sample;}
            }
            catch { }
            finally{if(__state!=null){productionBrokerFrame=__state.Previous;__state.Finished=true;}}
        }
        static Exception ProductionProcessFinalizer(Exception __exception,ProductionBrokerFrame __state)
        {if(__state!=null&&!__state.Finished){WorldBridgeAddon.CompleteProductionTelemetry(__state.Telemetry,false,default(ConverterResults));if(__state.Target!=null)ProductionNativeSamples.Remove(__state.Target);productionBrokerFrame=__state.Previous;}return __exception;}
        static IEnumerable<CodeInstruction> ProductionBrokerTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var rows=instructions.ToList();int requests=0,stores=0;
            foreach(var row in rows)
            {
                var method=row.operand as MethodInfo;
                if(row.opcode!=OpCodes.Callvirt||method?.DeclaringType!=typeof(IResourceBroker)||method.ReturnType!=typeof(double)||!method.GetParameters().Select(p=>p.ParameterType).SequenceEqual(new[]{typeof(Part),typeof(string),typeof(double),typeof(double),typeof(ResourceFlowMode)}))continue;
                if(method.Name=="RequestResource"){requests++;row.opcode=OpCodes.Call;row.operand=typeof(ColonyRuntime).GetMethod(nameof(ProductionBrokerRequest),BindingFlags.Static|BindingFlags.NonPublic);}
                if(method.Name=="StoreResource"){stores++;row.opcode=OpCodes.Call;row.operand=typeof(ColonyRuntime).GetMethod(nameof(ProductionBrokerStore),BindingFlags.Static|BindingFlags.NonPublic);}
            }
            if(requests!=1||stores!=1)throw new MissingMethodException("Native stock recipe direct broker call shape changed; production observation withheld.");return rows;
        }
        static double ProductionBrokerRequest(IResourceBroker broker,Part part,string resource,double amount,double seconds,ResourceFlowMode flow)
        {double actual=broker.RequestResource(part,resource,amount,seconds,flow);WorldBridgeAddon.RecordProductionTelemetry(broker,part,resource,amount,actual,false);RecordProductionBroker(broker,part,resource,amount,actual,false);return actual;}
        static double ProductionBrokerStore(IResourceBroker broker,Part part,string resource,double amount,double seconds,ResourceFlowMode flow)
        {double actual=broker.StoreResource(part,resource,amount,seconds,flow);WorldBridgeAddon.RecordProductionTelemetry(broker,part,resource,amount,actual,true);RecordProductionBroker(broker,part,resource,amount,-actual,true);return actual;}
        static void RecordProductionBroker(IResourceBroker broker,Part part,string resource,double requested,double actual,bool output)
        {
            // Installed StoreResource calls Part.RequestResource with a negative
            // amount and returns its negative credited amount. Observe only its
            // string overload; the internal int call cannot double-count it.
            try
            {
                var sample=productionBrokerFrame?.Sample;if(sample==null||!ReferenceEquals(sample.Broker,broker)||sample.Module.part!=part||Thread.CurrentThread.ManagedThreadId!=productionObserverThread||string.IsNullOrEmpty(resource)||resource.Length>128||!Finite(actual)||actual<0||!Finite(requested)||requested<0||actual>requested+Math.Max(1e-9,requested*1e-6))return;
                var rows=output?sample.Outputs:sample.Inputs;if(rows.Count>=16&&!rows.ContainsKey(resource))return;
                rows[resource]=(rows.TryGetValue(resource,out double before)?before:0)+actual;
            }
            catch { }
        }
        static List<ColonyProductionRate> ProductionRatesFrom(ResourceRatio[] rows)=>rows.GroupBy(r=>r.ResourceName,StringComparer.Ordinal).Select(g=>new ColonyProductionRate{Resource=g.Key,UnitsPerSecond=g.Sum(r=>r.Ratio)}).OrderBy(r=>r.Resource,StringComparer.Ordinal).ToList();
        static List<ColonyProductionRate> ProductionDelivered(Dictionary<string,double> rows,double seconds)=>rows.Select(r=>new ColonyProductionRate{Resource=r.Key,UnitsPerSecond=r.Value/seconds}).OrderBy(r=>r.Resource,StringComparer.Ordinal).ToList();
        void PopulateProductionObservations(ColonyEnvironment env)
        {
            if(!state.Plans.Any(p=>p.Production.Any(c=>c.Steps.Count>0))){ProductionObservedModules.Clear();ProductionNativeSamples.Clear();return;}
            EnsureProductionObserver(env.ContextKey);var observed=new HashSet<BaseConverter>();
            foreach(var plan in state.Plans.Where(p=>p.Production.Count>0))foreach(var item in plan.Quote.ProductionInvestments)
            {
                var claim=plan.Production.Single(c=>c.Id==item.Id);if(claim.Steps.Count==0)continue;
                var agriculture=claim.Steps.Last();var observation=new ColonyProductionObservation{PlanId=plan.Id,InvestmentId=item.Id,ContextKey=env.ContextKey,FacilityId=agriculture.FacilityId,PartId=agriculture.PartId,ModuleId=agriculture.ModuleId,RecipeHash=item.Recipe.ConfigurationHash,ObservedUt=env.Ut,Provider="Installed stock ResourceBroker actual returned physical quantities; native ownership retained.",Reason="Current loaded native production observation is unavailable."};
                env.Production.Observations.Add(observation);
                try
                {
                    RequireProductionInventoryCurrent(plan,item,claim);
                    if(TryProductionBackground(plan,item,claim,env,observation))continue;
                    var module=(BaseConverter)ResolveProductionModule(plan,item.BuildingId,item.Recipe.CraftPartId,item.Recipe.PartName,item.Recipe.ModuleName,false);observed.Add(module);
                    RequireProductionLoadout(module,item.Recipe.OptionIndex,item.Recipe.OptionHash,"USI_ConverterSwapOption");observation.Active=module.IsActivated;
                    if(!module.IsActivated)throw new InvalidOperationException("Cultivation is idle; no productive output is claimed.");
                    foreach(var feed in item.Recipe.Feeds)
                    {
                        var hopper=(WOLF.WOLF_HopperModule)ResolveProductionModule(plan,item.HopperBuildingId,feed.CraftPartId,feed.PartName,"WOLF_HopperModule",false);observed.Add(hopper);
                        RequireProductionLoadout(hopper,feed.OptionIndex,feed.OptionHash,"WOLF_HopperSwapOption");if(!hopper.IsConnectedToDepot||string.IsNullOrEmpty(hopper.HopperId)||!hopper.IsActivated)throw new InvalidOperationException("The exact native raw-feed hopper is not connected and active.");
                        var connect=claim.Steps.Single(s=>s.Kind=="hopperConnect"&&s.OptionHash==feed.OptionHash);if(hopper.HopperId!=connect.HopperId||connect.HopperId.Length==0)throw new InvalidOperationException("Native feed allocation was replaced after the saved connection receipt.");
                        WOLF.WOLF_ScenarioModule scenario;WOLF.IRegistryCollection registry;string unavailable;
                        if(!TryWolfRegistry(out scenario,out registry,out unavailable)||!ReferenceEquals(ReviewedPrivateField(hopper,"_registry"),registry))throw new InvalidOperationException("Current exact native feed registry is unavailable.");
                        var metadata=registry.GetHoppers().SingleOrDefault(h=>h.Id==connect.HopperId);
                        var colony=state.Colonies.Single(c=>c.Id==plan.ColonyId);
                        if(metadata?.Depot==null||metadata.Depot.Body!=colony.Site.Body||metadata.Depot.Biome!=colony.Site.Biome||!ProductionIngredientsSame(metadata.Recipe.InputIngredients,new Dictionary<string,int>{{feed.Resource,feed.WolfPoints}})||metadata.Recipe.OutputIngredients.Count!=0)throw new InvalidOperationException("Current native feed allocation/recipe differs from the exact saved reviewed connection.");
                    }
                    RequireProductionStartConditions(plan,item,module,true,env);
                    if(!ProductionNativeSamples.TryGetValue(module,out NativeProductionSample sample)||sample.Context!=env.ContextKey||env.Ut<sample.Ut||env.Ut-sample.Ut>10||!ReferenceEquals(sample.Option,ReviewedPrivateField(module,"_swapOption")))throw new InvalidOperationException(productionObserverFailure.Length>0?productionObserverFailure:"Awaiting an actual native broker callback for the current exact selected recipe.");
                    RequireProductionRecipeVector(sample.RecipeInputs,item.Recipe.Inputs);RequireProductionRecipeVector(sample.RecipeOutputs,item.Recipe.Outputs);
                    observation.ObservedUt=sample.Ut;observation.ObservedRecipeInputs=ProductionRatesFrom(sample.RecipeInputs);observation.ObservedRecipeOutputs=ProductionRatesFrom(sample.RecipeOutputs);observation.CurrentNativeEfficiency=sample.Efficiency;
                    observation.NativeSampleSeconds=sample.Seconds;observation.NativeStatus=sample.Status;observation.NativeDeliveredInputs=ProductionDelivered(sample.Inputs,sample.Seconds);observation.NativeDeliveredOutputs=ProductionDelivered(sample.Outputs,sample.Seconds);
                    if(!sample.Outputs.TryGetValue("Supplies",out double supplies)||supplies<=0)throw new InvalidOperationException("The latest native callback stored no Supplies; full storage, missing feed or native throttling yields zero confirmed output.");
                    observation.Witness=ColonyStateCodec.Hash(System.Text.Encoding.UTF8.GetBytes(item.Recipe.ConfigurationHash+"|"+agriculture.PartId+"|"+agriculture.ModuleId+"|"+sample.Ut.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+"|"+sample.Seconds.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+"|"+string.Join(";",sample.Inputs.OrderBy(r=>r.Key).Select(r=>r.Key+"="+r.Value.ToString("R",System.Globalization.CultureInfo.InvariantCulture)))+"|"+string.Join(";",sample.Outputs.OrderBy(r=>r.Key).Select(r=>r.Key+"="+r.Value.ToString("R",System.Globalization.CultureInfo.InvariantCulture)))));observation.Qualified=true;
                    observation.Reason="Actual native cultivation delivered physical Supplies after current power, inputs and named specialist checks. Managed stock changes only through a conserved physical transfer receipt.";
                }
                catch(Exception ex){observation.Reason=Bound(ex.Message,512);}
            }
            ProductionObservedModules.Clear();foreach(var module in observed.Take(256))ProductionObservedModules.Add(module);
            foreach(var module in ProductionNativeSamples.Keys.Where(m=>!ProductionObservedModules.Contains(m)).ToArray())ProductionNativeSamples.Remove(module);
        }
    }
}
