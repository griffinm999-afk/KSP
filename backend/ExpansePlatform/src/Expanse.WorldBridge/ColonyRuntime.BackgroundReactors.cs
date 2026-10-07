using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using Expanse.Domain.Colonies;
using UnityEngine;
using HarmonyLib;

namespace Expanse.WorldBridge
{
    public sealed partial class ColonyRuntime
    {
        sealed class ReactorProofObservation
        {
            public string Context,Seal;
            public double ObservedUt;
            public bool Invalidated,ObservedLoaded;
            public readonly Dictionary<string,ColonyReactorStabilityWindow> Windows=new Dictionary<string,ColonyReactorStabilityWindow>(StringComparer.Ordinal);
            public ColonyReactorContinuationProof Proof;
        }
        static readonly Dictionary<string,ReactorProofObservation> ReactorObservations=new Dictionary<string,ReactorProofObservation>(StringComparer.Ordinal);
        static readonly Dictionary<AvailablePart,string> ReactorPartConfigHashes=new Dictionary<AvailablePart,string>();
        sealed class NativeHeatTick {public string Context;public double Ut;public long Tick;}
        static readonly Dictionary<object,NativeHeatTick> NativeHeatTicks=new Dictionary<object,NativeHeatTick>();
        static Harmony ReactorObserverPatch;
        static string ReactorObserverContext="";
        static bool EnsureReactorObservers()
        {
            if(Current==null)return false;
            if(ReactorObserverContext!=Current.ContextKey){NativeHeatTicks.Clear();NonActiveHeatTicks.Clear();ReactorObservations.Clear();ReactorObserverContext=Current.ContextKey;}
            if(ReactorObserverPatch!=null)return true;
            Harmony candidate=null;
            try
            {
                var assembly=AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(a=>a.GetName().Name=="SystemHeat" && a.GetName().Version==new Version(0,9,1,0));
                if(assembly==null)return false;
                var loop=assembly.GetType("SystemHeat.HeatLoop").GetMethod("Simulate",BindingFlags.Instance|BindingFlags.Public,null,new[]{typeof(float)},null);
                var radiator=assembly.GetType("SystemHeat.ModuleSystemHeatRadiator").GetMethod("FixedUpdate",BindingFlags.Instance|BindingFlags.Public,null,Type.EmptyTypes,null);
                if(loop==null || radiator==null)return false;
                var patch=new Harmony("Expanse.Colony.NativeReactorHeatWitness.v1");candidate=patch;
                var postfix=new HarmonyMethod(typeof(ColonyRuntime).GetMethod(nameof(ObserveNativeHeatTick),BindingFlags.Static|BindingFlags.NonPublic));
                patch.Patch(loop,postfix:postfix);patch.Patch(radiator,postfix:postfix);RegisterNonActiveSystemHeat(patch,assembly);ReactorObserverPatch=patch;return true;
            }
            catch{if(candidate!=null)try{candidate.UnpatchAll(candidate.Id);}catch{}StopNonActiveSystemHeat();return false;}
        }
        private static void StopReactorObservers()
        {
            if(ReactorObserverPatch!=null)try{ReactorObserverPatch.UnpatchAll(ReactorObserverPatch.Id);}catch{}
            ReactorObserverPatch=null;ReactorObserverContext="";NativeHeatTicks.Clear();StopNonActiveSystemHeat();ReactorObservations.Clear();ReactorPartConfigHashes.Clear();
        }
        // Passive final native callbacks establish that repeated equal fields
        // really came from advancing simulation, including anchored-packed.
        static void ObserveNativeHeatTick(object __instance)
        {
            try
            {
            if(Current==null || ReactorObserverContext!=Current.ContextKey || !HighLogic.LoadedSceneIsFlight || TimeWarp.CurrentRate>1 || FlightDriver.Pause || Time.timeScale<=0)return;
            var module=__instance as PartModule;
            if(module!=null && (module.vessel==null || !module.vessel.loaded || !UtilityBool(module,"moduleIsEnabled") || !UtilityBool(module,"IsCooling") ||
                !(ReviewedPrivateField(module,"heatModule") is PartModule heat) || UtilityRead(heat,"Loop")==null || !Finite(Convert.ToDouble(ReviewedPrivateField(module,"radiativeFlux"),CultureInfo.InvariantCulture))))return;
            if(module==null)
            {
                var members=UtilityRead(__instance,"LoopModules") as IEnumerable;
                if(members==null || !members.Cast<object>().Any(m=>m is PartModule p && p.vessel!=null && p.vessel.loaded) || !PositiveFinite(UtilityNumber(__instance,"Temperature",double.NaN)))return;
            }
            NativeHeatTick row;if(!NativeHeatTicks.TryGetValue(__instance,out row)){if(NativeHeatTicks.Count>=4096)NativeHeatTicks.Clear();row=new NativeHeatTick();NativeHeatTicks[__instance]=row;}
            row.Context=Current.ContextKey;row.Ut=Planetarium.GetUniversalTime();row.Tick++;
            }
            catch { /* A passive observer must never interrupt native simulation. */ }
        }
        static bool HasFreshNativeHeatTick(object instance,double ut)
        {
            NativeHeatTick row;return Current!=null && instance!=null && NativeHeatTicks.TryGetValue(instance,out row) && row.Context==Current.ContextKey && ut>=row.Ut && ut-row.Ut<=1 && row.Tick>0;
        }
        static string HashReactorText(string value)=>ColonyStateCodec.Hash(Encoding.UTF8.GetBytes(value));
        static bool IsReviewedPassiveReactorUtilityModule(PartModule module)=>SupportedSystemHeat(module) &&
            (module.GetType().Name=="ModuleSystemHeatFissionFuelContainer" || module.GetType().Name=="ModuleSystemHeatColorAnimator");
        static string ReadReactorProviderHash()
        {
            if(!RemoteBrpInventoryGateway.SupportedProviderAvailable || GameDatabase.Instance==null)throw new InvalidOperationException("Exact supported BRP provider/config is absent.");
            var configs=GameDatabase.Instance.GetConfigNodes("BACKGROUND_CONVERTER").Where(n=>n.GetValue("name")=="ModuleSystemHeatFissionReactor" || n.GetValue("name")=="ModuleSystemHeatRadiator").ToArray();
            var reactor=configs.SingleOrDefault(n=>n.GetValue("name")=="ModuleSystemHeatFissionReactor");var radiator=configs.SingleOrDefault(n=>n.GetValue("name")=="ModuleSystemHeatRadiator");
            var branches=reactor==null ? new ConfigNode[0] : reactor.GetNodes("BACKGROUND_CONVERTER");var manual=branches.FirstOrDefault();
            if(reactor==null || reactor.GetValue("adapter")!="SelectFirst" || reactor.GetValue("ActiveCondition")!="%Enabled" || manual==null ||
                manual.GetValue("condition")!="%ManualControl" || manual.GetValue("adapter")!="BackgroundConstantConverter" || manual.GetValue("InputList")!="%inputs" || manual.GetValue("OutputList")!="%outputs" ||
                manual.GetValue("LastUpdateField")!="LastUpdateTime" || !manual.GetNodes("MULTIPLIER").Any(n=>n.GetValue("Value")=="%CurrentThrottle * 0.01") ||
                !manual.GetNodes("OUTPUT_RESOURCE").Any(n=>n.GetValue("ResourceName")=="ElectricCharge" && n.GetValue("DumpExcess")=="True" && n.GetValue("FlowMode")=="ALL_VESSEL") ||
                radiator==null || radiator.GetValue("adapter")!="BackgroundGenericConverter" || radiator.GetValue("ActiveCondition")!="%IsCooling")
                throw new InvalidOperationException("Installed reactor/radiator background adapter differs from the reviewed full-manual recipe.");
            return HashReactorText("SystemHeat/0.9.1.0|BRP/0.2.7.0|"+string.Join("|",configs.OrderBy(n=>n.GetValue("name"),StringComparer.Ordinal).Select(n=>n.ToString())));
        }
        static string PartConfigHash(AvailablePart part)
        {
            if(part==null || part.partConfig==null)throw new InvalidOperationException("Installed reactor package part config is absent.");
            string hash;if(!ReactorPartConfigHashes.TryGetValue(part,out hash))
            {if(ReactorPartConfigHashes.Count>=4096)ReactorPartConfigHashes.Clear();hash=HashReactorText(part.partConfig.ToString());ReactorPartConfigHashes.Add(part,hash);}return hash;
        }
        static readonly string[] ReactorSealFields={"Enabled","ManualControl","HibernateOnWarp","Hibernating","CurrentReactorThrottle","CurrentThrottle","CurrentSafetyOverride","CoreIntegrity"};
        static string ModuleSealField(PartModule module,ConfigNode saved,string name)
        {
            object live=UtilityRead(module,name);string text=saved==null ? null : saved.GetValue(name);
            if(saved!=null && text==null)throw new InvalidOperationException("Saved native reactor setting "+name+" is absent.");
            if(saved==null && live==null)throw new InvalidOperationException("Loaded native reactor setting "+name+" is absent.");
            if(live is bool)return saved==null ? live.ToString() : bool.Parse(text).ToString();
            if(live is float)return (saved==null ? (float)live : float.Parse(text,CultureInfo.InvariantCulture)).ToString("R",CultureInfo.InvariantCulture);
            if(live is double)return (saved==null ? (double)live : double.Parse(text,CultureInfo.InvariantCulture)).ToString("R",CultureInfo.InvariantCulture);
            if(live is int || live is uint)return saved==null ? Convert.ToString(live,CultureInfo.InvariantCulture) : long.Parse(text,CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
            return saved==null ? Convert.ToString(live,CultureInfo.InvariantCulture) : text;
        }
        static string ReadReactorHardwareHash(Vessel vessel)
        {
            var rows=new List<string>{vessel.id.ToString("D"),vessel.mainBody==null ? "" : vessel.mainBody.bodyName};
            Action<uint,uint,AvailablePart,IEnumerable<PartModule>,IEnumerable<ProtoPartModuleSnapshot>,IEnumerable<string>> append=(pid,flight,info,loaded,saved,resources)=>
            {
                rows.Add("part:"+pid+":"+flight+":"+info.name+":"+PartConfigHash(info));rows.AddRange(resources.OrderBy(x=>x,StringComparer.Ordinal));
                var live=loaded.ToArray();var proto=saved==null ? null : saved.ToArray();
                for(int i=0;i<(proto==null ? live.Length : proto.Length);i++)
                {
                    // Placement appends its own marker module. It has no thermal
                    // behavior and no installed prefab; saved thermal identities
                    // still must retain the exact installed module position.
                    if(proto!=null && i>=live.Length && (proto[i].moduleName=="ExpanseColonyPlacementMarker" || proto[i].moduleName=="ColonyPlacementMarker"))continue;
                    if(i>=live.Length)throw new InvalidOperationException("Saved package has an unknown added module.");
                    var module=live[i];var node=proto==null ? null : proto[i].moduleValues;
                    if(module==null || proto!=null && proto[i].moduleName!=module.moduleName)throw new InvalidOperationException("Saved/native module ordering changed.");
                    bool relevant=module.moduleName=="ModuleSystemHeatFissionReactor" || module.moduleName=="ModuleSystemHeatRadiator" || module.moduleName=="ModuleSystemHeat" || module is ModuleDeployableRadiator;
                    if(relevant)
                    {
                        uint mid=node==null ? NativeModuleId(module) : uint.Parse(node.GetValue("persistentId"),CultureInfo.InvariantCulture);if(mid==0)throw new InvalidOperationException("Saved thermal module identity is absent.");
                        rows.Add("module:"+pid+":"+i+":"+mid+":"+module.moduleName+":enabled="+(node==null ? UtilityBool(module,"moduleIsEnabled").ToString() : bool.Parse(node.GetValue("isEnabled")).ToString()));
                        if(module.moduleName=="ModuleSystemHeatFissionReactor")foreach(string field in ReactorSealFields)rows.Add(field+"="+ModuleSealField(module,node,field));
                        if(module.moduleName=="ModuleSystemHeatRadiator")rows.Add("IsCooling="+ModuleSealField(module,node,"IsCooling"));
                        if(module.moduleName=="ModuleSystemHeat")rows.Add("currentLoopID="+ModuleSealField(module,node,"currentLoopID"));
                        if(module is ModuleDeployableRadiator)rows.Add("deployState="+(node==null ? ((ModuleDeployableRadiator)module).deployState.ToString() : node.GetValue("deployState")));
                    }
                    // B9 persists the stable subtype name, not its UI index.
                    // Seal the same native setting before and after stock save.
                    if(module.moduleName=="ModuleB9PartSwitch")rows.Add("variant:"+pid+":"+i+":currentSubtype="+ReactorB9SubtypeSeal(module,node,info));
                    foreach(string field in module.moduleName=="ModuleB9PartSwitch" ? new[]{"selectedVariant"} : new[]{"currentSubtypeIndex","selectedVariant"})
                        if(UtilityRead(module,field)!=null || node!=null && node.HasValue(field))rows.Add("variant:"+pid+":"+i+":"+field+"="+(node==null ? Convert.ToString(UtilityRead(module,field),CultureInfo.InvariantCulture) : node.GetValue(field)));
                }
            };
            if(vessel.loaded)
            foreach(var part in vessel.parts.OrderBy(p=>p.persistentId))append(part.persistentId,part.flightID,part.partInfo,part.Modules.Cast<PartModule>(),null,
                part.Resources.Cast<PartResource>().Select(r=>"tank:"+part.persistentId+":"+r.resourceName+":"+r.maxAmount.ToString("R",CultureInfo.InvariantCulture)+":"+r.flowState));
            else
            foreach(var part in vessel.protoVessel.protoPartSnapshots.OrderBy(p=>p.persistentId))append(part.persistentId,part.flightID,part.partInfo,part.partInfo.partPrefab.Modules.Cast<PartModule>(),part.modules,
                part.resources.Select(r=>"tank:"+part.persistentId+":"+r.resourceName+":"+r.maxAmount.ToString("R",CultureInfo.InvariantCulture)+":"+r.flowState));
            return HashReactorText(string.Join("|",rows));
        }
        static string ReactorB9SubtypeSeal(PartModule module,ConfigNode saved,AvailablePart info)
        {
            if(UtilityModuleIdentity(module)!="B9PartSwitch.ModuleB9PartSwitch|B9PartSwitch|2.21.0.4" || info==null || info.partConfig==null)
                throw new InvalidOperationException("Exact native B9 subtype implementation/configuration is unavailable.");
            string id=UtilityRead(module,"moduleID") as string;
            if(string.IsNullOrEmpty(id) || saved!=null && saved.GetValue("moduleID")!=id)
                throw new InvalidOperationException("Native/saved B9 switch identity differs or is absent.");
            var configs=info.partConfig.GetNodes("MODULE").Where(n=>n.GetValue("name")=="ModuleB9PartSwitch" && n.GetValue("moduleID")==id).Take(2).ToArray();
            if(configs.Length!=1)throw new InvalidOperationException("Native B9 switch lacks one exact configured owner.");
            var names=configs[0].GetNodes("SUBTYPE").Select(n=>n.GetValue("name")).ToArray();
            string selected=saved==null ? UtilityRead(module,"CurrentSubtypeName") as string : saved.GetValue("currentSubtype");
            if(names.Length==0 || names.Length>128 || names.Any(string.IsNullOrEmpty) || names.Distinct(StringComparer.Ordinal).Count()!=names.Length ||
                string.IsNullOrEmpty(selected) || !names.Contains(selected,StringComparer.Ordinal))
                throw new InvalidOperationException("Native/saved B9 subtype is missing, unknown or ambiguous in the current configuration.");
            if(saved==null && (!(UtilityRead(module,"currentSubtypeIndex") is int index) || index<0 || index>=names.Length || names[index]!=selected))
                throw new InvalidOperationException("Loaded native B9 subtype name/index is contradictory.");
            return selected;
        }
        static string RecipeRow(object row)
        {
            string name=Convert.ToString(UtilityRead(row,"ResourceName"),CultureInfo.InvariantCulture);double rate=UtilityNumber(row,"Ratio",double.NaN);
            var flow=UtilityRead(row,"FlowMode");if(string.IsNullOrEmpty(name) || !PositiveFinite(rate) || flow==null)throw new InvalidOperationException("Native reactor resource recipe is invalid.");
            return name+"="+rate.ToString("R",CultureInfo.InvariantCulture)+":"+flow;
        }
        static string ReadLoadedReactorRecipeHash(Vessel vessel)
        {
            var rows=new List<string>();
            foreach(var module in vessel.parts.SelectMany(p=>p.Modules.Cast<PartModule>()).Where(m=>m.moduleName=="ModuleSystemHeatFissionReactor").OrderBy(m=>m.part.flightID).ThenBy(NativeModuleId))
            {
                string key=module.part.flightID+":"+NativeModuleId(module)+":";
                rows.AddRange(((IEnumerable)ReviewedPrivateField(module,"inputs")).Cast<object>().Select(r=>key+"in:"+RecipeRow(r)).OrderBy(x=>x,StringComparer.Ordinal));
                rows.AddRange(((IEnumerable)ReviewedPrivateField(module,"outputs")).Cast<object>().Select(r=>key+"out:"+RecipeRow(r)).OrderBy(x=>x,StringComparer.Ordinal));
                rows.Add(key+"out:ElectricCharge="+((FloatCurve)UtilityRead(module,"ElectricalGeneration")).Evaluate(100).ToString("R",CultureInfo.InvariantCulture)+":"+ResourceFlowMode.ALL_VESSEL);
            }
            return HashReactorText(string.Join("|",rows));
        }
        static void RecordLoadedReactorContinuation(Vessel vessel,double ut,ReactorUtilityWitness witness)
        {
            if(Current==null || Current.state==null)return;
            string id=vessel.id.ToString("D");ReactorProofObservation observed;
            if(!ReactorObservations.TryGetValue(id,out observed) || observed.Context!=Current.ContextKey)
            {if(ReactorObservations.Count>=256)ReactorObservations.Clear();observed=new ReactorProofObservation {Context=Current.ContextKey};ReactorObservations[id]=observed;}
            try
            {
                observed.ObservedUt=ut;observed.ObservedLoaded=vessel.loaded;
                string hardware=ReadReactorHardwareHash(vessel),provider=ReadReactorProviderHash(),recipe=ReadLoadedReactorRecipeHash(vessel),seal=hardware+provider+recipe;
                if(observed.Seal!=seal){observed.Windows.Clear();observed.Proof=null;observed.Seal=seal;}
                bool fullPower=witness.FullPower && TimeWarp.CurrentRate<=1 && !FlightDriver.Pause && Time.timeScale>0,stable=fullPower;
                foreach(var sample in witness.ThermalSamples)
                {
                    ColonyReactorStabilityWindow window;if(!observed.Windows.TryGetValue(sample.Item1,out window)){window=new ColonyReactorStabilityWindow();observed.Windows.Add(sample.Item1,window);}
                    bool ready=window.Observe(seal,ut,sample.Item2,sample.Item3,fullPower);stable&=ready;
                }
                witness.Evidence+=" Continuation fullPower="+fullPower+"; stable="+stable+"; heat/rejection="+witness.Heat.ToString("R",CultureInfo.InvariantCulture)+"/"+witness.Rejection.ToString("R",CultureInfo.InvariantCulture)+" kW; core/loop margin="+witness.Margin.ToString("R",CultureInfo.InvariantCulture)+"/"+witness.LoopMargin.ToString("R",CultureInfo.InvariantCulture)+" K; window seconds="+(observed.Windows.Count==0 ? "none" : observed.Windows.Values.Min(w=>w.Seconds).ToString("R",CultureInfo.InvariantCulture))+"; max core/loop rise="+(observed.Windows.Count==0 ? "none" : observed.Windows.Values.Max(w=>w.MaximumCoreRise).ToString("R",CultureInfo.InvariantCulture)+"/"+observed.Windows.Values.Max(w=>w.MaximumLoopRise).ToString("R",CultureInfo.InvariantCulture))+" K/s.";
                // A failed sample invalidates an earlier in-memory proof; a saved
                // proof remains usable only after its sealed current-state checks.
                if(!stable){observed.Proof=null;observed.Invalidated=true;return;}
                double coreRise=observed.Windows.Values.Max(w=>w.MaximumCoreRise),loopRise=observed.Windows.Values.Max(w=>w.MaximumLoopRise);
                double coreHorizon=coreRise>0 ? (witness.Margin-100)/coreRise : double.MaxValue,loopHorizon=loopRise>0 ? witness.LoopMargin/loopRise : double.MaxValue;
                double horizon=Math.Min(Math.Min(witness.Endurance,Math.Max(2*RequiredUtilityEndurance(vessel),7*ColonyLimits.KerbinDay)),Math.Min(coreHorizon,loopHorizon));
                horizon=Math.Floor(Math.Min(horizon,365*ColonyLimits.KerbinDay));
                witness.Evidence+=" Continuation horizon="+horizon.ToString("R",CultureInfo.InvariantCulture)+" s; required="+RequiredUtilityEndurance(vessel).ToString("R",CultureInfo.InvariantCulture)+" s.";
                if(!PositiveFinite(horizon) || horizon<RequiredUtilityEndurance(vessel) || !Finite(ut+horizon))
                {
                    observed.Proof=null;observed.Invalidated=true;
                    // A completed window may retain a warm-up slope forever.
                    // Discard that failed window; subsequent native samples must
                    // establish a full fresh 30 seconds before another proof.
                    observed.Windows.Clear();
                    witness.Evidence+=" Completed insufficient-horizon window discarded; awaiting a fresh full 30-second native observation.";
                    return;
                }
                var proof=new ColonyReactorContinuationProof {WorldId=Current.state.WorldId,VesselId=id,HardwareHash=hardware,ProviderHash=provider,RecipeHash=recipe,
                    ObservedUt=ut,ExpiresUt=ut+horizon,FullThrottleSeconds=observed.Windows.Values.Min(w=>w.Seconds),GenerationEcPerSecond=witness.Generation,FullLoadHeatKw=witness.Heat,
                    NominalCoolingKw=witness.Cooling,ObservedRejectionKw=witness.Rejection,MinimumShutdownMarginK=witness.Margin,FuelWasteEnduranceSeconds=witness.Endurance,
                    MaximumCoreRiseKelvinPerSecond=coreRise,MaximumLoopRiseKelvinPerSecond=loopRise,MinimumLoopOperatingMarginK=witness.LoopMargin};
                ColonyStateCodec.ValidateReactorContinuation(proof);observed.Proof=proof;observed.Invalidated=false;witness.Proof=proof;
            }
            catch(Exception ex){observed.Proof=null;observed.Invalidated=true;observed.ObservedUt=ut;witness.Evidence+=" Loaded background continuation awaits: "+Bound(ex.Message,256);}
        }
        static ColonyReactorContinuationProof LoadedReactorProof(Vessel vessel)
        {
            ReactorProofObservation row;return Current!=null && ReactorObservations.TryGetValue(vessel.id.ToString("D"),out row) && row.Context==Current.ContextKey ? row.Proof : null;
        }
        static void InvalidateLoadedReactorProof(Vessel vessel,double ut)
        {
            if(vessel==null || !vessel.loaded || Current==null || !Finite(ut))return;
            string id=vessel.id.ToString("D");ReactorProofObservation row;
            if(!ReactorObservations.TryGetValue(id,out row) || row.Context!=Current.ContextKey)
            {if(ReactorObservations.Count>=256)ReactorObservations.Clear();row=new ReactorProofObservation {Context=Current.ContextKey};ReactorObservations[id]=row;}
            row.Proof=null;row.Windows.Clear();row.Invalidated=true;row.ObservedUt=ut;row.ObservedLoaded=true;
        }
        private void RefreshLoadedReactorContinuations(bool revocationsOnly=false)
        {
            if(!Ready || mutating)return;
            bool mayGrant=!revocationsOnly && !state.Effects.Any(e=>e.State=="held" || e.State=="applying");
            ColonyState next=null;
            foreach(var facility in state.Colonies.SelectMany(c=>c.Facilities))
            {
                ReactorProofObservation observed;if(!ReactorObservations.TryGetValue(facility.VesselId,out observed) || observed.Context!=ContextKey)continue;
                double ut=Planetarium.GetUniversalTime();
                if(ColonyReactorContinuation.ShouldRevokeProof(ContextKey,observed.Context,observed.ObservedLoaded,observed.Invalidated,ut,observed.ObservedUt,revocationsOnly) && facility.Qualification.ReactorContinuation!=null)
                {if(next==null)next=GetStateCopy();next.Colonies.SelectMany(c=>c.Facilities).Single(f=>f.Id==facility.Id).Qualification.ReactorContinuation=null;continue;}
                var proof=observed.Proof;if(!mayGrant || proof==null)continue;
                if(ut<proof.ObservedUt || ut-proof.ObservedUt>10 || facility.Qualification.ReactorContinuation!=null && proof.ObservedUt-facility.Qualification.ReactorContinuation.ObservedUt<60)continue;
                if(next==null)next=GetStateCopy();next.Colonies.SelectMany(c=>c.Facilities).Single(f=>f.Id==facility.Id).Qualification.ReactorContinuation=proof;
            }
            // Detach cache-owned proof objects before accepting them. Later
            // observer updates cannot mutate selected-save authority by alias.
            if(next!=null){next.Revision++;Accept(ColonyStateCodec.Copy(next));}
        }

        static ReactorUtilityWitness ReadUnloadedReactors(Vessel vessel,string facilityId,double ut,object processor)
        {
            var witness=new ReactorUtilityWitness();
            var native=vessel.protoVessel.protoPartSnapshots.SelectMany(p=>p.modules.Where(m=>m.moduleName=="ModuleSystemHeatFissionReactor").Select(m=>Tuple.Create(p,m))).Take(17).ToArray();
            witness.Present=native.Length>0;if(!witness.Present)return witness;
            try
            {
                if(native.Length>16 || processor==null || Current==null || Current.state==null)throw new InvalidOperationException("Bounded actual saved reactor/provider authority is unavailable.");
                var facility=Current.state.Colonies.SelectMany(c=>c.Facilities).SingleOrDefault(f=>f.VesselId==vessel.id.ToString("D") && (facilityId.Length==0 || f.Id==facilityId));
                var proof=facility==null ? null : facility.Qualification.ReactorContinuation;
                if(proof==null)throw new InvalidOperationException("Load the native active reactor package for a saved stable full-power observation.");
                ReactorProofObservation latest;
                if(ReactorObservations.TryGetValue(vessel.id.ToString("D"),out latest) && ColonyReactorContinuation.ShouldRevokeProof(Current.ContextKey,latest.Context,latest.ObservedLoaded,latest.Invalidated,ut,latest.ObservedUt,true))
                    throw new InvalidOperationException("Latest loaded observation invalidated this thermal continuation; unload/save cannot resurrect it. Actual stable loaded requalification is required.");
                string hardware=ReadReactorHardwareHash(vessel),provider=ReadReactorProviderHash();
                var converters=((IEnumerable)UtilityRead(processor,"Converters")).Cast<object>().Take(513).ToArray();if(converters.Length>512)throw new InvalidOperationException("BRP recipe observation bound exceeded.");
                var inventories=UtilityRead(processor,"Inventories");int count=Convert.ToInt32(UtilityRead(inventories,"Count"),CultureInfo.InvariantCulture);var item=inventories.GetType().GetProperty("Item");
                if(item==null || count<0 || count>4096)throw new InvalidOperationException("Bounded BRP physical inventory is unavailable.");
                var recipeRows=new List<string>();var rates=new Dictionary<string,double>(StringComparer.Ordinal);var stocks=new Dictionary<string,double>(StringComparer.Ordinal);
                double ecOutput=0;
                foreach(var module in native.OrderBy(m=>m.Item1.flightID).ThenBy(m=>uint.Parse(m.Item2.moduleValues.GetValue("persistentId"),CultureInfo.InvariantCulture)))
                {
                    var part=module.Item1;uint mid=uint.Parse(module.Item2.moduleValues.GetValue("persistentId"),CultureInfo.InvariantCulture);
                    var matches=converters.Where(c=>UtilityRead(c,"FlightId") is uint flight && flight==part.flightID && UtilityRead(c,"ModuleId") is uint id && id==mid).ToArray();
                    if(matches.Length!=1)throw new InvalidOperationException("Saved active reactor lacks one exact BRP part/module recipe.");
                    var converter=matches[0];double rate=UtilityNumber(converter,"Rate",double.NaN);string constraint=Convert.ToString(UtilityRead(converter,"ConstraintState"),CultureInfo.InvariantCulture);
                    if(!Finite(rate) || rate<.999 || rate>1 || constraint!="ENABLED" && constraint!="BOUNDARY")throw new InvalidOperationException("Current native BRP reactor does not sustain the reviewed full-power rate/constraints.");
                    var inputs=((IEnumerable)UtilityRead(converter,"Inputs")).Cast<object>().Select(e=>UtilityRead(e,"Value")).Take(17).ToArray();
                    var outputs=((IEnumerable)UtilityRead(converter,"Outputs")).Cast<object>().Select(e=>UtilityRead(e,"Value")).Take(18).ToArray();
                    if(inputs.Length==0 || inputs.Length>16 || outputs.Length>17)throw new InvalidOperationException("Actual full-power reactor recipe exceeds bounds.");
                    string prefix=part.flightID+":"+mid+":";
                    recipeRows.AddRange(inputs.Select(r=>prefix+"in:"+RecipeRow(r)).OrderBy(x=>x,StringComparer.Ordinal));
                    recipeRows.AddRange(outputs.Where(r=>Convert.ToString(UtilityRead(r,"ResourceName"),CultureInfo.InvariantCulture)!="ElectricCharge").Select(r=>prefix+"out:"+RecipeRow(r)).OrderBy(x=>x,StringComparer.Ordinal));
                    foreach(var output in outputs.Where(r=>Convert.ToString(UtilityRead(r,"ResourceName"),CultureInfo.InvariantCulture)=="ElectricCharge")){recipeRows.Add(prefix+"out:"+RecipeRow(output));ecOutput+=UtilityNumber(output,"Ratio",double.NaN);}
                    foreach(var row in inputs.Concat(outputs.Where(r=>Convert.ToString(UtilityRead(r,"ResourceName"),CultureInfo.InvariantCulture)!="ElectricCharge")))
                    {
                        bool output=outputs.Contains(row);string name=Convert.ToString(UtilityRead(row,"ResourceName"),CultureInfo.InvariantCulture);double ratio=UtilityNumber(row,"Ratio",double.NaN);
                        var bits=((IEnumerable)UtilityRead(converter,output ? "Push" : "Pull")).Cast<object>().Select(i=>Convert.ToInt32(i,CultureInfo.InvariantCulture)).Take(4097).ToArray();
                        if(bits.Length>4096 || bits.Any(i=>i<0 || i>=count))throw new InvalidOperationException("BRP reactor feed index exceeds bounds.");
                        var path=new List<Tuple<int,ProtoPartResourceSnapshot>>();
                        foreach(int index in bits)
                        {
                            object inventory=item.GetValue(inventories,new object[]{index});if(inventory==null || Convert.ToString(UtilityRead(inventory,"ResourceName"),CultureInfo.InvariantCulture)!=name)continue;
                            if(UtilityRead(inventory,"ModuleId")!=null)throw new InvalidOperationException("Virtual module inventory cannot certify real reactor fuel/waste.");
                            uint flight=Convert.ToUInt32(UtilityRead(inventory,"FlightId"),CultureInfo.InvariantCulture);var owner=vessel.protoVessel.protoPartSnapshots.Where(p=>p.flightID==flight).ToArray();var snapshot=UtilityRead(inventory,"Snapshot") as ProtoPartResourceSnapshot;
                            double amount=UtilityNumber(inventory,"Amount",double.NaN),capacity=UtilityNumber(inventory,"MaxAmount",double.NaN);
                            if(owner.Length!=1 || snapshot==null || !owner[0].resources.Any(r=>ReferenceEquals(r,snapshot)) || !snapshot.flowState || !Finite(amount) || !Finite(capacity) || amount<0 || amount>capacity ||
                                snapshot.amount!=amount || snapshot.maxAmount!=capacity || UtilityNumber(inventory,"OriginalAmount",double.NaN)!=amount)throw new InvalidOperationException("BRP reactor path is not exact synchronized enabled physical stock.");
                            if(!output || owner[0]==part)path.Add(Tuple.Create(index,snapshot)); // Native waste-full check is local.
                        }
                        if(path.Count==0)throw new InvalidOperationException("Native reactor lacks an owned local waste or actual fuel path.");
                        string key=(output ? "waste:" : "fuel:")+name+":"+string.Join(",",path.Select(p=>p.Item1).OrderBy(x=>x));
                        rates[key]=rates.TryGetValue(key,out double previous) ? previous+ratio : ratio;
                        stocks[key]=path.Sum(p=>output ? p.Item2.maxAmount-p.Item2.amount : p.Item2.amount);
                        // A different module consuming these scarce tanks needs
                        // its own endurance allocation; never count stock twice.
                        if(converters.Any(c=>!ReferenceEquals(c,converter) && !native.Any(n=>UtilityRead(c,"FlightId") is uint f && f==n.Item1.flightID && UtilityRead(c,"ModuleId") is uint id && id==uint.Parse(n.Item2.moduleValues.GetValue("persistentId"),CultureInfo.InvariantCulture)) &&
                            ((IEnumerable)UtilityRead(c,output ? "Outputs" : "Inputs")).Cast<object>().Any(e=>Convert.ToString(UtilityRead(UtilityRead(e,"Value"),"ResourceName"),CultureInfo.InvariantCulture)==name)))
                            throw new InvalidOperationException("Other native recipes share reactor fuel/waste; separate bounded endurance allocation is required.");
                    }
                }
                // Overlapping but unequal fuel paths would over-promise their
                // common tanks. The current sealed package uses local fuel.
                var fuelKeys=rates.Keys.Where(k=>k.StartsWith("fuel:",StringComparison.Ordinal)).ToArray();
                foreach(var a in fuelKeys)foreach(var b in fuelKeys.Where(k=>string.CompareOrdinal(k,a)>0))
                    if(a.Split(':')[1]==b.Split(':')[1] && a.Split(':')[2].Split(',').Intersect(b.Split(':')[2].Split(',')).Any())throw new InvalidOperationException("Overlapping unequal fuel paths require a shared tank allocator.");
                double endurance=rates.Min(r=>stocks[r.Key]/r.Value);string recipe=HashReactorText(string.Join("|",recipeRows));
                if(!ColonyReactorContinuation.CanContinue(proof,Current.state.WorldId,vessel.id.ToString("D"),hardware,provider,recipe,ut,endurance,RequiredUtilityEndurance(vessel),out string reason))throw new InvalidOperationException(reason);
                if(!Finite(ecOutput) || ecOutput<proof.GenerationEcPerSecond)throw new InvalidOperationException("Actual BRP output is below the reviewed continuous source.");
                witness.Generation=proof.GenerationEcPerSecond;witness.Endurance=endurance;witness.HeatQualified=witness.Qualified=true;witness.Proof=proof;
                foreach(var part in vessel.protoVessel.protoPartSnapshots)foreach(var module in part.partInfo.partPrefab.Modules.Cast<PartModule>())
                    if(module.moduleName=="ModuleSystemHeatFissionReactor" || module.moduleName=="ModuleSystemHeatRadiator" || module.moduleName=="ModuleSystemHeat")witness.Accounted.Add(module);
                witness.Reason=reason;witness.Evidence=reason+" Current actual BRP full-load fuel/waste endurance="+endurance.ToString("R",CultureInfo.InvariantCulture)+"s; no cached temperature is reported as current.";
            }
            catch(Exception ex){witness.Qualified=false;witness.HeatQualified=false;witness.Generation=0;witness.Reason="Background reactor hold: "+Bound(ex.Message,1000);}
            return witness;
        }
    }
}
