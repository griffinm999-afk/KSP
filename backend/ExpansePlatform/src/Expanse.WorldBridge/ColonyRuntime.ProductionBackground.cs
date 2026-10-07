using System;
using System.Collections;
using System.Globalization;
using System.Linq;
using Expanse.Domain.Colonies;
using WOLF;

namespace Expanse.WorldBridge
{
    public sealed partial class ColonyRuntime
    {
        bool TryProductionBackground(ColonyPlan plan,ColonyProductionInvestment item,ColonyProductionClaim claim,ColonyEnvironment env,ColonyProductionObservation observation)
        {
            var facility=ProductionFacility(plan,item.BuildingId);var vessel=FlightGlobals.Vessels.SingleOrDefault(v=>v!=null&&v.id.ToString("D")==facility.VesselId);
            if(vessel==null||vessel.loaded)return false;
            observation.Provider="Installed BRP current exact recipe continuation; rates estimated by native processor, no gross-output measurement or managed stock credit.";
            var part=ResolveProductionProto(plan,item.BuildingId,item.Recipe.CraftPartId,item.Recipe.PartName,item.Recipe.ModuleName,item.Recipe.OptionIndex,item.Recipe.OptionHash,"USI_ConverterSwapOption",claim.Steps.Last());
            RequireProductionBackgroundUtility(facility,env);
            WOLF_ScenarioModule scenario;IRegistryCollection registry;string reason;
            if(!TryWolfRegistry(out scenario,out registry,out reason))throw new InvalidOperationException(reason);
            foreach(var feed in item.Recipe.Feeds)
            {
                var step=claim.Steps.Single(s=>s.Kind=="hopperConnect"&&s.OptionHash==feed.OptionHash);
                var proto=ResolveProductionProto(plan,item.HopperBuildingId,feed.CraftPartId,feed.PartName,"WOLF_HopperModule",feed.OptionIndex,feed.OptionHash,"WOLF_HopperSwapOption",step);
                var saved=proto.modules.Single(m=>m.moduleName=="WOLF_HopperModule"&&m.moduleValues.GetValue("persistentId")==step.ModuleId.ToString(CultureInfo.InvariantCulture));
                if(!bool.TryParse(saved.moduleValues.GetValue("IsConnectedToDepot"),out bool connected)||!connected||saved.moduleValues.GetValue("HopperId")!=step.HopperId||step.HopperId.Length==0||!registry.GetHoppers().Any(h=>h.Id==saved.moduleValues.GetValue("HopperId")&&h.Depot!=null&&h.Depot.Body==plan.Quote.ProductionInvestments[0].Wolf.Before.Body&&h.Depot.Biome==plan.Quote.ProductionInvestments[0].Wolf.Before.Biome))throw new InvalidOperationException("Actual saved hopper connection and native registry receipt are absent.");
                var metadata=registry.GetHoppers().Single(h=>h.Id==saved.moduleValues.GetValue("HopperId"));
                if(!ProductionIngredientsSame(metadata.Recipe.InputIngredients,new System.Collections.Generic.Dictionary<string,int>{{feed.Resource,feed.WolfPoints}})||metadata.Recipe.OutputIngredients.Count!=0)throw new InvalidOperationException("Saved native feed allocation differs from the approved exact raw-resource recipe.");
                var feedVessel=FlightGlobals.Vessels.Single(v=>v!=null&&v.id.ToString("D")==step.VesselId);
                var option=proto.partInfo.partConfig.GetNodes("MODULE").Where(n=>n.GetValue("name")=="WOLF_HopperSwapOption").ElementAt(feed.OptionIndex);
                ReadProductionBrpRecipe(feedVessel,proto,step.ModuleId,env,ProductionConfigRates(option,"INPUT_RESOURCE"),ProductionConfigRates(option,"OUTPUT_RESOURCE"),new ColonyProductionObservation());
                RequireProductionBackgroundUtility(ProductionFacility(plan,item.HopperBuildingId),env);
            }
            var seat=env.People.Seats.SingleOrDefault(s=>s.FacilityId==facility.Id&&s.PartId==part.persistentId&&s.VesselId==facility.VesselId&&s.Current&&s.ContextKey==env.ContextKey&&s.UtilitiesQualified);
            if(seat==null||!env.People.PresenceComplete||!plan.Quote.BootstrapWorkers.Where(w=>w.BuildingId==item.BuildingId).Any(w=>seat.Occupants.Contains(w.RosterId)&&env.People.Roster.Any(p=>p.RosterId==w.RosterId&&p.Current&&p.ContextKey==env.ContextKey&&p.VesselId==facility.VesselId&&p.PartId==part.persistentId&&p.Trait=="Scientist"&&!p.ProtectedMissionCrew)&&HighLogic.CurrentGame.CrewRoster[w.RosterId]?.HasEffect(item.Recipe.NativeExperienceEffect)==true))throw new InvalidOperationException("Current exact background work cabin lacks its reviewed ordinary BotanySkill Scientist.");
            ReadProductionBrpRecipe(vessel,part,observation.ModuleId,env,item.Recipe.Inputs,item.Recipe.Outputs,observation);
            if(!observation.ObservedRecipeOutputs.Any(r=>r.Resource=="Supplies"&&r.UnitsPerSecond>0))throw new InvalidOperationException("Actual native BRP recipe has no positive Supplies output estimate.");
            observation.Active=true;
            if(claim.OutputWitness.Length==0)throw new InvalidOperationException("Native background continuation is present, but first productive delivery still needs an actual loaded broker receipt; idle commissioning cannot prove output.");
            observation.Qualified=true;observation.Witness=claim.OutputWitness;observation.Reason="Current exact active BRP module, feed/input, physical Scientist, power/heat and background provider qualified. Prior actual delivered-output receipt retained; current BRP rates are estimates, not measured gross output.";
            return true;
        }
        void RequireProductionBackgroundUtility(ColonyFacility facility,ColonyEnvironment env)
        {
            var report=env.Services.Utilities.SingleOrDefault(u=>u.FacilityId==facility.Id&&u.ContextKey==env.ContextKey&&env.Ut>=u.ObservedUt&&env.Ut-u.ObservedUt<=10);
            if(report==null||!ColonyUtilityQualification.PowerSupported(report)||!ColonyUtilityQualification.HeatSupported(report)||!report.InputsAccessible||!report.BackgroundProviderQualified)throw new InvalidOperationException("Current actual background power, heat or physical feed path is unqualified.");
        }
        void ReadProductionBrpRecipe(Vessel vessel,ProtoPartSnapshot part,uint moduleId,ColonyEnvironment env,System.Collections.Generic.List<ColonyProductionRate> expectedInputs,System.Collections.Generic.List<ColonyProductionRate> expectedOutputs,ColonyProductionObservation observation)
        {
            var processors=((IEnumerable)UtilityRead(vessel,"vesselModules")).Cast<object>().Where(p=>p?.GetType().FullName=="BackgroundResourceProcessing.BackgroundResourceProcessor").ToArray();
            if(!RemoteBrpInventoryGateway.SupportedProviderAvailable||processors.Length!=1)throw new InvalidOperationException("Supported exact native BRP processor is unavailable.");
            double age=env.Ut-UtilityNumber(processors[0],"LastChangepoint",double.NaN);if(!Finite(age)||age<0||age>10)throw new InvalidOperationException("BRP recipe continuation is stale after native catch-up.");
            var converters=((IEnumerable)UtilityRead(processors[0],"Converters")).Cast<object>().Take(513).ToArray();if(converters.Length>512)throw new InvalidOperationException("BRP recipe vector exceeds its bound.");
            var mapped=converters.Where(c=>UtilityRead(c,"FlightId") is uint flight&&flight==part.flightID&&UtilityRead(c,"ModuleId") is uint id&&id==moduleId).ToArray();
            if(mapped.Length==0||mapped.Length>16)throw new InvalidOperationException("Current native processor has no exact saved production module mapping.");
            foreach(var converter in mapped)
            {
                RequireProportionalObservation(converter,vessel,part,moduleId,env.Ut,processors[0]);
                string status=Convert.ToString(UtilityRead(converter,"ConstraintState"),CultureInfo.InvariantCulture);double rate=UtilityNumber(converter,"Rate",double.NaN);
                if(!Finite(rate)||rate<=0||rate>1||status!="ENABLED"&&status!="BOUNDARY")throw new InvalidOperationException("Current native background recipe is not productive: "+status+".");
                foreach(var pair in new[]{Tuple.Create("Inputs",observation.ObservedRecipeInputs),Tuple.Create("Outputs",observation.ObservedRecipeOutputs)})
                {
                    var rows=((IEnumerable)UtilityRead(converter,pair.Item1)).Cast<object>().Select(e=>UtilityRead(e,"Value")).Take(17).ToArray();if(rows.Length>16)throw new InvalidOperationException("Background recipe vector exceeds16.");
                    foreach(var row in rows){string resource=Convert.ToString(UtilityRead(row,"ResourceName"),CultureInfo.InvariantCulture);double ratio=UtilityNumber(row,"Ratio",double.NaN);if(string.IsNullOrEmpty(resource)||!Finite(ratio)||ratio<0)throw new InvalidOperationException("Background physical recipe ratio is invalid.");var existing=pair.Item2.SingleOrDefault(r=>r.Resource==resource);if(existing==null)pair.Item2.Add(new ColonyProductionRate{Resource=resource,UnitsPerSecond=ratio*rate});else existing.UnitsPerSecond+=ratio*rate;}
                }
            }
            // Native BRP may bake current efficiency into its ratios. Preserve
            // one common actual native scale, rather than multiplying it again;
            // every reviewed physical input/output must still match that vector.
            observation.CurrentNativeEfficiency=RequireProductionBrpRecipeVector(expectedInputs,expectedOutputs,observation.ObservedRecipeInputs,observation.ObservedRecipeOutputs);
        }
        ProtoPartSnapshot ResolveProductionProto(ColonyPlan plan,string buildingId,uint craftId,string partName,string moduleName,int optionIndex,string optionHash,string optionModule,ColonyProductionStep step)
        {
            var facility=ProductionFacility(plan,buildingId);var vessel=FlightGlobals.Vessels.SingleOrDefault(v=>v!=null&&v.id.ToString("D")==facility.VesselId);
            if(vessel==null||vessel.loaded||!vessel.LandedOrSplashed||vessel.protoVessel==null||vessel.protoVessel.protoPartSnapshots.Count>512||!facility.PartIds.OrderBy(p=>p).SequenceEqual(vessel.protoVessel.protoPartSnapshots.Select(p=>p.persistentId).OrderBy(p=>p)))throw new InvalidOperationException("Actual registered background production membership changed.");
            RequireProductionLocation(plan,facility,vessel);
            var part=vessel.protoVessel.protoPartSnapshots.SingleOrDefault(p=>p.persistentId==step.PartId&&p.partInfo?.name==partName);
            if(part==null)throw new InvalidOperationException("Exact saved production part is unavailable.");
            var marker=part.modules.SingleOrDefault(m=>m.moduleName==nameof(ColonyPlacementMarker))?.moduleValues;
            if(marker==null||marker.GetValue("craftPartId")!=craftId.ToString(CultureInfo.InvariantCulture)||marker.GetValue("worldId")!=state.WorldId||marker.GetValue("colonyId")!=plan.ColonyId||marker.GetValue("plotId")!=facility.PlotId||marker.GetValue("operationId")!=facility.PlacementOperationId||marker.GetValue("requestFingerprint")!=facility.PlacementRequestFingerprint||marker.GetValue("templateSha256")!=facility.CraftSha256)throw new InvalidOperationException("Saved production mapping lost its exact placement lineage.");
            var module=part.modules.SingleOrDefault(m=>m.moduleName==moduleName&&m.moduleValues.GetValue("persistentId")==step.ModuleId.ToString(CultureInfo.InvariantCulture));
            var bay=part.modules.SingleOrDefault(m=>m.moduleName==(moduleName=="WOLF_HopperModule"?"WOLF_HopperBay":"USI_SwappableBay"));
            var options=part.partInfo.partConfig.GetNodes("MODULE").Where(m=>m.GetValue("name")==optionModule).ToArray();
            if(module==null||!bool.TryParse(module.moduleValues.GetValue("IsActivated"),out bool active)||!active||bay==null||bay.moduleValues.GetValue("currentLoadout")!=optionIndex.ToString(CultureInfo.InvariantCulture)||optionIndex>=options.Length||ProductionOptionHash(options[optionIndex])!=optionHash)throw new InvalidOperationException("Current saved native activation/loadout differs from the approved exact recipe.");
            return part;
        }
    }
}
