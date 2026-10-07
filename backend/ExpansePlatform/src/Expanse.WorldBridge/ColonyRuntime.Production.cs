using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Expanse.Domain.Colonies;
using USITools;
using WOLF;
namespace Expanse.WorldBridge
{
    public sealed partial class ColonyRuntime
    {
        void RunProductionEffects()
        {
            if(RunProductionInventoryRegistration())return;
            if(!Ready||mutating||Current!=this||HighLogic.CurrentGame==null||!ReferenceEquals(selectedGame,HighLogic.CurrentGame)||state.Effects.Any(e=>e.State=="held"||e.State=="applying"))return;
            var saved=state.Plans.Where(p=>p.State!="cancelled"&&p.State!="complete").SelectMany(p=>p.Production.Where(c=>c.State=="ready"||c.State=="configuring").Select(c=>new{Plan=p,Claim=c})).FirstOrDefault();
            if(saved==null)return;
            var selectedProductionGame=HighLogic.CurrentGame;string selectedProductionContext=ContextKey;
            var plan=saved.Plan;var claim=saved.Claim;var item=plan.Quote.ProductionInvestments.Single(i=>i.Id==claim.Id);
            try
            {
                RequireProductionInventoryCurrent(plan,item,claim);
                if(claim.State=="ready")
                {
                    var steps=new List<ColonyProductionStep>();
                    foreach(var feed in item.Recipe.Feeds)
                    {
                        var module=ResolveProductionModule(plan,item.HopperBuildingId,feed.CraftPartId,feed.PartName,"WOLF_HopperModule");
                        RequireProductionLoadout(module,feed.OptionIndex,feed.OptionHash,"WOLF_HopperSwapOption");
                        if(((WOLF_HopperModule)module).IsConnectedToDepot||((WOLF_HopperModule)module).HopperId.Length>0||((BaseConverter)module).IsActivated)throw new InvalidOperationException("Fresh reviewed feed module is already connected or active; no existing allocation is silently adopted.");
                        var facility=ProductionFacility(plan,item.HopperBuildingId);
                        steps.Add(ProductionStep(plan,claim,"connect:"+feed.CraftPartId,"hopperConnect",facility,module,feed.OptionHash));
                        steps.Add(ProductionStep(plan,claim,"start:"+feed.CraftPartId,"converterStart",facility,module,feed.OptionHash));
                    }
                    var agriculture=ResolveProductionModule(plan,item.BuildingId,item.Recipe.CraftPartId,item.Recipe.PartName,item.Recipe.ModuleName);
                    RequireProductionLoadout(agriculture,item.Recipe.OptionIndex,item.Recipe.OptionHash,"USI_ConverterSwapOption");
                    if(((BaseConverter)agriculture).IsActivated)throw new InvalidOperationException("Fresh cultivation module is already active; no implicit replacement or unreviewed native state is accepted.");
                    steps.Add(ProductionStep(plan,claim,"start:cultivation","converterStart",ProductionFacility(plan,item.BuildingId),agriculture,item.Recipe.OptionHash));
                    Accept(ColonyEngine.PrepareProductionSteps(state,plan.Id,claim.Id,steps));return;
                }
                var step=claim.Steps.FirstOrDefault(s=>s.State=="prepared");if(step==null)return;
                bool farm=step.OptionHash==item.Recipe.OptionHash;var selectedFeed=item.Recipe.Feeds.SingleOrDefault(f=>f.OptionHash==step.OptionHash);
                if(!farm&&selectedFeed==null)throw new InvalidOperationException("Production step no longer belongs to an approved native option.");
                var native=ResolveProductionModule(plan,farm?item.BuildingId:item.HopperBuildingId,farm?item.Recipe.CraftPartId:selectedFeed.CraftPartId,farm?item.Recipe.PartName:selectedFeed.PartName,farm?item.Recipe.ModuleName:"WOLF_HopperModule");
                if(native.part.persistentId!=step.PartId||NativeModuleId(native)!=step.ModuleId||native.vessel.id.ToString("D")!=step.VesselId)throw new InvalidOperationException("Exact native production identity changed before activation.");
                RequireProductionLoadout(native,farm?item.Recipe.OptionIndex:selectedFeed.OptionIndex,step.OptionHash,farm?"USI_ConverterSwapOption":"WOLF_HopperSwapOption");
                RequireProductionStartConditions(plan,item,native,farm);
                var game=HighLogic.CurrentGame;var original=state;string context=ContextKey,epoch=loadEpoch;
                IRegistryCollection depotRegistry=null;ColonyWolfDepot beforeDepot=null,intendedDepot=null;
                WOLF_ScenarioModule depotScenario=null;Dictionary<string,string> beforeHoppers=null;
                if(step.Kind=="hopperConnect")
                {
                    WOLF_ScenarioModule scenario;IRegistryCollection registry;string reason;
                    if(!TryWolfRegistry(out scenario,out registry,out reason))throw new InvalidOperationException(reason);
                    if(!ReferenceEquals(ReviewedPrivateField(native,"_registry"),registry))throw new InvalidOperationException("Hopper native provider no longer matches the current WOLF scenario registry.");
                    depotScenario=scenario;beforeHoppers=CaptureProductionHoppers(registry);
                    depotRegistry=registry;beforeDepot=CaptureWolfDepot(registry,native.vessel.mainBody.bodyName,WOLF_AbstractPartModule.GetVesselBiome(native.vessel));
                    intendedDepot=new ColonyWolfDepot{Body=beforeDepot.Body,Biome=beforeDepot.Biome,Exists=beforeDepot.Exists,Established=beforeDepot.Established,Surveyed=beforeDepot.Surveyed,Streams=beforeDepot.Streams.Select(r=>new ColonyWolfStream{Resource=r.Resource,Incoming=r.Incoming,Outgoing=r.Outgoing}).ToList()};
                    foreach(var demand in ProductionPointInputs(native.part.partInfo.partConfig.GetNodes("MODULE").Where(n=>n.GetValue("name")=="WOLF_HopperSwapOption").ElementAt(selectedFeed.OptionIndex)))
                    {
                        var row=intendedDepot.Streams.SingleOrDefault(r=>r.Resource==demand.Resource);if(row==null||row.Incoming-row.Outgoing<demand.Points)throw new InvalidOperationException("Paid native WOLF supply is not currently unallocated for this exact hopper.");
                        row.Outgoing=checked(row.Outgoing+demand.Points);
                    }
                    var hopper=(WOLF_HopperModule)native;
                    if(hopper.IsConnectedToDepot||hopper.HopperId.Length>0||hopper.IsActivated)throw new InvalidOperationException("Reviewed unconnected hopper changed; no existing native connection can be replaced.");
                }
                else if(((BaseConverter)native).IsActivated)throw new InvalidOperationException("Native converter changed before the exact start child; no replay is performed.");
                string before=ProductionSettingsWitness(native,beforeDepot);
                var applying=ColonyEngine.MarkProductionApplying(original,plan.Id,claim.Id,step.Id,before);Accept(applying);mutating=true;
                try
                {
                    Action current=()=>{if(Current!=this||state!=applying||ContextKey!=context||!ReferenceEquals(game,HighLogic.CurrentGame)||native==null||!native.vessel.loaded||native.part.persistentId!=step.PartId||NativeModuleId(native)!=step.ModuleId||ProductionSettingsWitness(native,beforeDepot)!=before)throw new InvalidOperationException("Selected world/native module changed after durable production intent.");
                        RequireProductionLoadout(native,farm?item.Recipe.OptionIndex:selectedFeed.OptionIndex,step.OptionHash,farm?"USI_ConverterSwapOption":"WOLF_HopperSwapOption");
                        if(step.Kind=="hopperConnect"&&(!SameWolfContext(game,epoch,applying,depotScenario,depotRegistry)||ColonyStateCodec.WolfDepotHash(CaptureWolfDepot(depotRegistry,beforeDepot.Body,beforeDepot.Biome))!=ColonyStateCodec.WolfDepotHash(beforeDepot)||!SameProductionHoppers(beforeHoppers,CaptureProductionHoppers(depotRegistry))))throw new InvalidOperationException("Actual WOLF depot/hopper registry changed after the durable before witness.");};
                    current();
                    if(step.Kind=="hopperConnect")
                    {
                        var hopper=(WOLF_HopperModule)native;hopper.ConnectToDepotEvent();
                        var actual=CaptureWolfDepot(depotRegistry,native.vessel.mainBody.bodyName,WOLF_AbstractPartModule.GetVesselBiome(native.vessel));
                        if(!SameWolfContext(game,epoch,applying,depotScenario,depotRegistry)||ContextKey!=context||!hopper.IsConnectedToDepot||string.IsNullOrEmpty(hopper.HopperId)||ColonyStateCodec.WolfDepotHash(actual)!=ColonyStateCodec.WolfDepotHash(intendedDepot))throw new InvalidOperationException("Native hopper connection lacks exact whole-depot allocation and persisted HopperId readback.");
                        RequireProductionLoadout(native,selectedFeed.OptionIndex,step.OptionHash,"WOLF_HopperSwapOption");
                        var afterHoppers=CaptureProductionHoppers(depotRegistry);var created=depotRegistry.GetHoppers().SingleOrDefault(h=>h.Id==hopper.HopperId);
                        if(created==null||created.Depot.Body!=beforeDepot.Body||created.Depot.Biome!=beforeDepot.Biome||afterHoppers.Count!=beforeHoppers.Count+1||beforeHoppers.ContainsKey(hopper.HopperId)||beforeHoppers.Any(h=>!afterHoppers.TryGetValue(h.Key,out string value)||value!=h.Value)||!ProductionIngredientsSame(created.Recipe.InputIngredients,hopper.WolfRecipe.InputIngredients)||!ProductionIngredientsSame(created.Recipe.OutputIngredients,hopper.WolfRecipe.OutputIngredients))throw new InvalidOperationException("Native connection changed prior hopper metadata or lacks one exact new native recipe/allocation receipt.");
                        Accept(ColonyEngine.CompleteProductionStep(state,plan.Id,claim.Id,step.Id,ProductionSettingsWitness(native,actual),hopper.HopperId));
                    }
                    else
                    {
                        ((BaseConverter)native).StartResourceConverter();
                        if(Current!=this||state!=applying||ContextKey!=context||!ReferenceEquals(game,HighLogic.CurrentGame)||!((BaseConverter)native).IsActivated||NativeModuleId(native)!=step.ModuleId)throw new InvalidOperationException("Native converter start lacks exact activation readback.");
                        RequireProductionLoadout(native,farm?item.Recipe.OptionIndex:selectedFeed.OptionIndex,step.OptionHash,farm?"USI_ConverterSwapOption":"WOLF_HopperSwapOption");
                        Accept(ColonyEngine.CompleteProductionStep(state,plan.Id,claim.Id,step.Id,ProductionSettingsWitness(native,null)));
                    }
                }
                catch(Exception ex){if(Current==this&&state==applying&&ContextKey==context)Accept(ColonyEngine.HoldProductionStep(state,plan.Id,claim.Id,step.Id,Bound(ex.Message,512)));else throw;}
                finally{mutating=false;}
            }
            catch(Exception ex)
            {
                if(Current!=this||ContextKey!=selectedProductionContext||!ReferenceEquals(selectedProductionGame,HighLogic.CurrentGame)||state==null||!state.Plans.Any(p=>p.Id==plan.Id&&p.Production.Any(ownedClaim=>ownedClaim.Id==claim.Id)))return;
                // Pure preflight is retryable and does not add a held external
                // effect. A native attempted child is held by the inner boundary.
                var next=ColonyStateCodec.Copy(state);var c=next.Plans.Single(p=>p.Id==plan.Id).Production.Single(p=>p.Id==claim.Id);
                string reason="Production preflight: "+Bound(ex.Message,460);if(c.Reason!=reason){c.Reason=reason;next.Revision++;Accept(next);}
            }
        }
        static bool ProductionIngredientsSame(Dictionary<string,int> a,Dictionary<string,int> b)=>a!=null&&b!=null&&a.Count==b.Count&&a.All(r=>b.TryGetValue(r.Key,out int points)&&points==r.Value);
        static bool SameProductionHoppers(Dictionary<string,string> a,Dictionary<string,string> b)=>a.Count==b.Count&&a.All(r=>b.TryGetValue(r.Key,out string value)&&value==r.Value);
        static Dictionary<string,string> CaptureProductionHoppers(IRegistryCollection registry)
        {
            var rows=registry.GetHoppers();if(rows==null||rows.Count>2048||rows.Any(h=>h==null||string.IsNullOrEmpty(h.Id)||h.Id.Length>256||h.Depot==null||h.Recipe?.InputIngredients==null||h.Recipe.OutputIngredients==null||h.Recipe.InputIngredients.Count>128||h.Recipe.OutputIngredients.Count>128)||rows.Select(h=>h.Id).Distinct(StringComparer.Ordinal).Count()!=rows.Count)throw new InvalidOperationException("Native hopper registry is unavailable, ambiguous or exceeds its bounded identity vector.");
            return rows.ToDictionary(h=>h.Id,h=>ColonyStateCodec.Hash(Encoding.UTF8.GetBytes(h.Depot.Body+"|"+h.Depot.Biome+"|"+string.Join(";",h.Recipe.InputIngredients.OrderBy(r=>r.Key,StringComparer.Ordinal).Select(r=>r.Key+"="+r.Value))+"|"+string.Join(";",h.Recipe.OutputIngredients.OrderBy(r=>r.Key,StringComparer.Ordinal).Select(r=>r.Key+"="+r.Value)))),StringComparer.Ordinal);
        }
        ColonyFacility ProductionFacility(ColonyPlan plan,string buildingId)
        {var line=plan.Buildings.Single(b=>b.Id==buildingId);var order=state.Construction.Single(o=>o.Id==line.OrderId);return state.Colonies.Single(c=>c.Id==plan.ColonyId).Facilities.Single(f=>f.Id==order.FacilityId&&f.ConstructionOrderId==order.Id);}
        PartModule ResolveProductionModule(ColonyPlan plan,string buildingId,uint craftPartId,string partName,string moduleName,bool requireOneTimes=true)
        {
            var facility=ProductionFacility(plan,buildingId);var line=plan.Buildings.Single(b=>b.Id==buildingId);var order=state.Construction.Single(o=>o.Id==line.OrderId);
            var vessel=FlightGlobals.Vessels.SingleOrDefault(v=>v!=null&&v.id.ToString("D")==facility.VesselId);
            if(vessel==null||!vessel.loaded||!vessel.LandedOrSplashed||vessel.parts==null||vessel.parts.Count>512||requireOneTimes&&TimeWarp.CurrentRate!=1||!facility.PartIds.OrderBy(x=>x).SequenceEqual(vessel.parts.Select(p=>p.persistentId).OrderBy(x=>x)))throw new InvalidOperationException("Load the exact registered landed production building at 1×; packed native modules are supported.");
            RequireProductionLocation(plan,facility,vessel);
            var parts=vessel.parts.Where(p=>p.partInfo?.name==partName&&p.Modules.Cast<PartModule>().OfType<ColonyPlacementMarker>().Any(m=>m.craftPartId==craftPartId&&m.worldId==state.WorldId&&m.colonyId==plan.ColonyId&&m.plotId==facility.PlotId&&m.operationId==facility.PlacementOperationId&&m.requestFingerprint==facility.PlacementRequestFingerprint&&m.templateSha256==facility.CraftSha256)).ToArray();
            if(parts.Length!=1||facility.TemplateHash!=line.TemplateHash||facility.PlacementOperationId!=order.Placement.OperationId||facility.PlacementRequestFingerprint!=order.Placement.RequestFingerprint)throw new InvalidOperationException("Production has no unique reviewed physical marker/part mapping.");
            var modules=parts[0].Modules.Cast<PartModule>().Where(m=>m.moduleName==moduleName).ToArray();
            if(modules.Length!=1||!(modules[0] is BaseConverter)||NativeModuleId(modules[0])==0)throw new InvalidOperationException("Exact native converter module is absent or duplicated.");return modules[0];
        }
        void RequireProductionLocation(ColonyPlan plan,ColonyFacility facility,Vessel vessel)
        {
            var colony=state.Colonies.Single(c=>c.Id==plan.ColonyId);var plot=colony.Plots.Single(p=>p.Id==facility.PlotId);
            if(facility.State!="operational"||vessel.mainBody==null||vessel.mainBody.bodyName!=colony.Site.Body||WOLF_AbstractPartModule.GetVesselBiome(vessel)!=colony.Site.Biome)throw new InvalidOperationException("Production is outside its actual commissioned body/biome; no alternate depot can replace the reviewed site.");
            var actual=new ColonySite{Body=colony.Site.Body,Latitude=vessel.latitude,Longitude=vessel.longitude};
            if(ColonyEngine.SurfaceDistance(colony.Site,actual,vessel.mainBody.Radius)>colony.Site.RadiusMeters||ColonyEngine.SurfaceDistance(new ColonySite{Body=colony.Site.Body,Latitude=plot.Latitude,Longitude=plot.Longitude},actual,vessel.mainBody.Radius)>Math.Min(50,Math.Max(5,Math.Max(plot.WidthMeters,plot.LengthMeters)/2)))throw new InvalidOperationException("Production moved outside its registered colony/plot footprint; persistent IDs alone do not authorize relocation.");
        }
        static ColonyProductionStep ProductionStep(ColonyPlan plan,ColonyProductionClaim claim,string suffix,string kind,ColonyFacility facility,PartModule native,string optionHash)=>
            new ColonyProductionStep{Id=ColonyEngine.PlanningChildId(plan.Id,claim.Id+":"+suffix),Kind=kind,FacilityId=facility.Id,VesselId=facility.VesselId,PartId=native.part.persistentId,ModuleId=NativeModuleId(native),OptionHash=optionHash};
        static AbstractSwapOption RequireProductionLoadout(PartModule module,int optionIndex,string optionHash,string optionModule)
        {
            var bay=module.part.FindModulesImplementing<USI_SwappableBay>().SingleOrDefault(b=>b.moduleIndex==0);
            var configured=module.part.partInfo.partConfig.GetNodes("MODULE").Where(n=>n.GetValue("name")==optionModule).ToArray();
            var live=module.part.Modules.Cast<PartModule>().Where(m=>m.moduleName==optionModule).ToArray();
            if(bay==null||bay.currentLoadout!=optionIndex||optionIndex<0||optionIndex>=configured.Length||optionIndex>=live.Length||ProductionOptionHash(configured[optionIndex])!=optionHash||!ReferenceEquals(ReviewedPrivateField(module,"_swapOption"),live[optionIndex]))throw new InvalidOperationException("Native OnStart selected option differs from the exact preconfigured paid bill; no free bay change is attempted.");
            var converter=(ModuleResourceConverter)module;
            if(converter.ConvertByMass||module.GetType()!=typeof(USITools.USI_Converter)&&module.GetType()!=typeof(WOLF_HopperModule))throw new InvalidOperationException("This production adapter supports only the reviewed native units-based converter implementation.");
            var selected=(AbstractSwapOption)live[optionIndex];
            RequireProductionRecipeVector(selected.inputList,ProductionConfigRates(configured[optionIndex],"INPUT_RESOURCE"));RequireProductionRecipeVector(selected.outputList,ProductionConfigRates(configured[optionIndex],"OUTPUT_RESOURCE"));
            return (AbstractSwapOption)live[optionIndex];
        }
        static System.Collections.Generic.List<ColonyProductionRate> ProductionNativeRates(System.Collections.Generic.List<ColonyProductionRate> expected)
        {
            if(expected==null||expected.Count>16||expected.Any(e=>e==null||string.IsNullOrEmpty(e.Resource)||!Finite(e.UnitsPerSecond)||e.UnitsPerSecond<0||e.UnitsPerSecond>1e9)||expected.Select(e=>e.Resource).Distinct(StringComparer.Ordinal).Count()!=expected.Count)throw new InvalidOperationException("Reviewed native production recipe vector is invalid.");
            // USI AbstractSwapOption.OnLoad uses stock ResourceRatio.Load,
            // which parses Ratio as float. Keep the paid nominal DTO unchanged;
            // project its canonical stock Save/Load value only for readback.
            return expected.Select(e=>{var node=new ConfigNode();new ResourceRatio(e.Resource,e.UnitsPerSecond,false).Save(node);var native=new ResourceRatio();native.Load(node);if(!Finite(native.Ratio)||native.Ratio<0||native.Ratio>1e9)throw new InvalidOperationException("Decoded native production ratio is invalid.");return new ColonyProductionRate{Resource=e.Resource,UnitsPerSecond=native.Ratio};}).ToList();
        }
        static void RequireProductionRecipeVector(System.Collections.Generic.IEnumerable<ResourceRatio> actual,System.Collections.Generic.List<ColonyProductionRate> expected)
        {
            var nativeExpected=ProductionNativeRates(expected);
            var rows=actual.ToArray();if(rows.Length!=nativeExpected.Count||rows.Length>16||rows.Select(r=>r.ResourceName).Distinct(StringComparer.Ordinal).Count()!=rows.Length||rows.Any(r=>!Finite(r.Ratio)||r.Ratio<0||!nativeExpected.Any(e=>e.Resource==r.ResourceName&&e.UnitsPerSecond==r.Ratio)))throw new InvalidOperationException("Current native final recipe differs from the exact reviewed installed input/output ratios.");
        }
        static double RequireProductionBrpRecipeVector(System.Collections.Generic.List<ColonyProductionRate> expectedInputs,System.Collections.Generic.List<ColonyProductionRate> expectedOutputs,System.Collections.Generic.List<ColonyProductionRate> actualInputs,System.Collections.Generic.List<ColonyProductionRate> actualOutputs)
        {
            var nativeInputs=ProductionNativeRates(expectedInputs);var nativeOutputs=ProductionNativeRates(expectedOutputs);
            var first=nativeOutputs.FirstOrDefault(r=>r.UnitsPerSecond>0);var observed=first==null?null:actualOutputs.SingleOrDefault(r=>r.Resource==first.Resource);
            double scale=observed==null?double.NaN:observed.UnitsPerSecond/first.UnitsPerSecond;
            if(!Finite(scale)||scale<=0||scale>10000)throw new InvalidOperationException("Current native background production efficiency is unavailable or unbounded.");
            foreach(var pair in new[]{Tuple.Create(nativeInputs,actualInputs),Tuple.Create(nativeOutputs,actualOutputs)})
            {if(pair.Item1.Count!=pair.Item2.Count||pair.Item1.Any(expected=>!pair.Item2.Any(actual=>actual.Resource==expected.Resource&&Math.Abs(actual.UnitsPerSecond-expected.UnitsPerSecond*scale)<=Math.Max(1e-12,expected.UnitsPerSecond*scale*1e-8))))throw new InvalidOperationException("Exact background production recipe differs from reviewed ratios or native scale.");}
            return scale;
        }
        void RequireProductionStartConditions(ColonyPlan plan,ColonyProductionInvestment item,PartModule native,bool farm,ColonyEnvironment observedEnvironment=null)
        {
            var env=observedEnvironment??GetEnvironment();var facility=ProductionFacility(plan,farm?item.BuildingId:item.HopperBuildingId);
            var report=env.Services.Utilities.SingleOrDefault(u=>u.FacilityId==facility.Id&&u.ContextKey==env.ContextKey&&env.Ut>=u.ObservedUt&&env.Ut-u.ObservedUt<=10);
            if(report==null||!ColonyUtilityQualification.PowerSupported(report)||!ColonyUtilityQualification.HeatSupported(report)||!report.BackgroundProviderQualified)throw new InvalidOperationException("Production start needs current actual continuous power, heat and qualified background provider.");
            var converter=(BaseConverter)native;var option=(AbstractSwapOption)ReviewedPrivateField(native,"_swapOption");
            if(option==null||option.inputList.Count>16||option.outputList.Count>16||option.reqList.Count>16)throw new InvalidOperationException("Bounded current native option is unavailable.");
            double ec=option.inputList.Where(r=>r.ResourceName=="ElectricCharge").Sum(r=>r.Ratio);
            double thermal;if(!TryCurveMaximum(converter.ThermalEfficiency,out thermal))throw new InvalidOperationException("Native prospective thermal multiplier is not bounded.");
            double modifiers=Convert.ToDouble(ReviewedPrivateField(converter,"_totalEfficiencyModifiers")??double.NaN,CultureInfo.InvariantCulture);
            double crew=option.UseSpecialistBonus?converter.SpecialistBonusBase+6*converter.SpecialistEfficiencyFactor:1;
            double full=ec*Math.Max(1,thermal)*Math.Max(1,converter.EfficiencyBonus)*Math.Max(1,modifiers)*Math.Max(1,crew);
            if(!Finite(full)||full<0||full>1e9||!Finite(modifiers)||modifiers<0||modifiers>10000||report.NominalGenerationEcPerSecond.GetValueOrDefault()<1.1*(report.NominalDemandEcPerSecond.GetValueOrDefault()+(converter.IsActivated?0:full)))throw new InvalidOperationException("Actual power budget cannot cover the selected native option's conservative full-use prospective demand with the required 10% headroom.");
            foreach(var input in option.inputList.Where(r=>r.ResourceName!="ElectricCharge"&&r.Ratio>0))
            {var definition=PartResourceLibrary.Instance.GetDefinition(input.ResourceName);if(definition==null)throw new InvalidOperationException("Unknown physical input.");var mode=input.FlowMode==ResourceFlowMode.NULL?definition.resourceFlowMode:input.FlowMode;native.part.GetConnectedResourceTotals(definition.id,mode,out double amount,out double capacity);if(!Finite(amount)||amount<=0||!Finite(capacity)||capacity<amount)throw new InvalidOperationException("Selected production recipe needs actual connected "+input.ResourceName+" stock.");}
            foreach(var requirement in option.reqList.Where(r=>r.Ratio!=0))
            {var tank=native.part.Resources.Get(requirement.ResourceName);if(tank==null||!Finite(tank.amount)||!Finite(tank.maxAmount)||tank.amount<0||tank.amount>tank.maxAmount)throw new InvalidOperationException("Production requires a valid actual installed "+requirement.ResourceName+" tank.");
                double fraction=requirement.Ratio>0?tank.amount/requirement.Ratio:1-tank.amount/Math.Abs(requirement.Ratio);
                if(!Finite(fraction)||fraction<=0||!converter.IsActivated&&requirement.Ratio>0&&tank.amount<requirement.Ratio)throw new InvalidOperationException(converter.IsActivated?"Current native installed requirement prevents production; actual efficiency and returned output must remain positive.":"Production start requires full installed "+requirement.ResourceName+" inventory, not a capacity reading.");}
            if(farm)
            {
                var template=env.Templates.Single(t=>t.Id==item.Recipe.TemplateId&&t.Hash==item.Recipe.TemplateHash);string witness;
                if(!QualifyActualStaffing(native.vessel,template,out witness)||!native.part.protoModuleCrew.Any(c=>c!=null&&c.HasEffect(item.Recipe.NativeExperienceEffect)&&plan.Quote.BootstrapWorkers.Where(w=>w.BuildingId==item.BuildingId).Any(w=>w.Name==c.name)))throw new InvalidOperationException("Cultivation requires the exact approved physically present BotanySkill worker.");
            }
            else if(native is WOLF_HopperModule hopper&&converter.IsActivated==false&&hopper.IsConnectedToDepot==false&&plan.Production.SelectMany(c=>c.Steps).Any(s=>s.PartId==native.part.persistentId&&s.Kind=="hopperConnect"&&s.State=="applied"))throw new InvalidOperationException("Confirmed native hopper connection was lost before start.");
        }
        static string ProductionSettingsWitness(PartModule native,ColonyWolfDepot depot)=>ColonyStateCodec.Hash(Encoding.UTF8.GetBytes(native.vessel.id.ToString("D")+"|"+native.part.persistentId+"|"+NativeModuleId(native)+"|"+((BaseConverter)native).IsActivated+"|"+(native is WOLF_HopperModule h?h.HopperId+"|"+h.IsConnectedToDepot:"")+"|"+ReviewedPrivateField(native,"_swapOption")?.GetType().FullName+"|"+(depot==null?"":ColonyStateCodec.WolfDepotHash(depot))));
    }
}
