using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using USITools;
using Expanse.Domain.Colonies;
namespace Expanse.WorldBridge
{
    public sealed partial class ColonyRuntime
    {
        void PopulateProductionRegistry(ColonyEnvironment env)
        {
            var registry=DepotRegistryModule.Instance;var r=env.Production.Registry;
            if(registry==null||!registry.IsReady||registry.IsCorrupt){r.Reason="The selected save's physical inventory registry is unavailable or quarantined; no endpoint mutation is authorized.";return;}
            r.Ready=true;r.WorldId=registry.WorldId;r.Revision=registry.MutationRevision;r.Witness=registry.RegistrationAuthorityWitness();r.ColonyEndpoints=registry.ColonyDepotCount;r.TotalMembers=registry.Registrations.Sum(d=>d.MemberIds.Count);
            r.Reason="Paid-colony endpoints: "+r.ColonyEndpoints+" / 128; shared registered members: "+r.TotalMembers+" / 4096. Eight legacy endpoint slots remain separate.";
        }
        bool RunProductionInventoryRegistration()
        {
            if(!Ready||mutating||Current!=this||HighLogic.CurrentGame==null||!ReferenceEquals(selectedGame,HighLogic.CurrentGame))return false;
            var registry=DepotRegistryModule.Instance;if(registry==null||!registry.IsReady||registry.IsCorrupt)return false;
            foreach(var plan in state.Plans)foreach(var claim in plan.Production.Where(c=>c.Inventory!=null))
            {
                var item=plan.Quote.ProductionInvestments.Single(i=>i.Id==claim.Id);
                foreach(var receipt in claim.Inventory.Endpoints)
                {
                    var spec=item.Inventory.Endpoints.Single(s=>s.Id==receipt.SpecId);
                    if(receipt.State=="prepared"&&(plan.State=="cancelled"||plan.State=="complete"||!claim.Inventory.ReservesReleased))continue;
                    if(receipt.State=="prepared"&&state.Effects.Any(e=>e.State=="held"||e.State=="applying"))return false;
                    if(receipt.State=="applied")continue; // Deletion is an outage; never recreate an applied endpoint.
                    var original=state;var game=HighLogic.CurrentGame;string context=ContextKey;
                    try
                    {
                        if(registry.WorldId!=item.Inventory.RegistryWorldId)throw new InvalidOperationException("Reviewed registry belongs to another save; endpoint authority cannot migrate to it.");
                        var mapping=ResolveProductionInventoryMapping(plan,item,spec,receipt.State=="prepared");var facility=ProductionFacility(plan,spec.BuildingId);
                        string depotId=ColonyEngine.PlanningChildId(plan.Id,spec.Id+":depot");
                        if(receipt.State!="prepared")
                        {
                            if(receipt.FacilityId!=facility.Id||receipt.VesselId!=mapping.Vessel.id.ToString("D")||receipt.AnchorPartId!=mapping.Anchor||!receipt.MemberPartIds.SequenceEqual(mapping.Members))throw new InvalidOperationException("Retained attempted endpoint mapping changed; do not repeat registration.");
                            string hash;if(!ReadProductionEndpoint(registry,plan,spec,receipt,depotId,out hash))throw new InvalidOperationException("Retained attempted registration has no exact current endpoint readback. No blind registration replay is permitted.");
                            if(Current!=this||state!=original||ContextKey!=context||!ReferenceEquals(game,HighLogic.CurrentGame)||DepotRegistryModule.Instance!=registry)throw new InvalidOperationException("Selected scenario changed during registration reconciliation.");
                            var reconciled=ColonyEngine.CompleteProductionRegistration(original,receipt.EffectId,hash,ProductionRegistrationAfter(spec,receipt,hash));
                            reconciled=AttachProductionInitialProvenance(reconciled,plan,spec,receipt,mapping.Vessel);
                            if(Current!=this||state!=original||ContextKey!=context||!ReferenceEquals(game,HighLogic.CurrentGame)||registry!=DepotRegistryModule.Instance)throw new InvalidOperationException("Selected scenario changed during initial-content provenance readback.");
                            Accept(reconciled);return true;
                        }
                        long revision=registry.MutationRevision;string before=ColonyStateCodec.Hash(Encoding.UTF8.GetBytes(context+"|"+registry.WorldId+"|"+revision.ToString(CultureInfo.InvariantCulture)+"|"+registry.RegistrationAuthorityWitness()+"|"+spec.Hash+"|"+facility.Id+"|"+string.Join(",",mapping.Members)));
                        var applying=ColonyEngine.MarkProductionRegistrationApplying(original,plan.Id,claim.Id,spec.Id,facility.Id,mapping.Vessel.id.ToString("D"),mapping.Anchor,mapping.Members,revision,before);
                        Accept(applying);mutating=true;
                        try
                        {
                            Func<bool> authority=()=>Current==this&&state==applying&&ContextKey==context&&ReferenceEquals(game,HighLogic.CurrentGame)&&DepotRegistryModule.Instance==registry&&registry.WorldId==item.Inventory.RegistryWorldId&&
                                ProductionInventoryMappingSame(plan,item,spec,mapping);
                            string reason;
                            if(!registry.TryRegisterColonyEndpoint(mapping.Vessel,registry.WorldId,revision,depotId,plan.ColonyId,facility.Id,spec.Hash,facility.Name,mapping.Anchor,mapping.Members,authority,out reason))throw new InvalidOperationException(reason);
                            var attempted=state.Plans.Single(p=>p.Id==plan.Id).Production.Single(c=>c.Id==claim.Id).Inventory.Endpoints.Single(e=>e.SpecId==spec.Id);string hash;
                            if(!authority()||!ReadProductionEndpoint(registry,plan,spec,attempted,depotId,out hash))throw new InvalidOperationException("Registration returned without exact paid-owner/member readback.");
                            var complete=ColonyEngine.CompleteProductionRegistration(applying,attempted.EffectId,hash,ProductionRegistrationAfter(spec,attempted,hash));
                            complete=AttachProductionInitialProvenance(complete,plan,spec,attempted,mapping.Vessel);if(!authority())throw new InvalidOperationException("Selected scenario changed during initial-content provenance readback.");Accept(complete);
                        }
                        catch(Exception ex)
                        {if(Current==this&&state==applying&&ContextKey==context&&ReferenceEquals(game,HighLogic.CurrentGame))Accept(ColonyEngine.HoldEffect(applying,ColonyEngine.PlanningChildId(plan.Id,spec.Id+":register"),Bound(ex.Message,512)));}
                        finally{mutating=false;}
                        return true;
                    }
                    catch(Exception ex)
                    {
                        if(Current!=this||state!=original||ContextKey!=context||!ReferenceEquals(game,HighLogic.CurrentGame))return true;
                        string reason="Physical inventory registration: "+Bound(ex.Message,465);
                        if(claim.Reason!=reason){var next=GetStateCopy();next.Plans.Single(p=>p.Id==plan.Id).Production.Single(c=>c.Id==claim.Id).Reason=reason;next.Revision++;Accept(next);}return true;
                    }
                }
            }
            return false;
        }
        sealed class ProductionInventoryMapping {public Vessel Vessel;public uint Anchor;public uint[] Members;}
        ProductionInventoryMapping ResolveProductionInventoryMapping(ColonyPlan plan,ColonyProductionInvestment item,ColonyProductionEndpointSpec spec,bool firstAttempt)
        {
            bool farm=spec.Role=="cultivation";var feed=item.Recipe.Feeds.First();
            var native=ResolveProductionModule(plan,spec.BuildingId,farm?item.Recipe.CraftPartId:feed.CraftPartId,farm?item.Recipe.PartName:feed.PartName,farm?item.Recipe.ModuleName:"WOLF_HopperModule",firstAttempt);
            var vessel=native.vessel;var facility=ProductionFacility(plan,spec.BuildingId);var mapped=new Dictionary<uint,uint>();
            foreach(uint craftId in spec.MemberCraftPartIds)
            {
                var parts=vessel.parts.Where(p=>p!=null&&p.Resources!=null&&p.Resources.Count>0&&p.Modules.Cast<PartModule>().OfType<ColonyPlacementMarker>().Any(m=>m.craftPartId==craftId&&m.worldId==state.WorldId&&m.colonyId==plan.ColonyId&&m.plotId==facility.PlotId&&m.operationId==facility.PlacementOperationId&&m.requestFingerprint==facility.PlacementRequestFingerprint&&m.templateSha256==facility.CraftSha256)).ToArray();
                if(parts.Length!=1||parts[0].persistentId==0)throw new InvalidOperationException("Paid resource member has no unique exact craft marker mapping.");mapped.Add(craftId,parts[0].persistentId);
            }
            return new ProductionInventoryMapping{Vessel=vessel,Anchor=mapped[spec.AnchorCraftPartId],Members=mapped.Values.OrderBy(id=>id).ToArray()};
        }
        bool ProductionInventoryMappingSame(ColonyPlan plan,ColonyProductionInvestment item,ColonyProductionEndpointSpec spec,ProductionInventoryMapping expected)
        {try{var current=ResolveProductionInventoryMapping(plan,item,spec,true);return current.Vessel==expected.Vessel&&current.Anchor==expected.Anchor&&current.Members.SequenceEqual(expected.Members);}catch{return false;}}
        static bool ReadProductionEndpoint(DepotRegistryModule registry,ColonyPlan plan,ColonyProductionEndpointSpec spec,ColonyProductionEndpointReceipt receipt,string depotId,out string hash)
        {
            hash="";if(registry!=DepotRegistryModule.Instance||!registry.IsReady||registry.IsCorrupt)return false;
            var rows=registry.Registrations.Where(entry=>entry.DepotId==depotId).ToArray();if(rows.Length!=1)return false;var r=rows[0];
            if(r.OwnerKind!="colony"||r.OwnerColonyId!=plan.ColonyId||r.OwnerFacilityId!=receipt.FacilityId||r.ApprovedSpecHash!=spec.Hash||r.Anchor!=receipt.AnchorPartId||!r.MemberIds.OrderBy(id=>id).SequenceEqual(receipt.MemberPartIds))return false;
            hash=registry.CreateSelectedEffectRegistrySnapshot(depotId)?.Depots.SingleOrDefault()?.MembershipHash??"";return hash.Length==64;
        }
        static string ProductionRegistrationAfter(ColonyProductionEndpointSpec spec,ColonyProductionEndpointReceipt receipt,string membershipHash)=>ColonyEngine.ProductionRegistrationAfterWitness(spec,receipt,membershipHash);
        ColonyState AttachProductionInitialProvenance(ColonyState complete,ColonyPlan plan,ColonyProductionEndpointSpec spec,ColonyProductionEndpointReceipt receipt,Vessel vessel)
        {
            var line=plan.Buildings.Single(b=>b.Id==spec.BuildingId);var template=templates.SingleOrDefault(t=>t.Id==line.TemplateId&&t.Hash==line.TemplateHash);
            if(template==null)throw new InvalidOperationException("Exact paid manifest is unavailable for initial-content provenance; do not relabel billed stock as local output.");
            var billed=template.StartupContents.Where(c=>spec.MemberCraftPartIds.Contains(c.CraftPartId)&&c.ResourceName!="ElectricCharge"&&c.Amount>0).ToArray();if(billed.Length==0)return complete;
            InventoryObservation observation;InventoryCapabilityEvidence capability;string membership,reason;
            if(!PhysicalInventoryTransfers.TryObserve(receipt.DepotId,out observation,out capability,out membership,out reason)||membership!=complete.Plans.Single(p=>p.Id==plan.Id).Production.SelectMany(c=>c.Inventory==null?Enumerable.Empty<ColonyProductionEndpointReceipt>():c.Inventory.Endpoints).Single(e=>e.SpecId==spec.Id).MembershipHash)throw new InvalidOperationException(reason??"Exact selected initial-content observation changed.");
            var lots=new List<ColonyPhysicalLot>();
            foreach(var content in billed)
            {
                var parts=vessel.parts.Where(p=>p!=null&&receipt.MemberPartIds.Contains(p.persistentId)&&p.Modules.Cast<PartModule>().OfType<ColonyPlacementMarker>().Any(m=>m.craftPartId==content.CraftPartId&&m.worldId==complete.WorldId&&m.colonyId==plan.ColonyId&&m.operationId==ProductionFacility(plan,spec.BuildingId).PlacementOperationId&&m.requestFingerprint==ProductionFacility(plan,spec.BuildingId).PlacementRequestFingerprint&&m.templateSha256==template.CraftSha256)).ToArray();
                if(parts.Length!=1)throw new InvalidOperationException("Billed initial-content member mapping is not exact.");
                var rows=observation.Rows.Where(r=>r.MemberPersistentId==parts[0].persistentId&&r.ResourceName==content.ResourceName).ToArray();if(rows.Length!=1)throw new InvalidOperationException("Billed native tank is absent from selected readback.");
                double amount=rows[0].Amount;lots.Add(new ColonyPhysicalLot{ColonyId=plan.ColonyId,FacilityId=receipt.FacilityId,PartId=parts[0].persistentId,Resource=content.ResourceName,
                    HadImportedStock=true,ImportedAttribution=content.Amount,LastPhysicalAmount=amount,ProvenanceUncertain=amount!=content.Amount/(double)ColonyLimits.Units,
                    Witness=complete.Effects.Single(e=>e.Id==receipt.EffectId).AfterWitness});
            }
            return ColonyEngine.RecordProductionInitialProvenance(complete,receipt.EffectId,lots);
        }
        static void RequireProductionInventoryCurrent(ColonyPlan plan,ColonyProductionInvestment item,ColonyProductionClaim claim)
        {
            if(item.Inventory==null)return;var registry=DepotRegistryModule.Instance;
            if(registry==null||registry.WorldId!=item.Inventory.RegistryWorldId||claim.Inventory==null)throw new InvalidOperationException("Reviewed physical inventory belongs to an unavailable or different registry world.");
            foreach(var spec in item.Inventory.Endpoints)
            {
                var receipt=claim.Inventory.Endpoints.Single(r=>r.SpecId==spec.Id);string hash;
                if(receipt.State!="applied"||!ReadProductionEndpoint(registry,plan,spec,receipt,receipt.DepotId,out hash)||hash!=receipt.MembershipHash)throw new InvalidOperationException("Paid-created inventory endpoint was deleted or changed. This is an outage; the saved registration will not be repeated.");
            }
        }
        void PopulateProductionFuelTargets(ColonyEnvironment env)
        {
            if(state==null||FlightGlobals.Vessels==null)return;
            var registry=DepotRegistryModule.Instance;if(registry==null||!registry.IsReady||registry.IsCorrupt)return;
            var vessels=FlightGlobals.Vessels.Where(v=>v!=null).Take(513).ToArray();if(vessels.Length>512)return;
            var workshops=ReadWorkshopWitnesses(vessels);
            foreach(var plan in state.Plans.Where(p=>p.State!="cancelled"))foreach(var claim in plan.Production.Where(c=>c.Inventory?.PoliciesApplied==true))
            {
                var item=plan.Quote.ProductionInvestments.Single(i=>i.Id==claim.Id);if(!item.Inventory.AutomaticInputRefill||!item.Inventory.Resources.Any(r=>r.Resource=="Plutonium-238"))continue;
                var spec=item.Inventory.Endpoints.Single(s=>s.Role=="cultivation");var receipt=claim.Inventory.Endpoints.Single(r=>r.SpecId==spec.Id);string hash;
                if(receipt.State!="applied"||!ReadProductionEndpoint(registry,plan,spec,receipt,receipt.DepotId,out hash)||hash!=receipt.MembershipHash)continue;
                var colony=state.Colonies.Single(c=>c.Id==plan.ColonyId);var facility=colony.Facilities.Single(f=>f.Id==receipt.FacilityId);var vessel=vessels.SingleOrDefault(v=>v.id.ToString("D")==receipt.VesselId);if(vessel==null)continue;
                try
                {
                    RequireProductionLocation(plan,facility,vessel);
                    if(vessel.loaded)
                    {
                        var parts=vessel.parts.Where(p=>p!=null&&receipt.MemberPartIds.Contains(p.persistentId)&&p.Modules.Cast<PartModule>().OfType<ColonyPlacementMarker>().Any(m=>m.craftPartId==104&&m.worldId==state.WorldId&&m.colonyId==plan.ColonyId&&m.operationId==facility.PlacementOperationId&&m.requestFingerprint==facility.PlacementRequestFingerprint&&m.templateSha256==facility.CraftSha256&&m.plotId==facility.PlotId)).ToArray();if(parts.Length!=1)continue;
                        var part=parts[0];var generator=part.Modules.Cast<PartModule>().OfType<USI_Converter>().SingleOrDefault();if(generator==null)continue;RequireFixedGeneratorHardware(generator);
                        var fuel=part.Resources.Get("Plutonium-238");if(fuel==null||!fuel.flowState||fuel.maxAmount!=20)continue;
                        AddServiceTarget(env,colony,facility,vessel,part.persistentId,part.partInfo.title,"Plutonium-238",fuel.amount,fuel.maxAmount,registry,workshops);
                    }
                    else if(vessel.protoVessel!=null)
                    {
                        var parts=vessel.protoVessel.protoPartSnapshots.Where(p=>p!=null&&receipt.MemberPartIds.Contains(p.persistentId)&&p.modules.Any(m=>m.moduleName==nameof(ColonyPlacementMarker)&&m.moduleValues.GetValue("craftPartId")=="104"&&m.moduleValues.GetValue("worldId")==state.WorldId&&m.moduleValues.GetValue("colonyId")==plan.ColonyId&&m.moduleValues.GetValue("operationId")==facility.PlacementOperationId&&m.moduleValues.GetValue("requestFingerprint")==facility.PlacementRequestFingerprint&&m.moduleValues.GetValue("templateSha256")==facility.CraftSha256&&m.moduleValues.GetValue("plotId")==facility.PlotId)).ToArray();if(parts.Length!=1)continue;
                        var part=parts[0];var generator=part.partInfo?.partPrefab?.Modules.Cast<PartModule>().OfType<USI_Converter>().SingleOrDefault();if(generator==null)continue;RequireFixedGeneratorHardware(generator,false);
                        var saved=part.modules.SingleOrDefault(m=>m.moduleName=="USI_Converter");uint moduleId;double bonus;
                        if(saved==null||!uint.TryParse(saved.moduleValues.GetValue("persistentId"),out moduleId)||moduleId==0||!double.TryParse(saved.moduleValues.GetValue("EfficiencyBonus"),NumberStyles.Float,CultureInfo.InvariantCulture,out bonus)||bonus!=1)continue;
                        var fuel=part.resources.SingleOrDefault(r=>r.resourceName=="Plutonium-238");if(fuel==null||!fuel.flowState||fuel.maxAmount!=20)continue;
                        object processor;string reason;if(!TryCatchUpUtilityProcessor(vessel,env.Ut,out processor,out reason))continue;VerifyFixedGeneratorBrp(vessel,processor,part,moduleId,fuel,env.Ut);
                        AddServiceTarget(env,colony,facility,vessel,part.persistentId,part.partInfo.title,"Plutonium-238",fuel.amount,fuel.maxAmount,registry,workshops);
                    }
                    var target=env.Services.Targets.LastOrDefault(t=>t.FacilityId==facility.Id&&t.DestinationResource=="Plutonium-238");if(target!=null&&target.CanApply)target.Reason="Reviewed modeled fuel delivery to the exact paid Ranger NO_FLOW tank; actual Engineer/workshop range and inventory synchronization required. This neither invokes native Replenish nor restarts the generator.";
                }
                catch { /* Missing/changed exact native hardware remains unavailable. */ }
            }
        }
    }
}
