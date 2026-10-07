using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Expanse.Domain.Colonies;
using USITools;

namespace Expanse.WorldBridge
{
    public sealed partial class ColonyRuntime
    {
        // Stock ResourceRatio.Load parses config Ratio as float, then widens to
        // double. Match that exact native value; reserve burn remains 1e-6.
        const double FixedGeneratorNativeInput=(double)(float)1e-6;
        sealed class FixedGeneratorWitness
        {
            public readonly HashSet<PartModule> Accounted=new HashSet<PartModule>();
            public bool Present,Qualified;
            public double Generation,Endurance=double.MaxValue;
            public string Reason="",Evidence="";
        }
        static bool IsReviewedFixedGenerator(PartModule module)
        {
            // IsStandalone is native info/swap-discovery metadata, not hardware
            // identity. Actual swap/addon/recipe guards remain below.
            return module!=null&&module.part?.partInfo?.name=="Ranger.PowerPack"&&module.GetType()==typeof(USI_Converter)&&
                module.GetType().Assembly.GetName().Version==new Version(1,0,0,0);
        }
        static void RequireFixedGeneratorHardware(USI_Converter module,bool requireInitialized=true)
        {
            if(!IsReviewedFixedGenerator(module)||!module.isEnabled||!UtilityBool(module,"moduleIsEnabled")||module.GeneratesHeat||module.UseSpecialistBonus||
                module.ConvertByMass||module.EfficiencyBonus!=1||ReviewedPrivateField(module,"_swapOption")!=null||module.Addons==null||module.Addons.Count!=0||
                module.part.Modules.Cast<PartModule>().OfType<BaseConverter>().Count()!=1||
                !(ReviewedPrivateField(module,"_preCalculateEfficiency") is bool pre)||pre||
                requireInitialized&&Convert.ToDouble(ReviewedPrivateField(module,"_totalEfficiencyModifiers")??double.NaN,CultureInfo.InvariantCulture)!=1)
                throw new InvalidOperationException("Fixed Generator requires unchanged exact standalone units-based native hardware; swap/addons/heat/specialist/modified efficiency are unsupported.");
            RequireFixedGeneratorRecipe(module.inputList,module.outputList,module.reqList);
            var definition=PartResourceLibrary.Instance.GetDefinition("Plutonium-238");
            if(definition==null||definition.resourceFlowMode!=ResourceFlowMode.NO_FLOW||
                module.inputList.Any(r=>r.FlowMode!=ResourceFlowMode.NULL&&r.FlowMode!=ResourceFlowMode.NO_FLOW)||
                module.part.Modules.Cast<PartModule>().Any(m=>m!=module&&m.resHandler.inputResources.Any(r=>r.name=="Plutonium-238")))
                throw new InvalidOperationException("Fixed Generator local fuel path is not exclusive reviewed NO_FLOW stock.");
        }
        static void RequireFixedGeneratorRecipe(IEnumerable<ResourceRatio> inputs,IEnumerable<ResourceRatio> outputs,IEnumerable<ResourceRatio> requirements)
        {
            var i=inputs?.ToArray();var o=outputs?.ToArray();var r=requirements?.ToArray();
            if(i==null||o==null||r==null||i.Length!=1||o.Length!=1||r.Length!=1||i[0].ResourceName!="Plutonium-238"||i[0].Ratio!=FixedGeneratorNativeInput||
                o[0].ResourceName!="ElectricCharge"||o[0].Ratio!=50||!o[0].DumpExcess||r[0].ResourceName!="Plutonium-238"||r[0].Ratio!=20)
                throw new InvalidOperationException("Fixed Generator final physical fuel/output/local requirement differs from installed Ranger recipe.");
        }
        static FixedGeneratorWitness ReadFixedGenerators(Vessel vessel,string context,double ut,object processor)
        {
            var result=new FixedGeneratorWitness();
            try
            {
                double horizon=RequiredUtilityEndurance(vessel);var evidence=new List<string>();
                if(vessel.loaded)
                {
                    var modules=vessel.parts.SelectMany(p=>p.Modules.Cast<PartModule>()).Where(IsReviewedFixedGenerator).Cast<USI_Converter>().Take(17).ToArray();
                    result.Present=modules.Length>0;if(modules.Length>16)throw new InvalidOperationException("Fixed Generator module bound exceeded.");
                    foreach(var module in modules)
                    {
                        RequireFixedGeneratorHardware(module);result.Accounted.Add(module);
                        if(!module.IsActivated)continue;
                        ResourceRatio[] ignored;string reason;
                        if(!TryReadNativeRecipeInputs(module,context,ut,out ignored,out reason))
                            throw new InvalidOperationException(reason);
                        if(!UtilityRecipes.TryGetValue(module,out UtilityRecipeSample sample)||
                            !sample.MultiplierObserved||sample.MultiplierUt!=sample.Ut||ut<sample.MultiplierUt||ut-sample.MultiplierUt>10||sample.NativeMultiplier!=1)
                            throw new InvalidOperationException("Actual fixed native final recipe and post-recipe multiplier have not been observed together. "+FixedGeneratorPairDiagnostic(sample,ut));
                        RequireFixedGeneratorRecipe(sample.Inputs,sample.Outputs,sample.Requirements);
                        var fuel=module.part.Resources.Get("Plutonium-238");
                        if(fuel==null||!fuel.flowState||fuel.maxAmount!=20)throw new InvalidOperationException("Actual exclusive local fuel tank is unavailable or changed.");
                        if(!ColonyFixedGeneratorBounds.TryFuelBound(fuel.amount,fuel.maxAmount,20,1e-6,50,1,1,horizon,out double generation,out double endurance,out reason))throw new InvalidOperationException(reason);
                        result.Generation+=generation;result.Endurance=Math.Min(result.Endurance,endurance);
                        evidence.Add("fixed:"+module.part.persistentId+":"+NativeModuleId(module)+" currentPu="+fuel.amount.ToString("R",CultureInfo.InvariantCulture)+"; reserve="+horizon.ToString("R",CultureInfo.InvariantCulture)+"s; conservativeEC="+generation.ToString("R",CultureInfo.InvariantCulture)+"/s; actual native recipe/multiplier observed");
                    }
                }
                else
                {
                    var native=vessel.protoVessel.protoPartSnapshots.Where(p=>p.partName=="Ranger.PowerPack").Take(17).ToArray();
                    result.Present=native.Length>0;if(native.Length>16||native.Length>0&&processor==null)throw new InvalidOperationException("Fixed native background provider unavailable or module bound exceeded.");
                    foreach(var part in native)
                    {
                        var prefab=part.partInfo?.partPrefab?.Modules.Cast<PartModule>().OfType<USI_Converter>().SingleOrDefault();
                        if(prefab==null)throw new InvalidOperationException("Fixed saved Generator lacks its exact installed module.");
                        // Stock tallies the instance multiplier only in OnStart; prefab
                        // hardware is configured but not started. Saved state/BRP prove output.
                        RequireFixedGeneratorHardware(prefab,false);result.Accounted.Add(prefab);
                        var saved=part.modules.SingleOrDefault(m=>m.moduleName=="USI_Converter");
                        if(saved==null||!bool.TryParse(saved.moduleValues.GetValue("IsActivated"),out bool active))throw new InvalidOperationException("Fixed saved Generator activation is unknown.");
                        if(!active)continue;
                        if(!bool.TryParse(saved.moduleValues.GetValue("isEnabled"),out bool enabled)||!enabled||!double.TryParse(saved.moduleValues.GetValue("EfficiencyBonus"),NumberStyles.Float,CultureInfo.InvariantCulture,out double bonus)||!Finite(bonus)||bonus!=1||
                            !uint.TryParse(saved.moduleValues.GetValue("persistentId"),NumberStyles.None,CultureInfo.InvariantCulture,out uint moduleId)||moduleId==0)
                            throw new InvalidOperationException("Fixed saved Generator identity/settings differ from reviewed native state.");
                        var fuel=part.resources.SingleOrDefault(r=>r.resourceName=="Plutonium-238");
                        if(fuel==null||!fuel.flowState||fuel.maxAmount!=20)throw new InvalidOperationException("Current actual saved local fuel tank is unavailable or changed.");
                        VerifyFixedGeneratorBrp(vessel,processor,part,moduleId,fuel,ut);
                        if(!ColonyFixedGeneratorBounds.TryFuelBound(fuel.amount,fuel.maxAmount,20,1e-6,50,1,1,horizon,out double generation,out double endurance,out string reason))throw new InvalidOperationException(reason);
                        result.Generation+=generation;result.Endurance=Math.Min(result.Endurance,endurance);
                        evidence.Add("fixed:"+part.flightID+":"+moduleId+" currentPu="+fuel.amount.ToString("R",CultureInfo.InvariantCulture)+"; conservativeEC="+generation.ToString("R",CultureInfo.InvariantCulture)+"/s; exact current native BRP recipe/path/constraint");
                    }
                }
                result.Qualified=result.Present&&result.Generation>0;result.Evidence=string.Join(";",evidence);
                result.Reason=result.Qualified?"Exact native fixed Generator conditional fuel/output horizon qualified; no resources produced by the adapter.":"Fixed Generator is inactive; native startup or user action required.";
            }
            catch(Exception ex){result.Generation=0;result.Qualified=false;result.Reason="Fixed Generator hold: "+Bound(ex.Message,1000);}
            return result;
        }
        static void VerifyFixedGeneratorBrp(Vessel vessel,object processor,ProtoPartSnapshot part,uint moduleId,ProtoPartResourceSnapshot fuel,double ut)
        {
            if(!RemoteBrpInventoryGateway.SupportedProviderAvailable||!Finite(ut-UtilityNumber(processor,"LastChangepoint",double.NaN))||ut<UtilityNumber(processor,"LastChangepoint",double.NaN)||ut-UtilityNumber(processor,"LastChangepoint",double.NaN)>10)
                throw new InvalidOperationException("Fixed native BRP recipe is stale after catch-up.");
            var converters=((IEnumerable)UtilityRead(processor,"Converters")).Cast<object>().Take(513).ToArray();if(converters.Length>512)throw new InvalidOperationException("BRP recipe bound exceeded.");
            var mapped=converters.Where(c=>UtilityRead(c,"FlightId") is uint flight&&flight==part.flightID&&UtilityRead(c,"ModuleId") is uint id&&id==moduleId).ToArray();
            if(mapped.Length!=1)throw new InvalidOperationException("Saved fixed Generator lacks one exact native BRP module mapping.");
            var converter=mapped[0];double rate=UtilityNumber(converter,"Rate",double.NaN);string status=Convert.ToString(UtilityRead(converter,"ConstraintState"),CultureInfo.InvariantCulture);
            RequireProportionalObservation(converter,vessel,part,moduleId,ut,processor);
            var inputs=((IEnumerable)UtilityRead(converter,"Inputs")).Cast<object>().Select(e=>UtilityRead(e,"Value")).Take(2).ToArray();
            var outputs=((IEnumerable)UtilityRead(converter,"Outputs")).Cast<object>().Select(e=>UtilityRead(e,"Value")).Take(2).ToArray();
            if(inputs.Length!=1||outputs.Length!=1||Convert.ToString(UtilityRead(inputs[0],"ResourceName"),CultureInfo.InvariantCulture)!="Plutonium-238"||Convert.ToString(UtilityRead(outputs[0],"ResourceName"),CultureInfo.InvariantCulture)!="ElectricCharge"||
                !PositiveFinite(rate)||rate>1||status!="ENABLED"&&status!="BOUNDARY")throw new InvalidOperationException("Current exact fixed BRP recipe/constraint is unqualified.");
            double input=UtilityNumber(inputs[0],"Ratio",double.NaN),output=UtilityNumber(outputs[0],"Ratio",double.NaN);
            if(!PositiveFinite(input)||!PositiveFinite(output)||input>1e-6*(1+1e-9)||output>50*(1+1e-9)||Math.Abs(output/input-50/FixedGeneratorNativeInput)>50/FixedGeneratorNativeInput*1e-9||output*rate+1e-7<50*Math.Min(1,fuel.amount/20))
                throw new InvalidOperationException("Current native BRP fixed output/rate cannot sustain reviewed fuel-throttled source.");
            var inventories=UtilityRead(processor,"Inventories");int count=Convert.ToInt32(UtilityRead(inventories,"Count"),CultureInfo.InvariantCulture);var indexer=inventories?.GetType().GetProperty("Item");
            var pulls=((IEnumerable)UtilityRead(converter,"Pull")).Cast<object>().Select(i=>Convert.ToInt32(i,CultureInfo.InvariantCulture)).Take(4097).ToArray();
            if(count<0||count>4096||indexer==null||pulls.Length>4096||pulls.Any(i=>i<0||i>=count))throw new InvalidOperationException("Fixed fuel inventory index is unavailable or unbounded.");
            var paths=pulls.Select(i=>indexer.GetValue(inventories,new object[]{i})).Where(r=>Convert.ToString(UtilityRead(r,"ResourceName"),CultureInfo.InvariantCulture)=="Plutonium-238").ToArray();
            if(paths.Length!=1||UtilityRead(paths[0],"ModuleId")!=null||!(UtilityRead(paths[0],"FlightId") is uint flightId)||flightId!=part.flightID||!ReferenceEquals(UtilityRead(paths[0],"Snapshot"),fuel)||
                UtilityNumber(paths[0],"Amount",double.NaN)!=fuel.amount||UtilityNumber(paths[0],"MaxAmount",double.NaN)!=fuel.maxAmount||UtilityNumber(paths[0],"OriginalAmount",double.NaN)!=fuel.amount)
                throw new InvalidOperationException("Fixed native BRP fuel is not exactly owned synchronized same-part stock.");
            if(converters.Any(c=>!ReferenceEquals(c,converter)&&((IEnumerable)UtilityRead(c,"Pull")).Cast<object>().Select(i=>Convert.ToInt32(i,CultureInfo.InvariantCulture)).Intersect(pulls).Any()&&
                ((IEnumerable)UtilityRead(c,"Inputs")).Cast<object>().Any(e=>Convert.ToString(UtilityRead(UtilityRead(e,"Value"),"ResourceName"),CultureInfo.InvariantCulture)=="Plutonium-238")))
                throw new InvalidOperationException("Another native BRP recipe shares fixed Generator fuel; a bounded shared allocator is required.");
        }
    }
}
