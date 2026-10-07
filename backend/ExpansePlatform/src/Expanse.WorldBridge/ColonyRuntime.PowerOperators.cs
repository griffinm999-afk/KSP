using System;
using System.Globalization;
using System.Linq;
using System.Text;
using Expanse.Domain.Colonies;

namespace Expanse.WorldBridge
{
    public sealed partial class ColonyRuntime
    {
        // Declared employment in this paid package is separate from native
        // specialist bonuses. This observes a real operator cabin; it grants
        // neither reactor power nor a native production/crew-point multiplier.
        static bool TryPaidPowerOperatorCabin(ColonyFacility facility,Vessel vessel,uint partId,ColonyTemplate template,out string evidence)
        {
            if(facility?.TemplateId=="power-ranger-bank-v1")return TryPaidRangerBankOperatorCabin(facility,vessel,partId,template,out evidence);
            evidence="";
            try
            {
                var runtime=Current;
                if(runtime==null||runtime.state==null||!runtime.Ready||HighLogic.CurrentGame==null||!ReferenceEquals(runtime.selectedGame,HighLogic.CurrentGame)||
                    facility==null||vessel==null||!vessel.LandedOrSplashed||partId==0||facility.State!="commissioning"&&facility.State!="operational")return false;
                if(template==null)template=runtime.templates.SingleOrDefault(t=>t.Id==facility.TemplateId&&t.Hash==facility.TemplateHash);
                if(template==null||template.Id!="power-duna-v1"||template.Workers!=1||template.WorkerTrait!="Engineer"||
                    facility.RequiredWorkers!=1||facility.RequiredTrait!="Engineer"||facility.TemplateId!=template.Id||facility.TemplateHash!=template.Hash||
                    !string.Equals(facility.CraftSha256,template.CraftSha256,StringComparison.OrdinalIgnoreCase)||facility.CraftSha256.Length!=64||
                    facility.FoundationId.Length==0||facility.PlacementWitnessHash.Length!=64||facility.PlacementRequestFingerprint.Length==0||!facility.PartIds.Contains(partId)||
                    facility.VesselId!=vessel.id.ToString("D"))return false;
                var colony=runtime.state.Colonies.SingleOrDefault(c=>c.Facilities.Any(f=>f.Id==facility.Id&&f.ConstructionOrderId==facility.ConstructionOrderId));
                var order=runtime.state.Construction.SingleOrDefault(o=>o.Id==facility.ConstructionOrderId&&o.ColonyId==colony?.Id&&o.FacilityId==facility.Id);
                if(colony==null||order==null||!order.FundsPaid||!order.MaterialsConsumed||order.State!="commissioning"&&order.State!="operational"||
                    order.TemplateId!=template.Id||order.TemplateHash!=template.Hash||order.PlotId!=facility.PlotId||order.Placement.Phase!="Anchored"||
                    order.Placement.OperationId!=facility.PlacementOperationId||order.Placement.RequestFingerprint!=facility.PlacementRequestFingerprint||
                    order.Placement.AfterWitness.Length==0||facility.PlacementWitnessHash!=ColonyStateCodec.Hash(Encoding.UTF8.GetBytes(order.Placement.AfterWitness))||
                    vessel.mainBody==null||vessel.mainBody.bodyName!=colony.Site.Body)return false;
                if(vessel.loaded)
                {
                    if(vessel.parts==null||vessel.parts.Count!=template.ExpectedPartCount||!facility.PartIds.OrderBy(x=>x).SequenceEqual(vessel.parts.Select(p=>p.persistentId).OrderBy(x=>x)))return false;
                    var part=vessel.parts.SingleOrDefault(p=>p!=null&&p.persistentId==partId);
                    if(part==null||part.vessel!=vessel||part.partInfo?.name!="Duna.PDU"||part.CrewCapacity!=2)return false;
                    var marker=part.Modules.OfType<ColonyPlacementMarker>().SingleOrDefault();
                    if(marker==null||!PowerOperatorMarkerMatches(marker.craftPartId,marker.worldId,marker.colonyId,marker.plotId,marker.operationId,marker.requestFingerprint,marker.templateSha256,runtime.state,colony,facility))return false;
                    var reactors=part.Modules.Cast<PartModule>().Where(m=>m.GetType().FullName=="SystemHeat.ModuleSystemHeatFissionReactor").ToArray();
                    var heats=part.Modules.Cast<PartModule>().Where(m=>m.GetType().FullName=="SystemHeat.ModuleSystemHeat").ToArray();
                    if(reactors.Length!=1||heats.Length!=1||!SupportedSystemHeat(reactors[0])||!SupportedSystemHeat(heats[0])||
                        reactors[0].part!=part||heats[0].part!=part||NativeModuleId(reactors[0])==0||NativeModuleId(heats[0])==0||NativeModuleId(reactors[0])==NativeModuleId(heats[0])||
                        !ReferenceEquals(ReviewedPrivateField(reactors[0],"heatModule"),heats[0]))return false;
                    var loop=UtilityRead(heats[0],"Loop");var loopModules=UtilityRead(loop,"LoopModules") as System.Collections.IEnumerable;
                    if(loop==null||loopModules==null||loopModules.Cast<object>().Count(m=>ReferenceEquals(m,heats[0]))!=1)return false;
                    string foundation;double positionError,angleError;
                    if(!ColonyPlacementFoundations.IsHeld(vessel)||!ColonyPlacementFoundations.ReadWitness(vessel,out foundation,out positionError,out angleError)||
                        foundation!=facility.FoundationId||!Finite(positionError)||!Finite(angleError)||positionError<0||angleError<0||positionError>.01||angleError>.01)return false;
                    evidence="Modeled paid Engineer power-operator employment in exact reactor cabin "+partId+"; SystemHeat 0.9.1 reactor/heat module IDs "+NativeModuleId(reactors[0])+"/"+NativeModuleId(heats[0])+". No native specialist bonus or generation is inferred.";
                    return true;
                }
                // Preserve a previously proved operator workplace only with the
                // actual saved paid membership and module IDs. A prefab alone or
                // an uncommissioned proto package can never qualify staffing.
                if(facility.State!="operational"||!facility.Qualification.StaffingQualified||!facility.Qualification.PlacementStable||
                    facility.Qualification.ReactorContinuation==null||facility.Qualification.ReactorContinuation.WorldId!=runtime.state.WorldId||
                    facility.Qualification.ReactorContinuation.VesselId!=facility.VesselId||facility.Qualification.ReactorContinuation.HardwareHash!=ReadReactorHardwareHash(vessel)||
                    vessel.protoVessel==null||vessel.protoVessel.protoPartSnapshots.Count!=template.ExpectedPartCount||
                    !facility.PartIds.OrderBy(x=>x).SequenceEqual(vessel.protoVessel.protoPartSnapshots.Select(p=>p.persistentId).OrderBy(x=>x)))return false;
                var saved=vessel.protoVessel.protoPartSnapshots.SingleOrDefault(p=>p!=null&&p.persistentId==partId);
                if(saved==null||saved.partInfo?.name!="Duna.PDU"||saved.partInfo.partPrefab==null||saved.partInfo.partPrefab.CrewCapacity!=2)return false;
                var markers=saved.modules.Where(m=>m.moduleName==nameof(ColonyPlacementMarker)).ToArray();
                uint craftId;if(markers.Length!=1||!uint.TryParse(markers[0].moduleValues.GetValue("craftPartId"),NumberStyles.None,CultureInfo.InvariantCulture,out craftId))return false;
                var values=markers[0].moduleValues;
                if(!PowerOperatorMarkerMatches(craftId,values.GetValue("worldId"),values.GetValue("colonyId"),values.GetValue("plotId"),values.GetValue("operationId"),values.GetValue("requestFingerprint"),values.GetValue("templateSha256"),runtime.state,colony,facility))return false;
                uint reactorId=0,heatId=0;
                foreach(var type in new[]{"ModuleSystemHeatFissionReactor","ModuleSystemHeat"})
                {
                    var modules=saved.modules.Where(m=>m.moduleName==type).ToArray();var prefabs=saved.partInfo.partPrefab.Modules.Cast<PartModule>().Where(m=>m.GetType().FullName=="SystemHeat."+type).ToArray();
                    uint id;if(modules.Length!=1||prefabs.Length!=1||!SupportedSystemHeat(prefabs[0])||!uint.TryParse(modules[0].moduleValues.GetValue("persistentId"),NumberStyles.None,CultureInfo.InvariantCulture,out id)||id==0)return false;
                    if(type=="ModuleSystemHeat")heatId=id;else reactorId=id;
                }
                if(reactorId==heatId)return false;
                evidence="Retained modeled paid Engineer power-operator cabin "+partId+"; exact current ProtoPart and saved reactor/heat IDs "+reactorId+"/"+heatId+" match paid placement. Fresh thermal/power continuation is separately qualified; no prefab-only staffing or native specialist bonus.";
                return true;
            }
            catch{return false;}
        }
        static bool PowerOperatorMarkerMatches(uint craftId,string world,string colonyId,string plot,string operation,string fingerprint,string sha,ColonyState state,ColonyRecord colony,ColonyFacility facility)=>
            craftId==104&&world==state.WorldId&&colonyId==colony.Id&&plot==facility.PlotId&&operation==facility.PlacementOperationId&&
            fingerprint==facility.PlacementRequestFingerprint&&string.Equals(sha,facility.CraftSha256,StringComparison.OrdinalIgnoreCase);
        static ColonyFacility PowerOperatorFacility(Vessel vessel)=>Current?.state?.Colonies.SelectMany(c=>c.Facilities).SingleOrDefault(f=>f.VesselId==vessel.id.ToString("D"));
    }
}
