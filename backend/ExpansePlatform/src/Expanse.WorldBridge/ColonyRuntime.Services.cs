using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using Expanse.Domain;
using Expanse.Domain.Colonies;
using UnityEngine;

namespace Expanse.WorldBridge
{
    public sealed partial class ColonyRuntime
    {
        private void PopulateServiceEnvironment(ColonyEnvironment env)
        {
            if(state==null || FlightGlobals.Vessels==null)return;
            var registry=DepotRegistryModule.Instance;
            var vessels=FlightGlobals.Vessels.Where(v=>v!=null).Take(2049).ToArray();
            if(vessels.Length>2048)return;
            var workshops=ReadWorkshopWitnesses(vessels);
            foreach(var colony in state.Colonies) foreach(var facility in colony.Facilities)
            {
                var vessel=vessels.SingleOrDefault(v=>v.id.ToString("D")==facility.VesselId);if(vessel==null)continue;
                if(vessel.loaded && vessel.parts!=null)
                {
                    foreach(var part in vessel.parts.Where(p=>p!=null && facility.PartIds.Contains(p.persistentId) && p.Modules.Contains("USI_ModuleFieldRepair")))
                    foreach(var resource in part.Resources.Where(r=>r!=null && IsServiceResource(r.resourceName) && (r.resourceName!="Machinery" || part.Modules.Cast<PartModule>().Any(m=>m is BaseConverter))))
                        AddServiceTarget(env,colony,facility,vessel,part.persistentId,part.partInfo==null ? part.name : part.partInfo.title,resource.resourceName,resource.amount,resource.maxAmount,registry,workshops);
                }
                else if(vessel.protoVessel!=null)
                {
                    foreach(var part in vessel.protoVessel.protoPartSnapshots.Where(p=>p!=null && facility.PartIds.Contains(p.persistentId) && p.modules.Any(m=>m.moduleName=="USI_ModuleFieldRepair")))
                    foreach(var resource in part.resources.Where(r=>r!=null && IsServiceResource(r.resourceName) && (r.resourceName!="Machinery" || part.partInfo!=null && part.partInfo.partPrefab!=null && part.modules.Any(m=>part.partInfo.partPrefab.Modules.Cast<PartModule>().Any(native=>native.moduleName==m.moduleName && native is BaseConverter)))))
                        AddServiceTarget(env,colony,facility,vessel,part.persistentId,part.partInfo==null ? part.partName : part.partInfo.title,resource.resourceName,resource.amount,resource.maxAmount,registry,workshops);
                }
            }
        }
        private sealed class WorkshopWitness
        {
            public Vessel Vessel;public uint PartId;public double Range;public string Engineer;
        }
        private static List<WorkshopWitness> ReadWorkshopWitnesses(Vessel[] vessels)
        {
            var result=new List<WorkshopWitness>();
            foreach(var vessel in vessels)
            {
                if(vessel.mainBody==null || !vessel.LandedOrSplashed)continue;
                if(vessel.loaded && vessel.parts!=null)
                {
                    foreach(var part in vessel.parts.Where(p=>p!=null))
                    {
                        var repairer=part.Modules.Cast<PartModule>().SingleOrDefault(m=>m!=null && m.moduleName=="ModuleAutoRepairer");if(repairer==null)continue;
                        var engineer=part.protoModuleCrew.FirstOrDefault(c=>c!=null && c.rosterStatus==ProtoCrewMember.RosterStatus.Assigned && c.experienceTrait!=null && c.experienceTrait.TypeName=="Engineer");if(engineer==null)continue;
                        var field=repairer.GetType().GetField("RepairRange",BindingFlags.Public|BindingFlags.Instance);if(field==null)continue;
                        double range=Convert.ToDouble(field.GetValue(repairer),CultureInfo.InvariantCulture);
                        if(Finite(range) && range>0 && range<=10000)result.Add(new WorkshopWitness {Vessel=vessel,PartId=part.persistentId,Range=range,Engineer=engineer.name});
                    }
                }
                else if(vessel.protoVessel!=null)
                {
                    foreach(var part in vessel.protoVessel.protoPartSnapshots.Where(p=>p!=null && p.modules.Any(m=>m.moduleName=="ModuleAutoRepairer")))
                    {
                        var engineer=part.protoModuleCrew.FirstOrDefault(c=>c!=null && c.rosterStatus==ProtoCrewMember.RosterStatus.Assigned && c.experienceTrait!=null && c.experienceTrait.TypeName=="Engineer");if(engineer==null || part.partInfo==null || part.partInfo.partPrefab==null)continue;
                        var repairer=part.partInfo.partPrefab.Modules.Cast<PartModule>().SingleOrDefault(m=>m!=null && m.moduleName=="ModuleAutoRepairer");if(repairer==null)continue;
                        var field=repairer.GetType().GetField("RepairRange",BindingFlags.Public|BindingFlags.Instance);if(field==null)continue;
                        double range=Convert.ToDouble(field.GetValue(repairer),CultureInfo.InvariantCulture);
                        if(Finite(range) && range>0 && range<=10000)result.Add(new WorkshopWitness {Vessel=vessel,PartId=part.persistentId,Range=range,Engineer=engineer.name});
                    }
                }
                if(result.Count>=256)break;
            }
            return result;
        }
        private static bool IsServiceResource(string resource) => resource=="Machinery" || resource=="ReplacementParts" || resource=="EnrichedUranium";
        private void AddServiceTarget(ColonyEnvironment env,ColonyRecord colony,ColonyFacility facility,Vessel vessel,uint partId,string title,string resource,double amount,double capacity,DepotRegistryModule registry,List<WorkshopWitness> workshops)
        {
            if(env.Services.Targets.Count>=1024 || !Finite(amount) || !Finite(capacity) || amount<0 || capacity<amount || capacity>ColonyLimits.MaxQuantity/(double)ColonyLimits.Units)return;
            var target=new ColonyServiceTarget {ColonyId=colony.Id,FacilityId=facility.Id,PartId=partId,PartName=Bound(title,160),SourceResource=resource=="ReplacementParts" ? "MaterialKits" : resource,DestinationResource=resource,
                Amount=checked((long)Math.Floor(amount*ColonyLimits.Units)),Capacity=checked((long)Math.Floor(capacity*ColonyLimits.Units)),ContextKey=ContextKey,ObservedUt=env.Ut,Current=true};
            var worker=workshops.FirstOrDefault(w=>w.Vessel.mainBody==vessel.mainBody && VesselDistance(w.Vessel,vessel)<=w.Range &&
                env.People.Roster.Any(p=>p.RosterId==w.Engineer && p.Type=="Crew" && p.Status=="Assigned" && p.Current && p.ContextKey==ContextKey && p.PartId==w.PartId && p.VesselId==w.Vessel.id.ToString("D")));
            target.QualifiedWorker=worker!=null;target.WorkerWitness=worker==null ? "No actual Engineer occupies a supported workshop part within its installed repair range." : "Engineer "+worker.Engineer+" in actual ModuleAutoRepairer part "+worker.PartId+"; vessel "+worker.Vessel.id.ToString("D")+"; range "+worker.Range.ToString("R",CultureInfo.InvariantCulture)+"m.";
            target.Reason=target.WorkerWitness;
            if(registry!=null && registry.IsReady && !registry.IsCorrupt)
            {
                var registrations=registry.Registrations.Where(r=>r.MemberIds.Contains(partId)&&(r.OwnerKind=="legacy"||r.OwnerKind=="colony"&&r.OwnerColonyId==colony.Id&&r.OwnerFacilityId==facility.Id)).ToArray();
                if(registrations.Length==1)
                {
                    var registration=registrations[0];var snapshot=registry.CreateSelectedEffectRegistrySnapshot(registration.DepotId);var record=snapshot?.Depots.SingleOrDefault(x=>x.DepotId==registration.DepotId && x.Active);
                    if(record!=null)
                    {
                        var members=registration.MemberIds.OrderBy(x=>x).ToArray();
                        var endpoint=new InventoryEndpoint {DepotId=registration.DepotId,MembershipRevision=registration.MembershipRevision,AnchorPersistentId=registration.Anchor,MemberPersistentIds=members,MembershipHash=record.MembershipHash,MemberSetHash=OperationIdentity.ComputeMemberSetHash(registration.Anchor,members),Scene=HighLogic.LoadedScene.ToString()};
                        IInventoryGateway gateway=vessel.loaded ? (IInventoryGateway)new LoadedBrpInventoryGateway() : new RemoteBrpInventoryGateway();
                        long mutation=registry.MutationRevision;string world=registry.WorldId;var game=HighLogic.CurrentGame;
                        var capability=gateway.Describe(endpoint);target.DepotId=registration.DepotId;target.Provider=capability.ProviderId;
                        bool currentAuthority=ReferenceEquals(registry,DepotRegistryModule.Instance)&&registry.IsReady&&!registry.IsCorrupt&&
                            ReferenceEquals(game,HighLogic.CurrentGame)&&world==registry.WorldId&&mutation==registry.MutationRevision&&registry.CreateSelectedEffectRegistrySnapshot(registration.DepotId)?.RegistryHash==snapshot.RegistryHash;
                        target.CanApply=capability.CanApply && target.QualifiedWorker&&currentAuthority;
                        if(!currentAuthority)target.Reason="Selected-save registry or endpoint authority changed during service observation; refresh before applying.";
                        else if(!capability.CanApply)target.Reason=capability.HoldReason ?? "Exact physical inventory provider is unavailable in this context.";
                        else if(target.QualifiedWorker)target.Reason="Real stock-to-installed-tank service available; qualified physical worker and exact same-owner readback required.";
                    }
                }
                else target.Reason="Installed tank requires one unambiguous registered inventory endpoint; no guessed depot membership.";
            }
            else target.Reason="Selected-save physical inventory registry is not ready.";
            target.QuoteHash=ColonyStateCodec.Hash(Encoding.UTF8.GetBytes(target.ContextKey+"|"+target.FacilityId+"|"+target.PartId+"|"+target.DepotId+"|"+resource+"|"+target.Amount+"|"+target.Capacity+"|"+target.WorkerWitness+"|"+target.Provider));
            env.Services.Targets.Add(target);
        }
        private static double VesselDistance(Vessel first,Vessel second)
        {
            if(first==second)return 0;
            if(first==null || second==null || first.mainBody==null || first.mainBody!=second.mainBody)return double.PositiveInfinity;
            return (first.GetWorldPos3D()-second.GetWorldPos3D()).magnitude;
        }
        private int serviceEffectCursor;
        private void ApplyOneServiceEffect()
        {
            if(mutating || !Ready || state.Effects.Any(x=>x.State=="held" || x.State=="applying"))return;
            var operations=state.ServiceOperations.Where(x=>x.State=="reserved").ToArray();if(operations.Length==0)return;
            if(serviceEffectCursor>=operations.Length)serviceEffectCursor=0;var op=operations[serviceEffectCursor++];
            var env=GetEnvironment();var target=env.Services.Targets.SingleOrDefault(x=>x.ColonyId==op.ColonyId && x.FacilityId==op.FacilityId && x.PartId==op.PartId && x.DestinationResource==op.DestinationResource && x.Current && x.CanApply && x.QualifiedWorker);
            if(target==null || target.DepotId!=op.DepotId){SetServiceBlocker(op.Id,"Current physical worker, exact installed tank or selected inventory endpoint is unavailable; reserved stock retained.");return;}
            PhysicalInventoryTransferPlan plan;string reason;
            if(!PhysicalInventoryTransfers.TryPrepare(op.Id,op.DepotId,new[]{new ResourceAmount {ResourceName=op.DestinationResource,AmountMicroUnits=op.Amount}},false,out plan,out reason,op.PartId))
            {SetServiceBlocker(op.Id,reason ?? "Exact installed tank preflight unavailable; reserved stock retained.");return;}
            var prior=state;var game=HighLogic.CurrentGame;string epoch=loadEpoch;
            long sourceBefore=prior.Colonies.Single(c=>c.Id==op.ColonyId).Stock.Single(s=>s.Resource==op.SourceResource).Amount;
            string source="colony="+op.ColonyId+"; source="+op.SourceResource+"; ";
            string before=source+"amountMicro="+sourceBefore+"; "+ServiceWitness(plan,false)+"; "+target.WorkerWitness;
            string after=source+"amountMicro="+(sourceBefore-op.Amount)+"; "+ServiceWitness(plan,true)+"; exact provider apply/synchronize/readback verified";
            var held=ColonyEngine.HoldPreparedService(prior,op.Id,plan.Witness.ProviderId,before);
            var complete=ColonyEngine.CompletePreparedService(held,op.Id,env.Ut,after);
            var rejected=ColonyEngine.RejectPreparedService(prior,op.Id,env.Ut,"Physical attempt rejected; exact rollback confirmed. Input reservation released.",before);
            byte[] heldBytes=ColonyStateCodec.Serialize(held),completeBytes=ColonyStateCodec.Serialize(complete),rejectedBytes=ColonyStateCodec.Serialize(rejected);
            string heldHash=ColonyStateCodec.Hash(heldBytes),completeHash=ColonyStateCodec.Hash(completeBytes),rejectedHash=ColonyStateCodec.Hash(rejectedBytes);
            var boundary=new PhysicalInventoryCommitBoundary {
                IsCurrent=()=>Current==this && ReferenceEquals(game,HighLogic.CurrentGame) && epoch==loadEpoch && (state==prior || state==held),
                CommitDurableHold=()=>{state=held;acceptedBytes=heldBytes;acceptedHash=heldHash;},
                CommitSuccess=()=>{state=complete;acceptedBytes=completeBytes;acceptedHash=completeHash;},
                RestoreBeforeAfterConfirmedRollback=()=>{state=rejected;acceptedBytes=rejectedBytes;acceptedHash=rejectedHash;}
            };
            mutating=true;
            try
            {
                var result=PhysicalInventoryTransfers.Commit(plan,boundary);
                if(result.Outcome!="accepted" && result.Outcome!="rejected")Debug.LogWarning("[ExpanseColony] Physical service "+result.Outcome+": "+Bound(result.Reason,360));
            }
            catch(Exception ex)
            {
                // Prepared hold is already authoritative if a callback began. Do
                // not restore stock or blindly repeat an uncertain physical credit.
                if(state==prior){state=held;acceptedBytes=heldBytes;acceptedHash=heldHash;}
                Debug.LogError("[ExpanseColony] Service held: "+Bound(ex.Message,360));
            }
            finally {mutating=false;}
        }
        private void SetServiceBlocker(string id,string reason)
        {
            reason=Bound(reason,512);var op=state.ServiceOperations.Single(x=>x.Id==id);if(op.Reason==reason)return;
            var next=GetStateCopy();next.ServiceOperations.Single(x=>x.Id==id).Reason=reason;Accept(next);
        }
        private static string ServiceWitness(PhysicalInventoryTransferPlan plan,bool after) => plan.Witness.ProviderId+" "+plan.Witness.ProviderVersion+"; "+string.Join("; ",plan.Witness.Rows.Select(r=>"part="+r.MemberPersistentId+"; "+r.ResourceName+"="+(after ? r.IntendedAfterAmount : r.BeforeAmount).ToString("R",CultureInfo.InvariantCulture)));
    }
}
