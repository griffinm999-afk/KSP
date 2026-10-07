using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Expanse.Domain.Colonies;
using HarmonyLib;
using UnityEngine;

namespace Expanse.WorldBridge
{
    public sealed partial class ColonyRuntime
    {
        sealed class UtilityRecipeSample
        {
            public BaseConverter Module;public string Context;public double Ut,InputEc;public ResourceRatio[] Inputs,Outputs,Requirements;public object SwapOption;
            public double NativeMultiplier,MultiplierUt,FillAmount,TakeAmount;public bool MultiplierObserved;
            // Diagnostic history only; these fields never qualify a recipe pair.
            // Returned/TimeFactor describe a return with an accepted prefix.
            public bool ProcessSeen,ProcessReturned;public double ProcessUt=double.NaN,ProcessTimeFactor=double.NaN;public string ProcessReason="not-called";
        }
        // Harmony keys __state by the patch method's declaring class, even
        // across different Harmony owners on the same original method.
        // Keep this pair separate from ColonyRuntime's production broker pair.
        static class UtilityProcessObserver
        {
            static void Prefix(double deltaTime,ConversionRecipe recipe,Part resPart,PartModule resModule,float efficiencyBonus,out UtilityRecipeSample __state)
                =>UtilityProcessPrefix(deltaTime,recipe,resPart,resModule,efficiencyBonus,out __state);
            static void Postfix(ConverterResults __result,UtilityRecipeSample __state)
                =>UtilityProcessPostfix(__result,__state);
            internal static MethodInfo PrefixMethod=>typeof(UtilityProcessObserver).GetMethod(nameof(Prefix),BindingFlags.Static|BindingFlags.NonPublic);
            internal static MethodInfo PostfixMethod=>typeof(UtilityProcessObserver).GetMethod(nameof(Postfix),BindingFlags.Static|BindingFlags.NonPublic);
        }
        static readonly Dictionary<BaseConverter,UtilityRecipeSample> UtilityRecipes=new Dictionary<BaseConverter,UtilityRecipeSample>();
        static readonly List<MethodInfo> RecipeTargets=new List<MethodInfo>();
        static Harmony recipeHarmony;static string recipeContext="",recipeFailure="",recipeObservationWarning="";
        static void EnsureRecipeObserver(string context)
        {
            if(recipeContext!=context){UtilityRecipes.Clear();recipeContext=context;}
            if(recipeHarmony!=null || recipeFailure.Length>0)return;
            try
            {
                // Patch only reviewed native implementations. Observe the final
                // most-derived return, not intermediate base or swap recipes.
                var types=new List<Type> {typeof(ModuleResourceConverter),typeof(ModuleResourceHarvester),typeof(USITools.USI_Converter),typeof(USITools.USI_Harvester)};
                foreach(var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(a=>a.GetName().Name=="SystemHeat" && a.GetName().Version==new Version(0,9,1,0)))
                foreach(var name in new[]{"SystemHeat.ModuleSystemHeatConverter","SystemHeat.ModuleSystemHeatHarvester"}){var type=assembly.GetType(name,false);if(type!=null)types.Add(type);}
                recipeHarmony=new Harmony("Expanse.WorldBridge.ColonyNativeUtilityRecipes");
                var postfix=typeof(ColonyRuntime).GetMethod(nameof(UtilityRecipePostfix),BindingFlags.Static|BindingFlags.NonPublic);
                var prefix=typeof(ColonyRuntime).GetMethod(nameof(UtilityRecipePrefix),BindingFlags.Static|BindingFlags.NonPublic);
                foreach(var type in types)
                {
                    var target=UtilityRecipeTarget(type);
                    if(RecipeTargets.Contains(target))continue;RecipeTargets.Add(target);recipeHarmony.Patch(target,prefix:new HarmonyMethod(prefix),postfix:new HarmonyMethod(postfix));
                }
                var process=typeof(ResourceConverter).GetMethod("ProcessRecipe",new[]{typeof(double),typeof(ConversionRecipe),typeof(Part),typeof(PartModule),typeof(float)});
                if(process==null||process.ReturnType!=typeof(ConverterResults))throw new MissingMethodException("Native final recipe multiplier boundary changed.");
                RecipeTargets.Add(process);recipeHarmony.Patch(process,prefix:new HarmonyMethod(UtilityProcessObserver.PrefixMethod),postfix:new HarmonyMethod(UtilityProcessObserver.PostfixMethod));
            }
            catch(Exception ex){recipeFailure="Native final recipe observer unavailable: "+Bound(ex.Message,256);StopRecipeObserver();}
        }
        static MethodInfo UtilityRecipeTarget(Type type)
        {
            var target=type.GetMethod("PrepareRecipe",BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance,null,new[]{typeof(double)},null);
            if(target==null || target.ReturnType!=typeof(ConversionRecipe))throw new MissingMethodException("Native converter recipe signature changed.");
            // Harmony requires the actual declaring method, not an inherited
            // MethodInfo reflected through a SystemHeat derived type.
            target=target.DeclaringType.GetMethod("PrepareRecipe",BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.DeclaredOnly,null,new[]{typeof(double)},null);
            if(target==null || target.ReturnType!=typeof(ConversionRecipe) || target.ReflectedType!=target.DeclaringType)throw new MissingMethodException("Native declaring converter recipe signature changed.");
            return target;
        }
        static void StopRecipeObserver()
        {
            try{if(recipeHarmony!=null)recipeHarmony.UnpatchAll("Expanse.WorldBridge.ColonyNativeUtilityRecipes");}catch { }
            recipeHarmony=null;RecipeTargets.Clear();UtilityRecipes.Clear();recipeContext="";recipeObservationWarning="";
        }
        static void UtilityRecipePrefix(BaseConverter __instance,MethodBase __originalMethod,out UtilityRecipeSample __state)
        {
            __state=null;
            // Remove while preparing: a failed or changed native preparation
            // cannot leave an earlier completed pair eligible.
            if(__instance!=null&&__instance.GetType().GetMethod("PrepareRecipe",BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance,null,new[]{typeof(double)},null)?.DeclaringType==__originalMethod.DeclaringType)
            {UtilityRecipes.TryGetValue(__instance,out __state);UtilityRecipes.Remove(__instance);}
        }
        static void UtilityRecipePostfix(BaseConverter __instance,ConversionRecipe __result,MethodBase __originalMethod,UtilityRecipeSample __state)
        {
            try
            {
                if(Current==null || Current.ContextKey!=recipeContext || __instance==null || !__instance.IsActivated || __instance.vessel==null || !__instance.vessel.loaded || __result==null || __result.Inputs==null || __result.Inputs.Count>64 || __instance.GetType().GetMethod("PrepareRecipe",BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance,null,new[]{typeof(double)},null)?.DeclaringType!=__originalMethod.DeclaringType)return;
                if(UtilityRecipes.Count>=512 && !UtilityRecipes.ContainsKey(__instance))return;
                if(__result.Inputs.Any(r=>string.IsNullOrWhiteSpace(r.ResourceName) || !Finite(r.Ratio) || r.Ratio<0) ||
                    __result.Outputs==null||__result.Outputs.Count>64||__result.Outputs.Any(r=>string.IsNullOrWhiteSpace(r.ResourceName)||!Finite(r.Ratio)||r.Ratio<0)||
                    __result.Requirements==null || __result.Requirements.Count>64 || __result.Requirements.Any(r=>string.IsNullOrWhiteSpace(r.ResourceName) || !Finite(r.Ratio)))return;
                var prepared=new UtilityRecipeSample {Module=__instance,Context=recipeContext,Ut=Planetarium.GetUniversalTime(),InputEc=__result.Inputs.Where(r=>r.ResourceName=="ElectricCharge").Sum(r=>r.Ratio),Inputs=__result.Inputs.ToArray(),Outputs=__result.Outputs.ToArray(),Requirements=__result.Requirements.ToArray(),SwapOption=ReviewedPrivateField(__instance,"_swapOption"),FillAmount=__instance.FillAmount,TakeAmount=__instance.TakeAmount};
                if(__state!=null&&__state.Module==prepared.Module&&__state.Context==prepared.Context&&
                    ReferenceEquals(__state.SwapOption,prepared.SwapOption)&&__state.FillAmount==prepared.FillAmount&&__state.TakeAmount==prepared.TakeAmount&&
                    __state.Inputs.SequenceEqual(prepared.Inputs)&&__state.Outputs.SequenceEqual(prepared.Outputs)&&__state.Requirements.SequenceEqual(prepared.Requirements))
                {prepared.ProcessSeen=__state.ProcessSeen;prepared.ProcessReturned=__state.ProcessReturned;prepared.ProcessUt=__state.ProcessUt;prepared.ProcessTimeFactor=__state.ProcessTimeFactor;prepared.ProcessReason=__state.ProcessReason;}
                // Stock/BRP can prepare a recipe only to probe it. Identical
                // successful probes may retain a fresh completed native pair,
                // but never renew its timestamp or claim delivered output.
                if(__state!=null&&__state.MultiplierObserved&&__state.Module==prepared.Module&&__state.Context==prepared.Context&&
                    Finite(prepared.Ut)&&Finite(__state.Ut)&&prepared.Ut>=__state.Ut&&prepared.Ut-__state.Ut<=10&&__state.MultiplierUt==__state.Ut&&
                    ReferenceEquals(__state.SwapOption,prepared.SwapOption)&&__state.FillAmount==prepared.FillAmount&&__state.TakeAmount==prepared.TakeAmount&&
                    __state.Inputs.SequenceEqual(prepared.Inputs)&&__state.Outputs.SequenceEqual(prepared.Outputs)&&__state.Requirements.SequenceEqual(prepared.Requirements)&&
                    ReviewedPrivateField(__instance,"_preCalculateEfficiency") is bool pre&&__state.NativeMultiplier==(pre?1:(double)(float)__instance.GetEfficiencyMultiplier()))
                    prepared=__state;
                UtilityRecipes[__instance]=prepared;
            }
            catch(Exception ex)
            {
                string warning="Native final recipe observation failed: "+ex.GetType().Name+": "+Bound(ex.Message,160);
                if(recipeObservationWarning!=warning){recipeObservationWarning=warning;Debug.LogWarning("[ExpanseColonyUtilityRecipe] "+warning);}
            }
        }
        static void UtilityProcessPrefix(double deltaTime,ConversionRecipe recipe,Part resPart,PartModule resModule,float efficiencyBonus,out UtilityRecipeSample __state)
        {
            __state=null;var module=resModule as BaseConverter;UtilityRecipeSample sample;
            if(module==null||!UtilityRecipes.TryGetValue(module,out sample))return;
            sample.MultiplierObserved=false;
            sample.ProcessSeen=true;sample.ProcessReturned=false;sample.ProcessUt=Planetarium.GetUniversalTime();sample.ProcessTimeFactor=double.NaN;
            if(Current==null||Current.ContextKey!=sample.Context||sample.Context!=recipeContext||module.part!=resPart||!module.IsActivated||
                module.vessel==null||!module.vessel.loaded){sample.ProcessReason="prefix-context/part/activation/loaded-rejected";return;}
            if(!Finite(deltaTime)||deltaTime<=0||deltaTime>21600||!Finite(efficiencyBonus)||efficiencyBonus<0||efficiencyBonus>10000)
                {sample.ProcessReason="prefix-dt/multiplier-rejected";return;}
            if(recipe==null||recipe.Inputs==null||recipe.Outputs==null||recipe.Requirements==null||!sample.Inputs.SequenceEqual(recipe.Inputs)||!sample.Outputs.SequenceEqual(recipe.Outputs)||!sample.Requirements.SequenceEqual(recipe.Requirements))
                {sample.ProcessReason="prefix-recipe-rejected";return;}
            sample.ProcessReason="awaiting-return";
            sample.NativeMultiplier=efficiencyBonus;sample.Ut=sample.MultiplierUt=Planetarium.GetUniversalTime();sample.FillAmount=module.FillAmount;sample.TakeAmount=module.TakeAmount;__state=sample;
        }
        static void UtilityProcessPostfix(ConverterResults __result,UtilityRecipeSample __state)
        {
            UtilityRecipeSample current;
            if(__state==null)return;
            __state.ProcessReturned=true;__state.ProcessTimeFactor=__result.TimeFactor;
            if(Current==null||Current.ContextKey!=__state.Context||!UtilityRecipes.TryGetValue(__state.Module,out current)||!ReferenceEquals(current,__state))
                {__state.ProcessReason="postfix-context/sample-rejected";return;}
            if(!Finite(__result.TimeFactor)||__result.TimeFactor<=0){__state.ProcessReason="postfix-TimeFactor-rejected";return;}
            __state.MultiplierObserved=true;__state.ProcessReason="completed-positive-TimeFactor";
        }
        static string FixedGeneratorPairDiagnostic(UtilityRecipeSample sample,double ut)
        {
            if(sample==null)return "sample=false; now="+ut.ToString("R",CultureInfo.InvariantCulture);
            return "sample=true; observed="+sample.MultiplierObserved+"; now="+ut.ToString("R",CultureInfo.InvariantCulture)+
                "; ut="+sample.Ut.ToString("R",CultureInfo.InvariantCulture)+"; multiplierUt="+sample.MultiplierUt.ToString("R",CultureInfo.InvariantCulture)+
                "; nativeMultiplier="+sample.NativeMultiplier.ToString("R",CultureInfo.InvariantCulture)+"; processSeen="+sample.ProcessSeen+"; processReturned="+sample.ProcessReturned+
                "; processUt="+sample.ProcessUt.ToString("R",CultureInfo.InvariantCulture)+"; TimeFactor="+sample.ProcessTimeFactor.ToString("R",CultureInfo.InvariantCulture)+"; processReason="+Bound(sample.ProcessReason,64);
        }
        internal static bool TryReadNativeRecipeInputs(BaseConverter module,string context,double ut,out ResourceRatio[] inputs,out string reason)
        {
            inputs=new ResourceRatio[0];reason="Current native final converter recipe has not yet been observed.";
            EnsureRecipeObserver(context);
            if(recipeFailure.Length>0){reason=recipeFailure;return false;}
            // Stock DirtyFlag is the previous activation status, not a recipe
            // configuration dirty bit. UpdateConverterStatus sets it equal to
            // IsActivated. Only an unsynchronized activation transition holds.
            if(module==null || !module.IsActivated || module.DirtyFlag!=module.IsActivated || !UtilityRecipes.TryGetValue(module,out UtilityRecipeSample sample) || sample.Module!=module || sample.Context!=context || ut<sample.Ut || ut-sample.Ut>10 || !ReferenceEquals(sample.SwapOption,ReviewedPrivateField(module,"_swapOption")))return false;
            // ResourceRatio is an installed stock value type; a copied array
            // detaches every resource/ratio/flow row from mutable native recipe lists.
            inputs=(ResourceRatio[])sample.Inputs.Clone();reason="Actual current final native recipe observed after its most-derived PrepareRecipe return.";return true;
        }
        static bool TryCurveMaximum(FloatCurve curve,out double maximum)
        {
            maximum=0;if(curve?.Curve==null)return false;
            if(curve.Curve.preWrapMode!=WrapMode.ClampForever && curve.Curve.preWrapMode!=WrapMode.Clamp && curve.Curve.preWrapMode!=WrapMode.Default || curve.Curve.postWrapMode!=WrapMode.ClampForever && curve.Curve.postWrapMode!=WrapMode.Clamp && curve.Curve.postWrapMode!=WrapMode.Default)return false;
            var points=new List<ColonyUtilityCurvePoint>();
            foreach(var key in curve.Curve.keys)
            {
                var property=key.GetType().GetProperty("weightedMode");if(property!=null && Convert.ToInt32(property.GetValue(key,null),CultureInfo.InvariantCulture)!=0)return false;
                points.Add(new ColonyUtilityCurvePoint {Time=key.time,Value=key.value,InTangent=key.inTangent,OutTangent=key.outTangent});
            }
            return ColonyUtilityCurveBounds.TryMaximum(points,out maximum) && Finite(maximum) && maximum>=0;
        }
        static bool ReadConverterDemandBound(BaseConverter module,string context,double ut,out double demand,out string reason)
        {
            demand=0;reason="Current native final converter recipe has not yet been observed.";
            ResourceRatio[] ignored;if(!TryReadNativeRecipeInputs(module,context,ut,out ignored,out reason))return false;
            if(!UtilityRecipes.TryGetValue(module,out UtilityRecipeSample sample) || sample.Context!=context || sample.Module!=module || ut<sample.Ut || ut-sample.Ut>10)return false;
            // The final observed recipe is used exactly once. Native stock
            // applies efficiency after PrepareRecipe returns; that multiplier
            // is not part of INPUT_RESOURCE/ConversionRecipe ratios.
            if(module.GetType().GetMethod("GetEfficiencyMultiplier")?.DeclaringType!=typeof(BaseConverter) || module.GetType().GetMethod("GetCrewEfficiencyBonus")?.DeclaringType!=typeof(BaseConverter)){reason="Converter overrides the reviewed native efficiency multiplier.";return false;}
            var heat=module.GetType().GetMethod("GetHeatThrottle");double thermalMaximum;
            FloatCurve thermalCurve=module.ThermalEfficiency;
            if(heat?.DeclaringType!=typeof(BaseConverter))
            {
                if(!SupportedSystemHeat(module) || heat==null || heat.DeclaringType.FullName!="SystemHeat.ModuleSystemHeatConverter" && heat.DeclaringType.FullName!="SystemHeat.ModuleSystemHeatHarvester"){reason="Converter thermal efficiency bound requires its installed provider.";return false;}
                thermalCurve=UtilityRead(module,"systemEfficiency") as FloatCurve;
            }
            if(!TryCurveMaximum(thermalCurve,out thermalMaximum)){reason="Actual converter thermal curve is not bounded.";return false;}
            double modifiers=Convert.ToDouble(ReviewedPrivateField(module,"_totalEfficiencyModifiers") ?? double.NaN,CultureInfo.InvariantCulture);
            double bonus=module.EfficiencyBonus,crew=1;
            if(module.UseSpecialistBonus)
            {
                if(module.vessel.GetVesselCrew().Any(c=>c==null || c.experienceLevel<0 || c.experienceLevel>5)){reason="Actual converter specialist roster exceeds the reviewed stock level bound.";return false;}
                crew=module.SpecialistBonusBase+6*module.SpecialistEfficiencyFactor;
            }
            if(!Finite(modifiers) || modifiers<0 || modifiers>10000 || !Finite(bonus) || bonus<0 || bonus>10000 || !Finite(crew) || crew<0 || crew>10000){reason="Actual converter efficiency modifiers exceed bounded native terms.";return false;}
            var preCalculate=ReviewedPrivateField(module,"_preCalculateEfficiency");if(!(preCalculate is bool)){reason="Native recipe multiplier boundary is unavailable.";return false;}
            demand=sample.InputEc*((bool)preCalculate ? 1 : Math.Max(1,thermalMaximum)*bonus*modifiers*crew);
            if(!Finite(demand) || demand<0 || demand>1e9){reason="Actual converter EC demand bound is invalid.";return false;}
            reason="Observed final native recipe EC="+sample.InputEc.ToString("R",CultureInfo.InvariantCulture)+" × conservative native thermal/efficiency/specialist bound; qualified physical staffing is evaluated separately.";return true;
        }
    }
}
