using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Expanse.Domain.Colonies
{
    public static partial class ColonyEngine
    {
        public const string ConstructionActivationKind="constructionActivation";
        public const string ConstructionActivationProvider="SystemHeat.0.9.1.InitialPaidCommissioning.v1";
        public static string ConstructionActivationId(ConstructionOrder order)
        {
            if(order==null || string.IsNullOrEmpty(order.Placement.OperationId))throw new InvalidDataException("Activation requires an existing paid placement operation.");
            byte[] hash=Encoding.ASCII.GetBytes(ColonyStateCodec.Hash(Encoding.UTF8.GetBytes("constructionActivation/v1|"+order.Id+"|"+order.Placement.OperationId)));
            return new Guid(hash.Take(16).ToArray()).ToString("D");
        }
        public static string ConstructionActivationBinding(ColonyState state,ConstructionOrder order)
        {
            var facility=state.Colonies.Single(c=>c.Id==order.ColonyId).Facilities.Single(f=>f.Id==order.FacilityId);
            return ColonyStateCodec.Hash(Encoding.UTF8.GetBytes(string.Join("|",state.WorldId,order.Id,order.ColonyId,order.TemplateId,order.TemplateHash,
                order.Placement.OperationId,order.Placement.RequestFingerprint,order.Placement.EscrowWitness,facility.Id,facility.VesselId,facility.CraftSha256,
                facility.FoundationId,string.Join(",",facility.PartIds.OrderBy(x=>x).Select(x=>x.ToString(CultureInfo.InvariantCulture))))));
        }
        public static bool IsConstructionActivationEffect(ColonyState state,string orderId,string effectId)
        {
            var order=state.Construction.SingleOrDefault(o=>o.Id==orderId);
            if(order==null || order.Placement.OperationId.Length==0 || effectId!=ConstructionActivationId(order))return false;
            var effect=state.Effects.SingleOrDefault(e=>e.Id==effectId);
            return effect!=null && effect.Kind==ConstructionActivationKind && effect.TargetId==orderId && effect.OperationId==effectId && effect.ColonyId==order.ColonyId &&
                effect.Provider==ConstructionActivationProvider && effect.FundsDelta==0 && effect.BeforeWitness.StartsWith("binding="+ConstructionActivationBinding(state,order)+";context=",StringComparison.Ordinal);
        }
        public static ColonyState PrepareConstructionActivation(ColonyState prior,string orderId,string context,string settingsBefore,string settingsAfter)
        {
            ColonyStateCodec.Validate(prior);
            var order=prior.Construction.Single(o=>o.Id==orderId);
            var facility=prior.Colonies.Single(c=>c.Id==order.ColonyId).Facilities.Single(f=>f.Id==order.FacilityId);
            if(!order.FundsPaid || !order.MaterialsConsumed || order.WorkCompleted<order.WorkRequired || order.Placement.Phase!="Anchored" || order.State!="commissioning" ||
                facility.State!="commissioning" || facility.PlacementOperationId!=order.Placement.OperationId || facility.ConstructionOrderId!=order.Id || facility.FoundationId.Length==0 ||
                order.Placement.EscrowWitness!=ConstructionEscrowWitness(prior,orderId))throw new InvalidDataException("Initial activation requires the exact funded, built, anchored commissioning package.");
            string id=ConstructionActivationId(order);
            if(prior.Effects.Any(e=>e.Id==id))throw new InvalidDataException("Saved initial activation already exists; reconcile it without repeating native events.");
            if(prior.Effects.Any(e=>e.State=="held" || e.State=="applying"))throw new InvalidDataException("Another external effect must be reconciled before activation.");
            ColonyStateCodec.Text(context,128,true);ColonyStateCodec.Text(settingsBefore,3500,true);ColonyStateCodec.Text(settingsAfter,3500,true);
            string prefix="binding="+ConstructionActivationBinding(prior,order)+";context="+context+";settings=";
            ColonyStateCodec.Text(prefix+settingsBefore,4096,true);ColonyStateCodec.Text(prefix+settingsAfter,4096,true);
            var next=ColonyStateCodec.Copy(prior);
            if(!CompactTerminalEffects(next,1))throw new InvalidDataException("Saved effect capacity reached before initial activation.");
            next.Effects.Add(new ColonyEffect {Id=id,OperationId=id,ColonyId=order.ColonyId,TargetId=orderId,Kind=ConstructionActivationKind,State="applying",
                Provider=ConstructionActivationProvider,BeforeWitness=prefix+settingsBefore,AfterWitness=prefix+settingsAfter,
                Reason="Initial paid reactor/radiator settings intent saved before native activation. Native resources and heat remain unchanged by this ledger."});
            next.Revision++;ColonyStateCodec.Serialize(next);return next;
        }
        public static ColonyState ObserveConstructionActivation(ColonyState prior,string orderId,string actualSettings)
        {
            ColonyStateCodec.Validate(prior);var order=prior.Construction.Single(o=>o.Id==orderId);string id=ConstructionActivationId(order);
            if(!IsConstructionActivationEffect(prior,orderId,id))throw new InvalidDataException("Initial activation lacks the exact retained paid-placement lineage.");
            var saved=prior.Effects.Single(e=>e.Id==id);int at=saved.AfterWitness.IndexOf(";settings=",StringComparison.Ordinal);
            bool match=at>=0 && saved.AfterWitness.Substring(at+10)==actualSettings;
            if(saved.State=="applied")return prior; // A later deliberate shutdown never authorizes another activation.
            if(saved.State!="applying" && saved.State!="held")throw new InvalidDataException("Initial activation is not reconcilable.");
            var next=ColonyStateCodec.Copy(prior);var effect=next.Effects.Single(e=>e.Id==id);
            effect.State=match ? "applied" : "held";
            effect.Reason=match ? "Exact native reactor/radiator target settings read back; thermal stabilization is still separately required."
                : "Initial native activation has an unresolved before/partial state. No native events are repeated; reconcile this saved child and actual hardware.";
            next.Revision++;ColonyStateCodec.Serialize(next);return next;
        }
    }
    public static partial class ColonyStateCodec
    {
        static void ValidateConstructionActivations(ColonyState state)
        {
            foreach(var effect in state.Effects.Where(e=>e.Kind==ColonyEngine.ConstructionActivationKind))
            {
                var order=state.Construction.SingleOrDefault(o=>o.Id==effect.TargetId);
                if(order==null || !order.FundsPaid || !order.MaterialsConsumed || order.WorkCompleted<order.WorkRequired || order.Placement.Phase!="Anchored" ||
                    !ColonyEngine.IsConstructionActivationEffect(state,order.Id,effect.Id))throw new InvalidDataException("Saved activation is not bound to one exact paid anchored package.");
                int before=effect.BeforeWitness.IndexOf(";settings=",StringComparison.Ordinal),after=effect.AfterWitness.IndexOf(";settings=",StringComparison.Ordinal);
                if(before<0 || after!=before || effect.BeforeWitness.Substring(0,before)!=effect.AfterWitness.Substring(0,after) ||
                    effect.BeforeWitness.Length<=before+10 || effect.AfterWitness.Length<=after+10 || effect.State=="prepared" || effect.State=="cancelled")
                    throw new InvalidDataException("Saved activation lacks immutable bounded before/target settings witnesses.");
            }
        }
    }
}
