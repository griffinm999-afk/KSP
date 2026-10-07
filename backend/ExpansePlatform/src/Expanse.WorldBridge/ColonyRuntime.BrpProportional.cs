using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using Expanse.Domain.Colonies;
using USITools;

namespace Expanse.WorldBridge
{
    public sealed partial class ColonyRuntime
    {
        const string ProportionalContract="colony-proportional15-v1";
        const string ProportionalBehaviour="Expanse.BrpColony.ExpanseColonyProportionalBehaviour";
        static Type ProportionalType(string name)
        {
            var assemblies=AppDomain.CurrentDomain.GetAssemblies().Where(a=>a.GetName().Name=="Expanse.BrpColony"&&a.GetName().Version==new Version(1,0,0,0)).Take(2).ToArray();
            return assemblies.Length==1?assemblies[0].GetType(name,false):null;
        }
        static string ProportionalHash(string value)=>ColonyStateCodec.Hash(Encoding.UTF8.GetBytes(value));
        static string ProportionalSeal(ConfigNode record)
        {
            var copy=record.CreateCopy();copy.RemoveValues("BindingHash");copy.RemoveValues("SampleUt");
            return ProportionalHash(copy.ToString());
        }
        static string ProportionalCrew(Vessel vessel)
        {
            var rows=vessel.GetVesselCrew().OrderBy(c=>c.name,StringComparer.Ordinal).Take(257).ToArray();
            if(rows.Length>256)throw new InvalidOperationException("Proportional native crew bound exceeded.");
            return ProportionalHash(string.Join("|",rows.Select(c=>c.name+":"+c.trait+":"+c.experienceLevel+":"+c.experience.ToString("R",CultureInfo.InvariantCulture))));
        }
        static string ProportionalTopology(Vessel vessel)
        {
            var rows=new List<string>();
            if(vessel.loaded)
            {
                if(vessel.parts==null||vessel.parts.Count>512)throw new InvalidOperationException("Bounded current proportional parts unavailable.");
                foreach(var part in vessel.parts.OrderBy(p=>p.persistentId))
                {
                    if(part?.partInfo?.partConfig==null)throw new InvalidOperationException("Current proportional native part/config absent.");
                    rows.Add(part.persistentId+":"+part.flightID+":"+part.partInfo.name+":"+ProportionalHash(part.partInfo.partConfig.ToString()));
                    foreach(var tank in part.Resources.Cast<PartResource>().OrderBy(r=>r.resourceName,StringComparer.Ordinal))
                        rows.Add(tank.resourceName+":"+tank.maxAmount.ToString("R",CultureInfo.InvariantCulture)+":"+tank.flowState);
                }
            }
            else
            {
                if(vessel.protoVessel?.protoPartSnapshots==null||vessel.protoVessel.protoPartSnapshots.Count>512)throw new InvalidOperationException("Current actual proportional proto parts absent.");
                foreach(var part in vessel.protoVessel.protoPartSnapshots.OrderBy(p=>p.persistentId))
                {
                    if(part?.partInfo?.partConfig==null)throw new InvalidOperationException("Current actual proportional proto definition absent.");
                    rows.Add(part.persistentId+":"+part.flightID+":"+part.partInfo.name+":"+ProportionalHash(part.partInfo.partConfig.ToString()));
                    foreach(var tank in part.resources.OrderBy(r=>r.resourceName,StringComparer.Ordinal))
                        rows.Add(tank.resourceName+":"+tank.maxAmount.ToString("R",CultureInfo.InvariantCulture)+":"+tank.flowState);
                }
            }
            if(rows.Count>4096)throw new InvalidOperationException("Proportional topology bound exceeded.");
            return ProportionalHash(string.Join("|",rows));
        }
        static ColonyFacility RequireProportionalPaidOwner(ConfigNode record,Vessel vessel)
        {
            var runtime=Current;
            if(runtime==null||!runtime.Ready||runtime.state==null||HighLogic.CurrentGame==null||!ReferenceEquals(runtime.selectedGame,HighLogic.CurrentGame)||vessel==null)
                throw new InvalidOperationException("Selected paid colony authority unavailable.");
            var state=runtime.state;
            if(record.GetValue("WorldId")!=state.WorldId||record.GetValue("VesselId")!=vessel.id.ToString("D"))throw new InvalidOperationException("Proportional world/vessel owner changed.");
            var facility=state.Colonies.SelectMany(c=>c.Facilities).SingleOrDefault(f=>f.Id==record.GetValue("FacilityId")&&f.VesselId==vessel.id.ToString("D"));
            var order=facility==null?null:state.Construction.SingleOrDefault(o=>o.Id==facility.ConstructionOrderId);
            if(facility==null||order==null||!order.FundsPaid||!order.MaterialsConsumed||order.WorkCompleted<order.WorkRequired||
                (order.State!="commissioning"&&order.State!="operational")||order.Placement.Phase!="Anchored"||facility.FoundationId.Length==0||
                order.Placement.EscrowWitness!=ColonyEngine.ConstructionEscrowWitness(state,order.Id)||facility.PlacementOperationId!=order.Placement.OperationId||
                facility.PlacementRequestFingerprint!=order.Placement.RequestFingerprint||record.GetValue("OperationId")!=order.Placement.OperationId||
                record.GetValue("RequestFingerprint")!=order.Placement.RequestFingerprint||record.GetValue("TemplateSha256")!=facility.CraftSha256||
                record.GetValue("OrderId")!=order.Id)throw new InvalidOperationException("Proportional owner lacks exact paid built anchored commissioning lineage.");
            if(facility.DevelopmentOnly)
            {
                var colony=state.Colonies.Single(c=>c.Id==order.ColonyId&&c.Facilities.Contains(facility));
                if(!colony.Charter.Sandbox||!runtime.IsAuthorizedDevelopmentContext()||!IsAuthorizedIsolatedConstruction())
                    throw new InvalidOperationException("Proportional development owner lacks exact isolated sandbox authorization.");
            }
            uint[] ids=vessel.loaded?vessel.parts.Select(p=>p.persistentId).ToArray():vessel.protoVessel.protoPartSnapshots.Select(p=>p.persistentId).ToArray();
            if(!ids.OrderBy(i=>i).SequenceEqual(facility.PartIds.OrderBy(i=>i)))throw new InvalidOperationException("Actual paid proportional part membership changed.");
            uint partId=uint.Parse(record.GetValue("PartId"),CultureInfo.InvariantCulture),craftId=uint.Parse(record.GetValue("CraftPartId"),CultureInfo.InvariantCulture);
            if(!facility.PartIds.Contains(partId))throw new InvalidOperationException("Proportional part is outside paid membership.");
            ConfigNode marker;
            if(vessel.loaded)
            {
                var part=vessel.parts.Single(p=>p.persistentId==partId);var actual=part.Modules.OfType<ColonyPlacementMarker>().Single();
                marker=new ConfigNode();marker.AddValue("worldId",actual.worldId);marker.AddValue("operationId",actual.operationId);marker.AddValue("requestFingerprint",actual.requestFingerprint);marker.AddValue("templateSha256",actual.templateSha256);marker.AddValue("craftPartId",actual.craftPartId);
            }
            else marker=vessel.protoVessel.protoPartSnapshots.Single(p=>p.persistentId==partId).modules.Single(m=>m.moduleName==nameof(ColonyPlacementMarker)).moduleValues;
            if(marker.GetValue("worldId")!=state.WorldId||marker.GetValue("operationId")!=order.Placement.OperationId||marker.GetValue("requestFingerprint")!=order.Placement.RequestFingerprint||
                marker.GetValue("templateSha256")!=facility.CraftSha256||marker.GetValue("craftPartId")!=craftId.ToString(CultureInfo.InvariantCulture))throw new InvalidOperationException("Actual proportional marker lineage changed.");
            var template=runtime.templates.SingleOrDefault(t=>t.Id==facility.TemplateId&&t.Hash==facility.TemplateHash&&t.CraftSha256==facility.CraftSha256);
            if(template==null||!ReadConstructionCraft(template).TryGetValue(craftId,out string expected)||expected!=record.GetValue("PartName"))throw new InvalidOperationException("Proportional part is not the paid reviewed craft mapping.");
            if(record.GetValue("Profile")=="ranger")
            {
                var paid=template.StartupContents.SingleOrDefault(r=>r.CraftPartId==craftId&&r.ResourceName=="Plutonium-238");
                if(paid==null||paid.Amount<=0||paid.Amount>20*ColonyLimits.Units||!ColonyEngine.IsConstructionActivationEffect(state,order.Id,ColonyEngine.ConstructionActivationId(order))||
                    state.Effects.Single(e=>e.Id==ColonyEngine.ConstructionActivationId(order)).State!="applied")throw new InvalidOperationException("Proportional fixed source lacks paid fuel/actual startup receipt.");
            }
            else if(record.GetValue("Profile")=="cultivate-s")
            {
                bool started=state.Plans.Where(p=>p.State!="cancelled").SelectMany(p=>p.Production).SelectMany(c=>c.Steps).Any(s=>s.Kind=="converterStart"&&s.State=="applied"&&s.FacilityId==facility.Id&&s.PartId==partId&&s.ModuleId.ToString(CultureInfo.InvariantCulture)==record.GetValue("ModuleId")&&s.OptionHash==record.GetValue("OptionHash"));
                if(!started)throw new InvalidOperationException("Proportional cultivation lacks exact paid production startup receipt.");
            }
            return facility;
        }
        static void ProportionalRows(ConfigNode record,string kind,IEnumerable<ResourceRatio> rows)
        {
            foreach(var row in rows)
            {
                var definition=PartResourceLibrary.Instance.GetDefinition(row.ResourceName);if(definition==null)throw new InvalidOperationException("Unknown proportional native resource.");
                var flow=row.FlowMode==ResourceFlowMode.NULL?definition.resourceFlowMode:row.FlowMode;
                if(!Enum.IsDefined(typeof(ResourceFlowMode),flow)||flow==ResourceFlowMode.NULL)throw new InvalidOperationException("Unknown proportional native flow.");
                var node=record.AddNode(kind);node.AddValue("ResourceName",row.ResourceName);node.AddValue("Ratio",row.Ratio.ToString("R",CultureInfo.InvariantCulture));node.AddValue("DumpExcess",row.DumpExcess);node.AddValue("FlowMode",flow);
            }
        }
        public static ConfigNode CaptureColonyBrpRecipe(PartModule owner)
        {
            if(!ColonyBrpColdLoadAddon.Installed)throw new InvalidOperationException(ColonyBrpColdLoadAddon.Failure);
            var module=owner as USI_Converter;
            if(module==null||module.GetType()!=typeof(USI_Converter)||module.part?.partInfo==null||module.vessel==null||!module.vessel.loaded||Current==null)throw new InvalidOperationException("Actual loaded proportional source unavailable.");
            bool fixedSource=IsReviewedFixedGenerator(module);
            if(!fixedSource&&module.part.partInfo.name!="Duna.Agriculture")throw new InvalidOperationException("Unsupported colony proportional hardware.");
            if(!module.IsActivated||!module.isEnabled||!UtilityBool(module,"moduleIsEnabled")||module.ConvertByMass||module.GeneratesHeat||module.TakeAmount!=1||
                !(ReviewedPrivateField(module,"_preCalculateEfficiency") is bool pre)||pre||Convert.ToDouble(ReviewedPrivateField(module,"_totalEfficiencyModifiers"),CultureInfo.InvariantCulture)!=1)
                throw new InvalidOperationException("Unsupported proportional native settings/efficiency callbacks.");
            if(fixedSource)RequireFixedGeneratorHardware(module);
            double ut=Planetarium.GetUniversalTime();ResourceRatio[] ignored;string reason;
            if(!TryReadNativeRecipeInputs(module,Current.ContextKey,ut,out ignored,out reason)||!UtilityRecipes.TryGetValue(module,out UtilityRecipeSample sample)||!sample.MultiplierObserved||sample.MultiplierUt!=sample.Ut||ut<sample.MultiplierUt||ut-sample.MultiplierUt>10||sample.NativeMultiplier!=(double)(float)module.GetEfficiencyMultiplier())
                throw new InvalidOperationException("Proportional final native recipe and current post-process efficiency not observed together.");
            var marker=module.part.Modules.OfType<ColonyPlacementMarker>().Single();
            var facility=Current.state.Colonies.SelectMany(c=>c.Facilities).Single(f=>f.VesselId==module.vessel.id.ToString("D")&&f.PartIds.Contains(module.part.persistentId));
            var record=new ConfigNode("COLONY_RECIPE");
            record.AddValue("Contract",ProportionalContract);record.AddValue("Status","supported");record.AddValue("Profile",fixedSource?"ranger":"cultivate-s");record.AddValue("WorldId",marker.worldId);record.AddValue("VesselId",module.vessel.id.ToString("D"));
            record.AddValue("FacilityId",facility.Id);record.AddValue("OrderId",facility.ConstructionOrderId);record.AddValue("OperationId",marker.operationId);record.AddValue("RequestFingerprint",marker.requestFingerprint);record.AddValue("TemplateSha256",marker.templateSha256);record.AddValue("CraftPartId",marker.craftPartId);
            record.AddValue("PartId",module.part.persistentId);record.AddValue("FlightId",module.part.flightID);record.AddValue("ModuleId",NativeModuleId(module));record.AddValue("PartName",module.part.partInfo.name);record.AddValue("Multiplier",sample.NativeMultiplier.ToString("R",CultureInfo.InvariantCulture));record.AddValue("EfficiencyBonus",module.EfficiencyBonus.ToString("R",CultureInfo.InvariantCulture));
            record.AddValue("FillAmount",module.FillAmount.ToString("R",CultureInfo.InvariantCulture));record.AddValue("TakeAmount",module.TakeAmount.ToString("R",CultureInfo.InvariantCulture));record.AddValue("SampleUt",ut.ToString("R",CultureInfo.InvariantCulture));record.AddValue("CrewHash",ProportionalCrew(module.vessel));record.AddValue("TopologyHash",ProportionalTopology(module.vessel));
            if(!fixedSource)
            {
                var options=module.part.partInfo.partConfig.GetNodes("MODULE").Where(n=>n.GetValue("name")=="USI_ConverterSwapOption").ToArray();
                if(options.Length<=1||options[1].GetValue("ConverterName")!="Cultivate(S)")throw new InvalidOperationException("Installed Cultivate(S) option changed.");
                string optionHash=ProductionOptionHash(options[1]);RequireProductionLoadout(module,1,optionHash,"USI_ConverterSwapOption");
                if(module.Addons==null||module.Addons.Count!=1||module.Addons[0].GetType()!=typeof(USI_EfficiencyConsumerAddonForConverters))throw new InvalidOperationException("Unreviewed cultivation efficiency addon set.");
                record.AddValue("OptionHash",optionHash);
            }
            ProportionalRows(record,"INPUT_RESOURCE",sample.Inputs);ProportionalRows(record,"OUTPUT_RESOURCE",sample.Outputs);ProportionalRows(record,"REQUIRED_RESOURCE",sample.Requirements);
            RequireProportionalPaidOwner(record,module.vessel);record.AddValue("BindingHash",ProportionalSeal(record));return record;
        }
        public static string ValidateColonyBrpRecipe(ConfigNode record,Vessel vessel)
        {
            try
            {
                if(record==null||record.GetValue("Contract")!=ProportionalContract||record.GetValue("Status")!="supported"||record.GetValue("BindingHash")!=ProportionalSeal(record))throw new InvalidOperationException("Proportional source seal missing/changed.");
                RequireProportionalPaidOwner(record,vessel);
                if(record.GetValue("CrewHash")!=ProportionalCrew(vessel)||record.GetValue("TopologyHash")!=ProportionalTopology(vessel))throw new InvalidOperationException("Current proportional crew/configuration/physical topology changed; loaded recapture required.");
                uint flight=uint.Parse(record.GetValue("FlightId"),CultureInfo.InvariantCulture),id=uint.Parse(record.GetValue("ModuleId"),CultureInfo.InvariantCulture);
                if(vessel.loaded)
                {
                    var module=vessel.parts.Single(p=>p.flightID==flight).Modules.Cast<PartModule>().OfType<USI_Converter>().Single(m=>NativeModuleId(m)==id);
                    var fresh=CaptureColonyBrpRecipe(module);
                    if(fresh.GetValue("BindingHash")!=record.GetValue("BindingHash"))throw new InvalidOperationException("Current final proportional native recipe/efficiency differs from captured owner.");
                }
                else
                {
                    var part=vessel.protoVessel.protoPartSnapshots.Single(p=>p.flightID==flight&&p.persistentId.ToString(CultureInfo.InvariantCulture)==record.GetValue("PartId")&&p.partInfo.name==record.GetValue("PartName"));
                    var saved=part.modules.Single(m=>m.moduleName=="USI_Converter"&&m.moduleValues.GetValue("persistentId")==id.ToString(CultureInfo.InvariantCulture)).moduleValues;
                    if(!bool.TryParse(saved.GetValue("IsActivated"),out bool active)||!active||!bool.TryParse(saved.GetValue("isEnabled"),out bool enabled)||!enabled||!double.TryParse(saved.GetValue("EfficiencyBonus"),NumberStyles.Float,CultureInfo.InvariantCulture,out double bonus)||bonus!=double.Parse(record.GetValue("EfficiencyBonus"),CultureInfo.InvariantCulture))throw new InvalidOperationException("Actual saved proportional activation/efficiency changed.");
                    if(record.GetValue("Profile")=="cultivate-s")
                    {
                        var bay=part.modules.Single(m=>m.moduleName=="USI_SwappableBay");var option=part.partInfo.partConfig.GetNodes("MODULE").Where(n=>n.GetValue("name")=="USI_ConverterSwapOption").ElementAt(1);
                        if(bay.moduleValues.GetValue("currentLoadout")!="1"||ProductionOptionHash(option)!=record.GetValue("OptionHash"))throw new InvalidOperationException("Actual saved cultivation option changed.");
                    }
                }
                return "";
            }
            catch(Exception ex){return "Proportional owner hold: "+Bound((ex.InnerException??ex).Message,1000);}
        }
        static void RequireProportionalObservation(object converter,Vessel vessel,ProtoPartSnapshot part,uint moduleId,double ut,object processor)
        {
            if(part.partInfo?.name!="Ranger.PowerPack"&&part.partInfo?.name!="Duna.Agriculture")return;
            string cold=ColonyBrpColdLoadAddon.ProcessorFailure(processor);if(cold.Length>0)throw new InvalidOperationException(cold);
            var behaviour=UtilityRead(converter,"Behaviour");var type=ProportionalType(ProportionalBehaviour);
            if(type==null||behaviour==null||behaviour.GetType()!=type||Convert.ToString(UtilityRead(behaviour,"Contract"),CultureInfo.InvariantCulture)!=ProportionalContract||
                !UtilityBool(behaviour,"Bound")||Convert.ToString(UtilityRead(behaviour,"HoldReason"),CultureInfo.InvariantCulture)!="")throw new InvalidOperationException("Exact paid native proportional owner is absent, mismatched or held.");
            double sample=UtilityNumber(behaviour,"SampleUt",double.NaN),factor=UtilityNumber(behaviour,"Factor",double.NaN);
            if(!Finite(sample)||sample>ut||!Finite(factor)||factor<=0||factor>1)throw new InvalidOperationException("Current proportional observation unavailable.");
            var record=UtilityRead(behaviour,"CurrentRecord") as ConfigNode;
            if(record==null||record.GetValue("FlightId")!=part.flightID.ToString(CultureInfo.InvariantCulture)||record.GetValue("ModuleId")!=moduleId.ToString(CultureInfo.InvariantCulture))throw new InvalidOperationException("Proportional observation identity changed.");
            string reason=ValidateColonyBrpRecipe(record,vessel);if(reason.Length>0)throw new InvalidOperationException(reason);
            double current=1;
            foreach(var req in record.GetNodes("REQUIRED_RESOURCE"))
            {
                var tank=part.resources.Single(r=>r.resourceName==req.GetValue("ResourceName"));double ratio=double.Parse(req.GetValue("Ratio"),CultureInfo.InvariantCulture);
                current=Math.Min(current,ratio>0?tank.amount/ratio:1-tank.amount/Math.Abs(ratio));
            }
            current=current<=1e-9?0:Math.Max(0,Math.Min(1,current));
            if(current<=0||Math.Abs(current-factor)>Math.Max(1e-12,current*1.001e-4))throw new InvalidOperationException("Current proportional factor differs beyond its native changepoint bound.");
        }
        internal static string InvalidateColonyBrpAfterMutation(object processor,double ut)
        {
            try
            {
                var type=ProportionalType(ProportionalBehaviour);if(type==null)return "";
                var method=type.GetMethod("InvalidateAfterPhysicalMutation",BindingFlags.Static|BindingFlags.Public,null,new[]{typeof(object),typeof(double)},null);
                if(method==null)return "Proportional physical refresh entry unavailable.";
                return method.Invoke(null,new[]{processor,(object)ut}) as string??"Proportional physical refresh returned no result.";
            }
            catch(Exception ex){return "Proportional physical refresh failed: "+Bound((ex.InnerException??ex).Message,512);}
        }
    }
}
