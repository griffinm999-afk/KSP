using System;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Expanse.Domain.Colonies
{
    public static partial class ColonyEngine
    {
        static string ExecuteServices(ColonyState state, ColonyCommand command, ColonyEnvironment env)
        {
            if (command.Kind=="configureServicePolicy")
            {
                Colony(state,command.ColonyId);
                var policy=new ColonyServicePolicy {ColonyId=command.ColonyId,AutomaticEnabled=bool.Parse(Field(command,"AutomaticEnabled")),CadenceSeconds=Number(command,"CadenceSeconds",21600),TargetFillFraction=Number(command,"TargetFillFraction",.95)};
                ColonyStateCodec.Range(policy.CadenceSeconds,21600,365*ColonyLimits.KerbinDay);ColonyStateCodec.Range(policy.TargetFillFraction,.01,1);
                state.ServicePolicies.RemoveAll(x=>x.ColonyId==command.ColonyId);state.ServicePolicies.Add(policy);
                Log(state,env.Ut,command.ColonyId,command.OperationId,"servicePolicy",policy.AutomaticEnabled ? "Automatic costed reserve service enabled; real qualified workers and physical provider required." : "Automatic service disabled; existing operations retained.");return command.ColonyId;
            }
            if(command.Kind=="cancelService")
            {
                var op=state.ServiceOperations.SingleOrDefault(x=>x.Id==command.TargetId && x.ColonyId==command.ColonyId) ?? throw new InvalidDataException("Service reservation is unavailable.");
                if(op.State!="reserved" || op.SourceDebited)throw new InvalidDataException("Started or uncertain physical service requires witness reconciliation; it cannot be cancelled by restoring unknown stock.");
                Stock(Colony(state,op.ColonyId),op.SourceResource).Reserved-=op.Amount;op.State="cancelled";op.CompletedUt=env.Ut;op.Reason="Unstarted stock reservation released; physical tank unchanged.";
                foreach(var effect in state.Effects.Where(x=>x.TargetId==op.Id && x.Kind=="serviceTransfer" && x.State=="prepared"))effect.State="cancelled";
                return op.Id;
            }
            if(command.Kind!="serviceFacility")throw new InvalidDataException("This operation is not yet qualified by the colony runtime: "+command.Kind);
            var target=env.Services.Targets.SingleOrDefault(x=>x.ColonyId==command.ColonyId && x.FacilityId==command.TargetId && x.PartId==checked((uint)Integer(command,"PartId")) && x.DestinationResource==Field(command,"DestinationResource")) ?? throw new InvalidDataException("Selected installed service tank is unavailable.");
            if(target.QuoteHash!=Field(command,"ServiceQuoteHash"))throw new InvalidDataException("Installed tank or reviewed service terms changed; obtain a current quote.");
            ReserveService(state,command.OperationId,target,Integer(command,"AmountMicroUnits"),env);return command.OperationId;
        }

        static void ReserveService(ColonyState state,string id,ColonyServiceTarget target,long amount,ColonyEnvironment env)
        {
            var colony=Colony(state,target.ColonyId);
            if(!target.Current || target.ContextKey!=env.ContextKey || !target.CanApply || !target.QualifiedWorker)throw new InvalidDataException(target.Reason.Length==0 ? "Service requires current installed tank, physical worker and qualified transfer provider." : target.Reason);
            var facility=colony.Facilities.SingleOrDefault(x=>x.Id==target.FacilityId && x.PartIds.Contains(target.PartId)) ?? throw new InvalidDataException("Installed service part is not an adopted facility member.");
            if(facility.State=="retired")throw new InvalidDataException("Retired facility cannot be serviced.");
            if(state.ServiceOperations.Any(x=>x.FacilityId==target.FacilityId && x.PartId==target.PartId && x.DestinationResource==target.DestinationResource && x.State!="complete" && x.State!="cancelled"))throw new InvalidDataException("This installed tank already has a reserved/uncertain service operation.");
            ColonyStateCodec.Quantity(amount);ColonyStateCodec.Quantity(target.Amount);ColonyStateCodec.Quantity(target.Capacity);
            if(amount<=0 || amount>target.Capacity-target.Amount)throw new InvalidDataException("Service amount exceeds actual installed free tank capacity.");
            bool replacement=target.SourceResource=="MaterialKits" && target.DestinationResource=="ReplacementParts";
            bool localFuel=ProductionFuelTargetAllowed(state,target);
            if(!replacement && target.SourceResource!=target.DestinationResource || target.DestinationResource!="Machinery" && target.DestinationResource!="ReplacementParts" && target.DestinationResource!="EnrichedUranium"&&!localFuel)throw new InvalidDataException("Installed service resource mapping is not supported.");
            var stock=Stock(colony,target.SourceResource);
            if(amount>stock.Amount-stock.Reserved-stock.SupportFloor)throw new InvalidDataException("Funded colony-owned service inputs are insufficient; existing reservations and support floor are protected.");
            while(state.ServiceOperations.Count>=256)
            {
                int terminal=state.ServiceOperations.FindIndex(x=>x.State=="complete" || x.State=="cancelled");
                if(terminal<0)throw new InvalidDataException("Service operation capacity reached; reconcile pending stock/tanks.");state.ServiceOperations.RemoveAt(terminal);
            }
            if(!CompactTerminalEffects(state,1))throw new InvalidDataException("External effect capacity reached; reconcile pending operations.");
            stock.Reserved=checked(stock.Reserved+amount);
            var op=new ColonyServiceOperation {Id=id,ColonyId=colony.Id,FacilityId=facility.Id,PartId=target.PartId,DepotId=target.DepotId,SourceResource=target.SourceResource,DestinationResource=target.DestinationResource,Amount=amount,CreatedUt=env.Ut,Provider=target.Provider};
            state.ServiceOperations.Add(op);state.Effects.Add(new ColonyEffect {Id=Guid.NewGuid().ToString("D"),OperationId=id,ColonyId=colony.Id,TargetId=id,Kind="serviceTransfer"});
            Log(state,env.Ut,colony.Id,id,"serviceReserved",target.WorkerWitness+"; real "+target.SourceResource+" reserved for installed "+target.DestinationResource+" tank "+target.PartId+".");
        }

        static void AdvanceServices(ColonyState state,ColonyEnvironment env,double ut)
        {
            // Physical witnesses describe now, never an invented historical tank.
            if(ut!=env.Ut)return;
            int queued=0;
            foreach(var policy in state.ServicePolicies.Where(x=>x.AutomaticEnabled))
            foreach(var target in env.Services.Targets.Where(x=>x.ColonyId==policy.ColonyId && x.Current && x.CanApply && x.QualifiedWorker))
            {
                if(queued>=4)return;
                if(state.ServiceOperations.Any(x=>x.ColonyId==policy.ColonyId && x.PartId==target.PartId && x.DestinationResource==target.DestinationResource && (x.State!="complete" && x.State!="cancelled" || x.CompletedUt>ut-policy.CadenceSeconds)))continue;
                var colony=Colony(state,policy.ColonyId);var stock=colony.Stock.SingleOrDefault(x=>x.Resource==target.SourceResource);if(stock==null)continue;
                long desired=checked((long)decimal.Floor(target.Capacity*(decimal)policy.TargetFillFraction));
                long amount=Math.Min(desired-target.Amount,stock.Amount-stock.Reserved-stock.SupportFloor);if(amount<=0)continue;
                try {ReserveService(state,Guid.NewGuid().ToString("D"),target,amount,env);queued++;state.Revision++;}
                catch(InvalidDataException) { /* A qualified manual review can explain the current blocker without fabricating an automatic order. */ }
            }
        }

        public static ColonyState HoldPreparedService(ColonyState prior,string operationId,string provider,string beforeWitness)
        {
            var state=ColonyStateCodec.Copy(prior);var op=state.ServiceOperations.Single(x=>x.Id==operationId);
            if(op.State!="reserved" || op.SourceDebited || string.IsNullOrWhiteSpace(beforeWitness))throw new InvalidDataException("Service lacks an unstarted reservation and exact physical before witness.");
            var stock=Stock(Colony(state,op.ColonyId),op.SourceResource);stock.Reserved-=op.Amount;stock.Amount-=op.Amount;stock.ImportedAmount=Math.Max(0,stock.ImportedAmount-op.Amount);
            op.SourceDebited=true;op.State="held";op.Provider=provider;op.BeforeWitness=beforeWitness;op.Reason="Source debited into held service escrow; physical credit must be read back or rolled back exactly.";
            var effect=state.Effects.Single(x=>x.Kind=="serviceTransfer" && x.TargetId==op.Id);effect.State="held";effect.Provider=provider;effect.BeforeWitness=beforeWitness;effect.Reason=op.Reason;
            state.Revision++;ColonyStateCodec.Serialize(state);return state;
        }

        public static ColonyState CompletePreparedService(ColonyState held,string operationId,double ut,string verifiedAfterWitness)
        {
            var state=ColonyStateCodec.Copy(held);var op=state.ServiceOperations.Single(x=>x.Id==operationId);
            if(op.State=="complete")return held;
            if(op.State!="held" || !op.SourceDebited || string.IsNullOrWhiteSpace(verifiedAfterWitness))throw new InvalidDataException("Service completion requires held debit and exact verified physical readback.");
            op.State="complete";op.CompletedUt=ut;op.AfterWitness=verifiedAfterWitness;op.Reason="Qualified worker service delivered; exact installed physical tank credit verified.";
            var effect=state.Effects.Single(x=>x.Kind=="serviceTransfer" && x.TargetId==op.Id);effect.State="applied";effect.AfterWitness=verifiedAfterWitness;effect.Reason=op.Reason;
            Log(state,ut,op.ColonyId,op.Id,"serviceDelivered",op.Reason+" "+op.SourceResource+"→"+op.DestinationResource+"; part "+op.PartId+".",0,op.SourceResource,-op.Amount);state.Revision++;ColonyStateCodec.Serialize(state);return state;
        }
        public static ColonyState RejectPreparedService(ColonyState prior,string operationId,double ut,string reason,string rollbackWitness)
        {
            var state=ColonyStateCodec.Copy(prior);var op=state.ServiceOperations.Single(x=>x.Id==operationId);
            if(op.State!="reserved" || op.SourceDebited || string.IsNullOrWhiteSpace(rollbackWitness))throw new InvalidDataException("Only exact confirmed rollback may release an unstarted source reservation.");
            Stock(Colony(state,op.ColonyId),op.SourceResource).Reserved-=op.Amount;op.State="cancelled";op.CompletedUt=ut;op.Reason=reason;op.BeforeWitness=rollbackWitness;op.AfterWitness=rollbackWitness;
            var effect=state.Effects.Single(x=>x.Kind=="serviceTransfer" && x.TargetId==op.Id);effect.State="cancelled";effect.BeforeWitness=rollbackWitness;effect.AfterWitness=rollbackWitness;effect.Reason=reason;
            Log(state,ut,op.ColonyId,op.Id,"serviceFailed",reason);state.Revision++;ColonyStateCodec.Serialize(state);return state;
        }
    }
}
