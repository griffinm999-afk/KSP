using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Expanse.Domain;
using Expanse.Domain.Colonies;
using UnityEngine;

namespace Expanse.WorldBridge
{
    public sealed partial class ColonyRuntime
    {
        // Main-thread proposals in the legacy bridge use this exact tank claim,
        // after checking an already retained terminal receipt. Unrelated tanks
        // remain available; a colony hold already supplies the global fence.
        public bool IsPhysicalTankReserved(string depotId,uint partId,string resource)
        {
            if(state==null)return false;
            return state.PhysicalTransfers.Any(o=>ColonyEngine.PhysicalPending(o)&&o.DepotId==depotId&&o.PartId==partId&&o.Resource==resource) ||
                state.ServiceOperations.Any(o=>(o.State=="reserved"||o.State=="held")&&o.DepotId==depotId&&o.PartId==partId&&o.DestinationResource==resource);
        }
        public PhysicalTankReservation[] GetPhysicalTankReservations()
        {
            if(state==null)return new PhysicalTankReservation[0];
            return state.PhysicalTransfers.Where(ColonyEngine.PhysicalPending).Select(o=>new PhysicalTankReservation {DepotId=o.DepotId,PartId=o.PartId,Resource=o.Resource})
                .Concat(state.ServiceOperations.Where(o=>o.State=="reserved"||o.State=="held").Select(o=>new PhysicalTankReservation {DepotId=o.DepotId,PartId=o.PartId,Resource=o.DestinationResource})).ToArray();
        }
        sealed class LocalDepotObservation {public InventoryObservation Observation;public InventoryCapabilityEvidence Capability;public string Hash,Reason;}
        sealed class LocalRecipeObservation {public HashSet<string> Inputs;public bool Complete;}
        static bool WarehouseCommodity(string resource) => resource!="ElectricCharge"&&resource!="Machinery"&&resource!="ReplacementParts"&&resource!="EnrichedUranium"&&resource!="DepletedFuel"&&resource!="Construction";
        bool PaidProductionStockAccess(ColonyEnvironment env,ColonyRecord colony,ColonyFacility facility,Vessel vessel,uint partId,string depotId,string membership,DepotRegistryModule registry)
        {
            // This is a conserved owned-stock delivery, not native USI scavenging.
            // The ordinary 150m rule below remains in force for adopted warehouses.
            if(Current!=this||!Ready||state.WorldId!=env.WorldId||ContextKey!=env.ContextKey||!ReferenceEquals(selectedGame,HighLogic.CurrentGame)||
                facility.ConstructionOrderId.Length==0||vessel==null||!vessel.LandedOrSplashed||vessel.id.ToString("D")!=facility.VesselId||!facility.PartIds.Contains(partId)||
                vessel.mainBody==null||!Finite(vessel.mainBody.Radius)||vessel.mainBody.Radius<=0||!Finite(vessel.latitude)||!Finite(vessel.longitude)||
                vessel.latitude < -90||vessel.latitude > 90||vessel.longitude < -180||vessel.longitude > 180)return false;
            var original=state;
            try
            {
                var candidates=state.Plans.Where(p=>p.ColonyId==colony.Id&&p.State!="cancelled")
                    .SelectMany(p=>p.Production.Where(c=>c.Inventory?.PoliciesApplied==true&&c.State!="held"&&c.State!="cancelled")
                        .SelectMany(c=>c.Inventory.Endpoints.Where(r=>r.State=="applied"&&r.FacilityId==facility.Id&&r.VesselId==facility.VesselId&&r.DepotId==depotId&&r.MemberPartIds.Contains(partId))
                            .Select(r=>new{Plan=p,Claim=c,Receipt=r}))).Take(2).ToArray();
                if(candidates.Length!=1)return false;var owned=candidates[0];
                var item=owned.Plan.Quote.ProductionInvestments.Single(i=>i.Id==owned.Claim.Id);
                if(item.Inventory==null||(!item.Inventory.AutomaticIntake&&!item.Inventory.AutomaticInputRefill)||registry.WorldId!=item.Inventory.RegistryWorldId)return false;
                var spec=item.Inventory.Endpoints.Single(s=>s.Id==owned.Receipt.SpecId);
                var paid=ProductionFacility(owned.Plan,spec.BuildingId);var order=state.Construction.Single(o=>o.Id==paid.ConstructionOrderId);
                if(!ReferenceEquals(paid,facility)||!order.FundsPaid||order.State!="operational")return false;
                RequireProductionLocation(owned.Plan,facility,vessel);
                string current;
                return ReadProductionEndpoint(registry,owned.Plan,spec,owned.Receipt,depotId,out current)&&current==membership&&current==owned.Receipt.MembershipHash&&
                    state==original&&Current==this&&ContextKey==env.ContextKey&&ReferenceEquals(selectedGame,HighLogic.CurrentGame);
            }
            catch(Exception){return false;}
        }
        private void PopulatePhysicalProcurementEnvironment(ColonyEnvironment env)
        {
            PopulatePhysicalProcurementEnvironment(env, new ColonyEnvironmentConfigObservation());
        }
        private void PopulatePhysicalProcurementEnvironment(ColonyEnvironment env, ColonyEnvironmentConfigObservation environmentConfigs)
        {
            if(state==null||FlightGlobals.Vessels==null)return;
            var registry=DepotRegistryModule.Instance;if(registry==null||!registry.IsReady||registry.IsCorrupt)return;
            double range=0;var configs=environmentConfigs.LogisticsSettings;
            if(configs.Length!=1||!double.TryParse(configs[0].GetValue("ScavangeRange"),NumberStyles.Float,CultureInfo.InvariantCulture,out range)||!Finite(range)||range<=0||range>10000)return;
            var depots=new Dictionary<string,LocalDepotObservation>();
            var recipes=new Dictionary<string,LocalRecipeObservation>();
            var vessels=FlightGlobals.Vessels.Where(v=>v!=null).Take(2049).ToArray();if(vessels.Length>2048)return;
            var workshops=ReadWorkshopWitnesses(vessels);
            foreach(var colony in state.Colonies)foreach(var facility in colony.Facilities.Where(f=>f.State!="retired"))
            {
                var matching=vessels.Where(v=>v.id.ToString("D")==facility.VesselId).ToArray();if(matching.Length!=1)continue;var vessel=matching[0];
                bool inRange=false;
                if(vessel.mainBody!=null&&vessel.mainBody.bodyName==colony.Site.Body&&vessel.LandedOrSplashed)
                {
                    var site=new ColonySite {Body=vessel.mainBody.bodyName,Latitude=vessel.latitude,Longitude=vessel.longitude,RadiusMeters=10};
                    double distance=ColonyEngine.SurfaceDistance(colony.Site,site,vessel.mainBody.Radius);inRange=distance<=Math.Min(range,colony.Site.RadiusMeters);
                }
                if(vessel.loaded&&vessel.parts!=null)
                {
                    foreach(var part in vessel.parts.Where(p=>p!=null&&facility.PartIds.Contains(p.persistentId)))
                    {
                        var warehouses=part.Modules.Cast<PartModule>().Where(m=>m!=null&&m.moduleName=="USI_ModuleResourceWarehouse").ToArray();if(warehouses.Length!=1)continue;
                        bool enabled=UtilityBool(warehouses[0],"localTransferEnabled");
                        bool maintenanceReserve=!part.Modules.Cast<PartModule>().OfType<BaseConverter>().Any();
                        foreach(var resource in part.Resources.Where(r=>r!=null&&(WarehouseCommodity(r.resourceName)||r.resourceName=="Machinery"&&maintenanceReserve)))
                            AddLocalStock(env,colony,facility,vessel,part.persistentId,part.partInfo==null?part.name:part.partInfo.title,resource.resourceName,enabled,resource.flowState,inRange,registry,depots,recipes,workshops);
                    }
                }
                else if(vessel.protoVessel!=null)
                {
                    foreach(var part in vessel.protoVessel.protoPartSnapshots.Where(p=>p!=null&&facility.PartIds.Contains(p.persistentId)))
                    {
                        var warehouses=part.modules.Where(m=>m.moduleName=="USI_ModuleResourceWarehouse").ToArray();if(warehouses.Length!=1)continue;
                        bool enabled;enabled=bool.TryParse(warehouses[0].moduleValues.GetValue("localTransferEnabled"),out enabled)&&enabled;
                        bool maintenanceReserve=part.partInfo!=null&&part.partInfo.partPrefab!=null&&!part.partInfo.partPrefab.Modules.Cast<PartModule>().OfType<BaseConverter>().Any()&&part.modules.All(m=>part.partInfo.partPrefab.Modules.Cast<PartModule>().Any(native=>native.moduleName==m.moduleName));
                        foreach(var resource in part.resources.Where(r=>r!=null&&(WarehouseCommodity(r.resourceName)||r.resourceName=="Machinery"&&maintenanceReserve)))
                            AddLocalStock(env,colony,facility,vessel,part.persistentId,part.partInfo==null?part.partName:part.partInfo.title,resource.resourceName,enabled,resource.flowState,inRange,registry,depots,recipes,workshops);
                    }
                }
            }
        }
        static HashSet<string> ReadLocalNativeInputs(Vessel vessel,string context,double ut,out bool complete)
        {
            complete=true;
            var inputs=new HashSet<string>(StringComparer.Ordinal);
            if(vessel.loaded&&vessel.parts!=null)
            {
                var active=vessel.parts.Where(p=>p!=null).SelectMany(p=>p.Modules.Cast<PartModule>()).OfType<BaseConverter>().Where(c=>c.IsActivated).Take(513).ToArray();
                if(active.Length>512){complete=false;return inputs;}
                foreach(var converter in active)
                {
                    ResourceRatio[] recipe;string reason;
                    if(!TryReadNativeRecipeInputs(converter,context,ut,out recipe,out reason)){complete=false;continue;}
                    foreach(var input in recipe.Where(i=>i.Ratio>0))inputs.Add(input.ResourceName);
                }
            }
            else
            {
                try
                {
                    var modules=UtilityRead(vessel,"vesselModules") as IEnumerable;if(modules==null){complete=false;return inputs;}
                    var processors=modules.Cast<object>().Where(m=>m!=null&&m.GetType().FullName=="BackgroundResourceProcessing.BackgroundResourceProcessor").ToArray();if(processors.Length!=1){complete=false;return inputs;}
                    var converters=UtilityRead(processors[0],"Converters") as IEnumerable;if(converters==null){complete=false;return inputs;}
                    var bounded=converters.Cast<object>().Take(513).ToArray();if(bounded.Length>512){complete=false;return inputs;}
                    foreach(var converter in bounded)
                    {
                        var rows=UtilityRead(converter,"Inputs") as IEnumerable;if(rows==null){complete=false;continue;}var boundedRows=rows.Cast<object>().Take(65).ToArray();if(boundedRows.Length>64){complete=false;continue;}
                        foreach(var entry in boundedRows){var ratio=UtilityRead(entry,"Value");var name=UtilityRead(ratio,"ResourceName") as string;if(name==null){complete=false;continue;}if(UtilityNumber(ratio,"Ratio",0)>0)inputs.Add(name);}
                    }
                }
                catch(Exception) {inputs.Clear();complete=false;}
            }
            return inputs;
        }
        bool PreservePaidProductionInputs(ColonyFacility facility,string resource,HashSet<string> observed,ref bool nativeInput)
        {
            if(facility.ConstructionOrderId.Length==0||facility.TemplateId!="cultivation-duna-v1")return true;
            // An absent/held native recipe is not evidence that a paid farm no
            // longer needs its approved inputs. Retain intent; never invent supply.
            try
            {
                var owners=state.Plans.Where(p=>p.State!="cancelled").SelectMany(p=>p.Production
                    .Where(c=>c.Inventory?.PoliciesApplied==true)
                    .SelectMany(c=>c.Inventory.Endpoints.Where(e=>e.State=="applied"&&e.FacilityId==facility.Id&&e.VesselId==facility.VesselId)
                        .Select(e=>new {Plan=p,Claim=c,Endpoint=e}))).Take(2).ToArray();
                if(owners.Length!=1)return false;
                var owner=owners[0];var item=owner.Plan.Quote.ProductionInvestments.Single(i=>i.Id==owner.Claim.Id);
                var spec=item.Inventory.Endpoints.Single(e=>e.Id==owner.Endpoint.SpecId);
                var recipe=item.Recipe;
                if(spec.Role!="cultivation"||recipe.TemplateId!=facility.TemplateId||recipe.TemplateHash!=facility.TemplateHash||
                    !ReferenceEquals(ProductionFacility(owner.Plan,spec.BuildingId),facility))return false;
                var inputs=recipe.Inputs.Where(i=>i.UnitsPerSecond>0).Select(i=>i.Resource).ToArray();
                if(inputs.Length==0||inputs.Length>16||inputs.Distinct(StringComparer.Ordinal).Count()!=inputs.Length)return false;
                nativeInput=nativeInput||inputs.Contains(resource,StringComparer.Ordinal);
                return observed!=null&&inputs.All(observed.Contains);
            }
            catch(Exception){return false;}
        }
        void AddLocalStock(ColonyEnvironment env,ColonyRecord colony,ColonyFacility facility,Vessel vessel,uint partId,string title,string resource,bool warehouse,bool flow,bool range,DepotRegistryModule registry,Dictionary<string,LocalDepotObservation> cache,Dictionary<string,LocalRecipeObservation> recipes,List<WorkshopWitness> workshops)
        {
            if(env.Planning.LocalStocks.Count>=1024)return;
            var registrations=registry.Registrations.Where(r=>r.MemberIds.Contains(partId)&&(r.OwnerKind=="legacy"||r.OwnerKind=="colony"&&r.OwnerColonyId==colony.Id&&r.OwnerFacilityId==facility.Id)).ToArray();if(registrations.Length!=1)return;var registration=registrations[0];
            LocalDepotObservation depot;
            if(!cache.TryGetValue(registration.DepotId,out depot))
            {depot=new LocalDepotObservation();PhysicalInventoryTransfers.TryObserve(registration.DepotId,out depot.Observation,out depot.Capability,out depot.Hash,out depot.Reason);cache.Add(registration.DepotId,depot);}
            if(depot.Observation==null||depot.Capability==null)return;
            // Remote Resolve catches the authoritative producer up first; recipe
            // access must be observed after that same provider boundary.
            LocalRecipeObservation recipe;
            string vesselId=vessel.id.ToString("D");
            if(!recipes.TryGetValue(vesselId,out recipe)){recipe=new LocalRecipeObservation();recipe.Inputs=ReadLocalNativeInputs(vessel,env.ContextKey,env.Ut,out recipe.Complete);recipes.Add(vesselId,recipe);}
            bool recipeComplete=recipe.Complete,nativeInput=recipe.Inputs.Contains(resource);
            recipeComplete=PreservePaidProductionInputs(facility,resource,recipe.Inputs,ref nativeInput)&&recipeComplete;
            var rows=depot.Observation.Rows.Where(r=>r.MemberPersistentId==partId&&r.ResourceName==resource).ToArray();if(rows.Length!=1)return;var row=rows[0];
            if(!Finite(row.Amount)||!Finite(row.Capacity)||row.Amount<0||row.Amount>row.Capacity||row.Capacity>ColonyLimits.MaxQuantity/(double)ColonyLimits.Units)return;
            bool paidAccess=PaidProductionStockAccess(env,colony,facility,vessel,partId,registration.DepotId,depot.Hash,registry);
            range=range||paidAccess;
            flow=flow&&row.DebitAllowed==true;
            var local=new ColonyLocalStock {Id=ColonyEngine.PlanningChildId(facility.Id,partId+":"+resource),ColonyId=colony.Id,FacilityId=facility.Id,VesselId=vessel.id.ToString("D"),PartId=partId,PartName=Bound(title,160),DepotId=registration.DepotId,Resource=resource,Amount=(long)Math.Floor(row.Amount*ColonyLimits.Units),Capacity=(long)Math.Floor(row.Capacity*ColonyLimits.Units),Current=true,WarehouseEnabled=warehouse,FlowAllowed=flow,WithinRange=range,NativeInput=nativeInput,Provider=depot.Observation.ProviderId,ProviderVersion=depot.Observation.ProviderVersion,MembershipHash=depot.Hash,ContextKey=env.ContextKey,ObservedUt=env.Ut};
            local.PhysicalReserve=nativeInput?(long)decimal.Ceiling(local.Capacity*.5m):0;
            local.StockKind=resource=="Machinery"?"maintenanceReserve":"warehouse";
            if(local.StockKind=="maintenanceReserve")
            {
                var worker=workshops.FirstOrDefault(w=>w.PartId!=partId&&w.Vessel.mainBody==vessel.mainBody&&VesselDistance(w.Vessel,vessel)<=w.Range&&env.People.Roster.Any(p=>p.RosterId==w.Engineer&&p.Type=="Crew"&&p.Status=="Assigned"&&p.Current&&p.ContextKey==env.ContextKey&&p.PartId==w.PartId&&p.VesselId==w.Vessel.id.ToString("D")));
                local.QualifiedWorker=worker!=null;local.WorkerWitness=worker==null?"No actual Engineer in a separate supported repair workshop within installed repair range.":"Engineer "+worker.Engineer+"; AutoRepairer part "+worker.PartId+"; range "+worker.Range.ToString("R",CultureInfo.InvariantCulture)+"m; actual reserve tank has no native BaseConverter.";
            }
            local.CanApply=depot.Capability.CanApply&&warehouse&&flow&&range&&recipeComplete&&(local.StockKind!="maintenanceReserve"||local.QualifiedWorker);
            local.Reason=local.CanApply?(paidAccess?"Exact paid production endpoint; modeled owned-stock transfer within its charter and approved plot; native input buffer respected.":"Actual unlocked local warehouse; registered same-owner provider; installed scavange radius and native 50% input buffer respected."):Bound(depot.Reason??(!warehouse?"Native local warehouse transfer is disabled.":!flow?"Actual tank flow is locked.":!range?"Tank is outside ordinary warehouse reach and lacks current approved paid-production access.":"Physical provider cannot apply in the current context."),512);
            if(!recipeComplete)local.Reason="Actual final native converter recipe is not yet observed; stock transfer held to protect unknown input buffers.";
            else if(local.StockKind=="maintenanceReserve"&&!local.QualifiedWorker)local.Reason=local.WorkerWitness;
            local.AccessWitness=ColonyStateCodec.Hash(Encoding.UTF8.GetBytes(env.ContextKey+"|"+facility.Id+"|"+vessel.id.ToString("D")+"|"+partId+"|"+resource+"|"+warehouse+"|"+flow+"|"+range+"|"+nativeInput+"|"+recipeComplete+"|"+local.StockKind+"|"+local.WorkerWitness+"|"+depot.Hash+"|"+local.Provider+"|"+local.ProviderVersion));
            env.Planning.LocalStocks.Add(local);
        }
        int physicalTransferCursor;
        private ColonyEnvironment GetPhysicalProcurementEnvironment()
        {
            long started=performance.Start();
            try
            {
                // Exact local access consumes fresh identities/crew locations,
                // registry/recipe/range/stock observations, not utility forecasts
                // or housing/planning/production qualifications. Reuse the same
                // observers and provider catch-up, with no retained native data.
                var env=new ColonyEnvironment {WorldId=state==null?"":state.WorldId,ContextKey=ContextKey,Ut=Planetarium.GetUniversalTime()};
                var configs=new ColonyEnvironmentConfigObservation();
                PopulatePeopleEnvironment(env,configs);
                PopulatePhysicalProcurementEnvironment(env,configs);
                return env;
            }
            finally {performance.End(ColonyPerformanceChannel.Environment,started);}
        }
        private void ApplyOnePhysicalProcurementEffect(ColonyEnvironment tickEnvironment)
        {
            if(mutating||!Ready||state.Effects.Any(e=>e.State=="held"||e.State=="applying")||WorldBridgeAddon.Current!=null&&WorldBridgeAddon.Current.LegacyAuthorityWriteHeld)return;
            var operations=state.PhysicalTransfers.Where(o=>o.State=="reserved").ToArray();if(operations.Length==0)return;if(physicalTransferCursor>=operations.Length)physicalTransferCursor=0;var op=operations[physicalTransferCursor++];
            // A current same-tick negative can defer work; it never authorizes a transfer.
            // Positive or stale observations retain the original fresh native preflight.
            if(Current==this&&ReferenceEquals(selectedGame,HighLogic.CurrentGame)&&
                ObservedPhysicalProcurementBlocked(tickEnvironment,op,state,ContextKey,Planetarium.GetUniversalTime()))
            {SetPhysicalProcurementBlocker(op.Id,"Exact warehouse access, adopted tank or endpoint membership changed; reservation retained for review/cancellation.");return;}
            var env=GetPhysicalProcurementEnvironment();var target=env.Planning.LocalStocks.SingleOrDefault(s=>s.Id==op.LocalStockId&&s.ColonyId==op.ColonyId&&s.PartId==op.PartId&&s.Resource==op.Resource&&s.CanApply&&s.Current);
            if(!ExactPhysicalProcurementAccess(target,op))
            {SetPhysicalProcurementBlocker(op.Id,"Exact warehouse access, adopted tank or endpoint membership changed; reservation retained for review/cancellation.");return;}
            if(op.Direction=="toColony"&&op.Amount>target.Amount-target.PhysicalReserve || op.Direction=="toPhysical"&&op.Amount>target.Capacity-target.Amount)
            {SetPhysicalProcurementBlocker(op.Id,"Current physical stock or capacity changed before the attempt; no debit/credit was repeated.");return;}
            PhysicalInventoryTransferPlan plan;string reason;
            if(!PhysicalInventoryTransfers.TryPrepare(op.Id,op.DepotId,new[]{new ResourceAmount {ResourceName=op.Resource,AmountMicroUnits=op.Amount}},op.Direction=="toColony",out plan,out reason,op.PartId))
            {SetPhysicalProcurementBlocker(op.Id,reason??"Exact physical preflight unavailable.");return;}
            var rows=plan.Witness.Rows;if(rows.Length!=1||rows[0].MemberPersistentId!=op.PartId||rows[0].ResourceName!=op.Resource)return;
            var prior=state;var game=HighLogic.CurrentGame;string epoch=loadEpoch;var stock=prior.Colonies.Single(c=>c.Id==op.ColonyId).Stock.Single(s=>s.Resource==op.Resource);
            string before="colony="+op.ColonyId+"; ownedMicro="+stock.Amount+"; "+ServiceWitness(plan,false)+"; access="+target.AccessWitness;
            string after="colony="+op.ColonyId+"; ownedMicro="+(op.Direction=="toColony"?stock.Amount+op.Amount:stock.Amount-op.Amount)+"; "+ServiceWitness(plan,true)+"; exact apply/synchronize/readback";
            var held=ColonyEngine.HoldPreparedPhysicalTransfer(prior,op.Id,plan.Witness.ProviderId,before,after,rows[0].BeforeAmount,rows[0].IntendedAfterAmount);
            var complete=ColonyEngine.CompletePreparedPhysicalTransfer(held,op.Id,env.Ut,after);
            var rejected=ColonyEngine.RejectPreparedPhysicalTransfer(prior,op.Id,env.Ut,before);
            byte[] heldBytes=ColonyStateCodec.Serialize(held),completeBytes=ColonyStateCodec.Serialize(complete),rejectedBytes=ColonyStateCodec.Serialize(rejected);
            string heldHash=ColonyStateCodec.Hash(heldBytes),completeHash=ColonyStateCodec.Hash(completeBytes),rejectedHash=ColonyStateCodec.Hash(rejectedBytes);
            var boundary=new PhysicalInventoryCommitBoundary {
                IsCurrent=()=>Current==this&&ReferenceEquals(game,HighLogic.CurrentGame)&&epoch==loadEpoch&&(state==prior||state==held)&&(WorldBridgeAddon.Current==null||!WorldBridgeAddon.Current.LegacyAuthorityWriteHeld),
                CommitDurableHold=()=>{state=held;acceptedBytes=heldBytes;acceptedHash=heldHash;},CommitSuccess=()=>{state=complete;acceptedBytes=completeBytes;acceptedHash=completeHash;},RestoreBeforeAfterConfirmedRollback=()=>{state=rejected;acceptedBytes=rejectedBytes;acceptedHash=rejectedHash;}};
            mutating=true;
            try {var result=PhysicalInventoryTransfers.Commit(plan,boundary);if(result.Outcome!="accepted"&&result.Outcome!="rejected")Debug.LogWarning("[ExpanseColony] Physical procurement "+result.Outcome+": "+Bound(result.Reason,360));}
            catch(Exception ex){if(state==prior){state=held;acceptedBytes=heldBytes;acceptedHash=heldHash;}Debug.LogError("[ExpanseColony] Physical procurement held: "+Bound(ex.Message,360));}
            finally {mutating=false;}
        }
        static bool ExactPhysicalProcurementAccess(ColonyLocalStock target,ColonyPhysicalTransfer op) =>
            target!=null&&target.Current&&target.CanApply&&target.DepotId==op.DepotId&&
            target.MembershipHash==op.MembershipHash&&target.AccessWitness==op.AccessWitness;
        static bool ObservedPhysicalProcurementBlocked(ColonyEnvironment observed,ColonyPhysicalTransfer op,ColonyState current,string contextKey,double currentUt)
        {
            if(observed==null||current==null||string.IsNullOrWhiteSpace(contextKey)||observed.Planning==null||observed.Planning.LocalStocks==null||
                observed.WorldId!=current.WorldId||observed.ContextKey!=contextKey||
                observed.Ut!=current.SimulatedUt||observed.Ut!=currentUt||observed.Planning.LocalStocks.Any(s=>s==null))return false;
            var matches=observed.Planning.LocalStocks.Where(s=>s.Id==op.LocalStockId&&s.ColonyId==op.ColonyId&&s.PartId==op.PartId&&s.Resource==op.Resource).Take(2).ToArray();
            if(matches.Length>1)return false;
            var target=matches.SingleOrDefault();
            if(target!=null&&(target.ContextKey!=observed.ContextKey||target.ObservedUt!=observed.Ut))return false;
            return !ExactPhysicalProcurementAccess(target,op);
        }
        void SetPhysicalProcurementBlocker(string id,string reason)
        {reason=Bound(reason,512);if(state.PhysicalTransfers.Single(o=>o.Id==id).Reason==reason)return;var next=GetStateCopy();next.PhysicalTransfers.Single(o=>o.Id==id).Reason=reason;Accept(next);}
    }
}
