using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Expanse.Domain.Colonies;
using UnityEngine;

namespace Expanse.WorldBridge
{
    public sealed partial class ColonyRuntime
    {
        // Read-only, version-bound adapter. The installed native modules remain
        // the sole resource producer, fuel consumer and heat simulator.
        sealed class ReactorWitness
        {
            public PartModule Module;
            public object Loop;
            public double Generation, Heat, Core, Margin;
        }
        sealed class ReactorUtilityWitness
        {
            public readonly HashSet<PartModule> Accounted=new HashSet<PartModule>();
            public bool Present,Qualified,HeatQualified;
            public double Generation,Endurance=double.MaxValue,Core,Margin=double.MaxValue;
            public double Heat,Cooling,Rejection,LoopMargin=double.MaxValue;
            public bool FullPower=true;
            public ColonyReactorContinuationProof Proof;
            public readonly List<Tuple<string,double,double>> ThermalSamples=new List<Tuple<string,double,double>>();
            public string Evidence="",Reason="";
        }
        static object ReviewedPrivateField(object instance,string name)
        {
            if(instance==null)return null;
            for(var type=instance.GetType();type!=null;type=type.BaseType){var field=type.GetField(name,BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.DeclaredOnly);if(field!=null)return field.GetValue(instance);}
            return null;
        }
        static bool SupportedSystemHeat(PartModule module) => module!=null && module.GetType().Assembly.GetName().Name=="SystemHeat" && module.GetType().Assembly.GetName().Version==new Version(0,9,1,0);
        static bool PositiveFinite(double value) => Finite(value) && value>0;
        static double RequiredUtilityEndurance(Vessel vessel)
        {
            double days=1;
            if(Current?.state!=null)foreach(var colony in Current.state.Colonies.Where(c=>c.Facilities.Any(f=>f.VesselId==vessel.id.ToString("D"))))days=Math.Max(days,colony.Charter.ReserveDays);
            return days*21600;
        }
        // Monotone unweighted Hermite segments with bounded slopes cannot
        // overshoot their endpoints. A sampled curve maximum is insufficient.
        static bool TryThrottleCurve(FloatCurve curve,out double maximum)
        {
            maximum=0;if(curve==null || curve.Curve==null)return false;
            var keys=curve.Curve.keys;if(keys.Length<2 || keys.Length>32 || keys[0].time!=0 || keys[keys.Length-1].time!=100)return false;
            for(int i=0;i<keys.Length;i++)
            {
                var key=keys[i];if(!Finite(key.time) || !Finite(key.value) || key.value<0 || !Finite(key.inTangent) || !Finite(key.outTangent))return false;
                var weighted=key.GetType().GetProperty("weightedMode");if(weighted!=null && Convert.ToInt32(weighted.GetValue(key,null),CultureInfo.InvariantCulture)!=0)return false;
                if(i==0)continue;var prior=keys[i-1];double span=key.time-prior.time,delta=key.value-prior.value;if(span<=0 || delta<0)return false;
                double slope=delta/span;if(prior.outTangent<0 || key.inTangent<0 || prior.outTangent>3*slope+1e-6 || key.inTangent>3*slope+1e-6)return false;
            }
            maximum=keys[keys.Length-1].value;return PositiveFinite(maximum);
        }
        static ReactorUtilityWitness ReadLoadedReactors(Vessel vessel,double ut)
        {
            var witness=new ReactorUtilityWitness();
            if(!vessel.loaded || vessel.parts==null)return witness;
            var modules=vessel.parts.Where(p=>p!=null).SelectMany(p=>p.Modules.Cast<PartModule>()).Where(m=>m!=null).ToArray();
            var native=modules.Where(m=>m.GetType().FullName=="SystemHeat.ModuleSystemHeatFissionReactor").Take(17).ToArray();
            witness.Present=native.Length>0;if(!witness.Present){InvalidateLoadedReactorProof(vessel,ut);return witness;}
            bool observerReady=EnsureReactorObservers();
            try
            {
                if(native.Length>16)throw new InvalidOperationException("Reactor observation exceeds sixteen actual modules.");
                var reactors=new List<ReactorWitness>();var rates=new Dictionary<string,double>(StringComparer.Ordinal);var stocks=new Dictionary<string,double>(StringComparer.Ordinal);
                var fuelPaths=new Dictionary<string,uint[]>(StringComparer.Ordinal);
                foreach(var module in native)
                {
                    if(!SupportedSystemHeat(module) || !UtilityBool(module,"moduleIsEnabled") || !UtilityBool(module,"Enabled") || !UtilityBool(module,"GeneratesElectricity") || UtilityBool(module,"Hibernating") || !(ReviewedPrivateField(module,"fuelCheckPassed") is bool fuelCheck) || !fuelCheck)throw new InvalidOperationException("The actual supported reactor is disabled, hibernating, or lacks a successful native fuel check.");
                    double age=ut-UtilityNumber(module,"LastUpdateTime",double.NaN),integrity=UtilityNumber(module,"CoreIntegrity",double.NaN),efficiency=UtilityNumber(module,"Efficiency",double.NaN);
                    double electricMaximum,heatMaximum;
                    if(!Finite(age) || age<0 || age>10 || !PositiveFinite(integrity) || integrity>100 || !Finite(efficiency) || efficiency<0 || efficiency>=1 || !TryThrottleCurve(UtilityRead(module,"ElectricalGeneration") as FloatCurve,out electricMaximum) || !TryThrottleCurve(UtilityRead(module,"HeatGeneration") as FloatCurve,out heatMaximum))throw new InvalidOperationException("Current reactor time, integrity, efficiency or bounded native curves are unavailable.");
                    double rated=electricMaximum*integrity/100;
                    if(UtilityBool(module,"ManualControl"))rated=Math.Min(rated,UtilityNumber(module,"MaxElectricalGeneration",double.NaN));
                    double current=UtilityNumber(module,"CurrentElectricalGeneration",double.NaN);
                    if(!PositiveFinite(rated) || !Finite(current) || current<0 || current>electricMaximum*1.001)throw new InvalidOperationException("Actual reactor output limit is invalid.");
                    var inputs=ReviewedPrivateField(module,"inputs") as IEnumerable;var outputs=ReviewedPrivateField(module,"outputs") as IEnumerable;
                    if(inputs==null || outputs==null)throw new InvalidOperationException("Actual native fuel/waste recipe is unavailable.");
                    var inputRows=inputs.Cast<object>().Take(17).ToArray();var outputRows=outputs.Cast<object>().Take(17).ToArray();if(inputRows.Length==0 || inputRows.Length>16 || outputRows.Length>16)throw new InvalidOperationException("Actual fuel/waste recipe exceeds provider bounds.");
                    foreach(var row in inputRows.Concat(outputRows))
                    {
                        bool output=outputRows.Contains(row);string name=Convert.ToString(UtilityRead(row,"ResourceName"),CultureInfo.InvariantCulture);double rate=UtilityNumber(row,"Ratio",double.NaN);
                        var mode=(ResourceFlowMode)UtilityRead(row,"FlowMode");
                        if(string.IsNullOrEmpty(name) || name=="ElectricCharge" || !PositiveFinite(rate) || mode!=ResourceFlowMode.NO_FLOW && mode!=ResourceFlowMode.ALL_VESSEL)throw new InvalidOperationException("Fuel/waste resource or actual flow mode requires another qualified provider.");
                        // Native CheckFull inspects the reactor's own waste tank,
                        // even when a recipe declares a wider output flow mode.
                        var tanks=output || mode==ResourceFlowMode.NO_FLOW ? new[]{module.part.Resources.Get(name)} : vessel.parts.Where(p=>p!=null).Select(p=>p.Resources.Get(name)).ToArray();
                        if(tanks.Any(t=>t==null) && (output || mode==ResourceFlowMode.NO_FLOW))throw new InvalidOperationException("A required actual reactor fuel/waste tank is absent.");
                        var usable=tanks.Where(t=>t!=null && t.flowState).ToArray();double stock=usable.Sum(t=>output ? t.maxAmount-t.amount : t.amount);
                        if(usable.Length==0 || usable.Any(t=>!Finite(t.amount) || !Finite(t.maxAmount) || t.amount<0 || t.amount>t.maxAmount) || !PositiveFinite(stock))throw new InvalidOperationException("Actual fuel or waste room is empty, disabled, or invalid.");
                        uint[] path=usable.Select(t=>t.part.persistentId).OrderBy(id=>id).ToArray();
                        string key=(output ? "waste:" : "fuel:")+name+":"+string.Join(",",path);
                        if(!output)fuelPaths[key]=path;
                        rates[key]=rates.TryGetValue(key,out double prior) ? prior+rate : rate;stocks[key]=stock;
                    }
                    var heatModule=ReviewedPrivateField(module,"heatModule") as PartModule;var loop=UtilityRead(heatModule,"Loop");
                    if(heatModule==null || heatModule.vessel!=vessel || !SupportedSystemHeat(heatModule) || loop==null)throw new InvalidOperationException("Actual reactor heat-loop ownership is unavailable.");
                    double core=UtilityNumber(module,"InternalCoreTemperature",double.NaN),cutoff=Math.Min(UtilityNumber(module,"CurrentSafetyOverride",double.NaN),UtilityNumber(module,"CriticalTemperature",double.NaN));
                    if(!PositiveFinite(core) || !PositiveFinite(cutoff) || cutoff-core<100)throw new InvalidOperationException("Actual reactor core lacks a 100 K shutdown/damage margin.");
                    double fullHeat=heatMaximum*(1-efficiency)*integrity/100;
                    double actualHeat=UtilityNumber(module,"CurrentHeatGeneration",double.NaN);
                    witness.FullPower &= UtilityBool(module,"ManualControl") && !UtilityBool(module,"HibernateOnWarp") && UtilityNumber(module,"CurrentReactorThrottle",double.NaN)==100 &&
                        UtilityNumber(module,"CurrentThrottle",double.NaN)==100 && PositiveFinite(actualHeat) && Math.Abs(actualHeat-fullHeat)<=Math.Max(1e-5,fullHeat*1e-5) && current>=rated*.999;
                    reactors.Add(new ReactorWitness {Module=module,Loop=loop,Generation=rated,Heat=fullHeat,Core=core,Margin=cutoff-core});
                    witness.Accounted.Add(module);witness.Accounted.Add(heatModule);
                }
                foreach(var a in fuelPaths)foreach(var b in fuelPaths.Where(p=>string.CompareOrdinal(p.Key,a.Key)>0))
                    if(a.Key.Split(':')[1]==b.Key.Split(':')[1] && a.Value.Intersect(b.Value).Any())throw new InvalidOperationException("Overlapping unequal native fuel paths require a shared tank/endurance allocator.");
                var scarce=new HashSet<string>(rates.Keys.Select(k=>k.Split(':')[1]),StringComparer.Ordinal);
                foreach(var module in modules.Where(m=>!native.Contains(m)))
                {
                    if(module is BaseConverter converter && converter.IsActivated)throw new InvalidOperationException("Other active conversion on the reactor vessel requires separate bounded fuel/waste allocation.");
                    if(module is ModuleGenerator generator && (generator.generatorIsActive || generator.isAlwaysActive) &&
                        generator.resHandler.inputResources.Concat(generator.resHandler.outputResources).Any(r=>scarce.Contains(r.name)))
                        throw new InvalidOperationException("Another active native generator shares reactor fuel/waste; endurance allocation is unavailable.");
                }
                witness.Endurance=rates.Min(r=>stocks[r.Key]/r.Value);
                if(!Finite(witness.Endurance) || witness.Endurance<RequiredUtilityEndurance(vessel))throw new InvalidOperationException("Full-throttle shared fuel and local waste room do not cover the colony reserve period.");
                double coolingTotal=0,heatTotal=0;
                foreach(var loop in reactors.Select(r=>r.Loop).Distinct())
                {
                    var owners=((IEnumerable)UtilityRead(loop,"LoopModules"))?.Cast<object>().Take(513).ToArray();if(owners==null || owners.Length==0 || owners.Length>512 || owners.Any(o=>!(o is PartModule partModule) || partModule.vessel!=vessel))throw new InvalidOperationException("Heat-loop module membership is absent, oversized, or crosses physical ownership.");
                    var local=reactors.Where(r=>ReferenceEquals(r.Loop,loop)).ToArray();double nominal=local.Min(r=>UtilityNumber(r.Module,"NominalTemperature",double.NaN)),heat=local.Sum(r=>r.Heat),cooling=0;
                    double loopTemperature=UtilityNumber(loop,"Temperature",double.NaN);if(!PositiveFinite(nominal) || !PositiveFinite(loopTemperature) || loopTemperature>nominal+90)throw new InvalidOperationException("Actual loop temperature lacks 10 K of the bounded operating margin.");
                    witness.FullPower&=observerReady && HasFreshNativeHeatTick(loop,ut);
                    witness.LoopMargin=Math.Min(witness.LoopMargin,nominal+100-loopTemperature);
                    foreach(var reactor in local)witness.ThermalSamples.Add(Tuple.Create(reactor.Module.part.persistentId+":"+NativeModuleId(reactor.Module),reactor.Core,loopTemperature));
                    double rejected=0;
                    foreach(var radiator in modules.Where(m=>m.GetType().FullName=="SystemHeat.ModuleSystemHeatRadiator"))
                    {
                        var heatModule=ReviewedPrivateField(radiator,"heatModule") as PartModule;if(!ReferenceEquals(UtilityRead(heatModule,"Loop"),loop))continue;
                        if(!SupportedSystemHeat(radiator) || !UtilityBool(radiator,"moduleIsEnabled") || !UtilityBool(radiator,"IsCooling"))continue;
                        if(radiator.part.Modules.Cast<PartModule>().OfType<ModuleDeployableRadiator>().Any(d=>d.deployState!=ModuleDeployablePart.DeployState.EXTENDED))continue;
                        var curve=UtilityRead(radiator,"temperatureCurve") as FloatCurve;if(curve==null)continue;
                        double rating=curve.Evaluate((float)nominal);
                        if(UtilityBool(radiator,"affectedByAtmosphere") && vessel.atmDensity>0){var atmosphere=UtilityRead(radiator,"atmosphereCurve") as FloatCurve;rating*=atmosphere==null ? double.NaN : atmosphere.Evaluate((float)vessel.atmDensity);}
                        if(UtilityBool(radiator,"affectedByAcceleration")){var acceleration=UtilityRead(radiator,"accelerationCurve") as FloatCurve;rating*=acceleration==null ? double.NaN : acceleration.Evaluate((float)vessel.acceleration.magnitude);}
                        double observed=UtilityNumber(radiator,"radiativeFlux",double.NaN);observed=Convert.ToDouble(ReviewedPrivateField(radiator,"radiativeFlux") ?? observed,CultureInfo.InvariantCulture);
                        if(!PositiveFinite(rating) || !Finite(observed) || observed>=0)continue;
                        cooling+=rating;rejected-=observed;witness.FullPower&=HasFreshNativeHeatTick(radiator,ut);witness.Accounted.Add(radiator);witness.Accounted.Add(heatModule);
                    }
                    if(owners.Any(o=>!witness.Accounted.Contains(o as PartModule)))throw new InvalidOperationException("A current heat-loop member lacks a bounded producer/rejection model.");
                    if(cooling<heat*1.1)throw new InvalidOperationException("Active same-loop radiators lack 10% full-throttle heat-rejection headroom at nominal temperature.");
                    heatTotal+=heat;coolingTotal+=cooling;witness.Rejection+=rejected;witness.FullPower&=rejected>=heat;
                }
                witness.Generation=reactors.Sum(r=>r.Generation);witness.Core=reactors.Max(r=>r.Core);witness.Margin=reactors.Min(r=>r.Margin);witness.Qualified=true;witness.HeatQualified=true;
                witness.Heat=heatTotal;witness.Cooling=coolingTotal;
                witness.Evidence="SystemHeat 0.9.1 actual loaded reactors="+reactors.Count+"; rated continuous EC/s="+witness.Generation.ToString("R",CultureInfo.InvariantCulture)+"; full-throttle fuel/waste endurance="+witness.Endurance.ToString("R",CultureInfo.InvariantCulture)+"s; bounded heat="+heatTotal.ToString("R",CultureInfo.InvariantCulture)+"kW; actual active same-loop radiator capacity at nominal="+coolingTotal.ToString("R",CultureInfo.InvariantCulture)+"kW. Native output rating is distinct from delivered flow; convection bonus is excluded.";
                RecordLoadedReactorContinuation(vessel,ut,witness);
                witness.Evidence+=" Native heat ticks: observer="+observerReady+"; loops="+string.Join(",",reactors.Select(r=>r.Loop).Distinct().Take(16).Select(loop=>HasFreshNativeHeatTick(loop,ut).ToString()))+"; accounted radiators="+string.Join(",",modules.Where(m=>m.GetType().FullName=="SystemHeat.ModuleSystemHeatRadiator" && witness.Accounted.Contains(m)).Take(16).Select(m=>m.part.persistentId+":"+HasFreshNativeHeatTick(m,ut)))+"; warp="+TimeWarp.CurrentRate+"; paused="+FlightDriver.Pause+"; timeScale="+Time.timeScale+".";
                witness.Reason="Actual loaded reactor fuel, waste room and heat-loop package cover the reserve period.";
            }
            catch(Exception ex){InvalidateLoadedReactorProof(vessel,ut);witness.Qualified=false;witness.HeatQualified=false;witness.Generation=0;witness.Reason="Reactor utility hold: "+Bound(ex.Message,512);}
            return witness;
        }
    }
}
