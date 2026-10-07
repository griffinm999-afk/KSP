using System;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Expanse.Domain.Colonies
{
    public static partial class ColonyEngine
    {
        public static bool PhysicalPending(ColonyPhysicalTransfer op) => op.State=="reserved" || op.State=="held";
        public static long PhysicalSourceReserved(ColonyState state,string colonyId,string resource) => state.PhysicalTransfers.Where(o=>o.ColonyId==colonyId&&o.Resource==resource&&o.Direction=="toPhysical"&&PhysicalPending(o)&&!o.OwnedSourceDebited).Sum(o=>o.Amount);
        public static long PhysicalIncomingReserved(ColonyState state,string colonyId,string resource) => state.PhysicalTransfers.Where(o=>o.ColonyId==colonyId&&o.Resource==resource&&o.Direction=="toColony"&&PhysicalPending(o)).Sum(o=>o.Amount);
        static long PhysicalMaterialPromised(ColonyState state,string planId,string resource) => state.PhysicalTransfers.Where(o=>o.PlanId==planId&&o.Resource==resource&&PhysicalPending(o)).Sum(o=>o.PlannedMaterialAmount);
        public static long PlanningImportAmount(ColonyPlanningImport load) => checked(load.Amount-load.SubstitutedLocallyAmount);
        public static long PlanningImportFunds(ColonyPlanningImport load) => PlanningImportAmount(load)==0?0:checked(ScaledProduct(PlanningImportAmount(load),load.FundsPerUnit)+load.FreightFunds);
        static bool AccessibleLocal(ColonyLocalStock s,ColonyRecord colony,ColonyEnvironment env) => s.ColonyId==colony.Id && s.ContextKey==env.ContextKey && s.Current && s.CanApply && s.WarehouseEnabled && s.FlowAllowed && s.WithinRange &&
            colony.Facilities.Any(f=>f.Id==s.FacilityId && f.State!="retired" && f.VesselId==s.VesselId && f.PartIds.Contains(s.PartId)) && s.ObservedUt==env.Ut && s.MembershipHash.Length>0 && s.AccessWitness.Length>0;
        public static ColonyPhysicalTransferQuote QuotePhysicalTransfer(ColonyState state,string colonyId,string localStockId,string direction,long amountMicro,ColonyEnvironment env)
        {
            var q=new ColonyPhysicalTransferQuote {ColonyId=colonyId,LocalStockId=localStockId,ContextKey=env.ContextKey,Revision=state.Revision,Direction=direction,Amount=amountMicro};
            try
            {
                ValidateEnvironment(state,env);ColonyStateCodec.ValidateLocalStocks(env.Planning.LocalStocks);ColonyStateCodec.Quantity(amountMicro);
                if(amountMicro==0 || (direction!="toColony" && direction!="toPhysical"))throw new InvalidDataException("Choose a positive transfer amount and direction.");
                var colony=Colony(state,colonyId);var local=env.Planning.LocalStocks.SingleOrDefault(s=>s.Id==localStockId) ?? throw new InvalidDataException("Refresh the actual registered warehouse observation.");
                q.Resource=local.Resource;q.Provider=local.Provider;q.AccessWitness=local.AccessWitness;q.PhysicalBefore=local.Amount;
                if(!AccessibleLocal(local,colony,env))throw new InvalidDataException(local.Reason.Length>0?local.Reason:"Current registered, in-range, unlocked warehouse access is required.");
                if(state.PhysicalTransfers.Any(o=>PhysicalPending(o)&&o.PartId==local.PartId&&o.Resource==local.Resource) || state.ServiceOperations.Any(o=>(o.State=="reserved"||o.State=="held")&&o.PartId==local.PartId&&o.DestinationResource==local.Resource))throw new InvalidDataException("This exact physical tank is already reserved.");
                if(state.Effects.Any(e=>e.State=="held"||e.State=="applying"))throw new InvalidDataException("An external effect requires reconciliation before another transfer.");
                var stock=Stock(colony,local.Resource);q.OwnedBefore=stock.Amount;
                if(direction=="toColony")
                {
                    if(amountMicro>local.Amount-local.PhysicalReserve)throw new InvalidDataException("The unlocked source has insufficient stock above its native input buffer.");
                    if(amountMicro>stock.Capacity-stock.Amount-stock.IncomingReserved)throw new InvalidDataException("Owned receiving capacity is already occupied or promised.");
                    q.PhysicalAfter=local.Amount-amountMicro;q.OwnedAfter=stock.Amount+amountMicro;
                    var lot=state.PhysicalLots.SingleOrDefault(l=>l.PartId==local.PartId&&l.Resource==local.Resource);
                    q.Provenance=lot==null?"Verified physical stock; no colony import attribution recorded.":lot.ProvenanceUncertain||Math.Floor(lot.LastPhysicalAmount*ColonyLimits.Units)!=local.Amount?lot.HadImportedStock?"Physical movement since the last witness; imported/mixed attribution retained conservatively.":"Verified physical movement; no previous colony import into this tank.":"Exact physical stock with saved import attribution.";
                }
                else
                {
                    if(amountMicro>stock.Amount-stock.Reserved-stock.SupportFloor)throw new InvalidDataException("Owned source stock is insufficient above existing material claims and support reserves.");
                    if(amountMicro>local.Capacity-local.Amount)throw new InvalidDataException("The exact physical destination tank lacks capacity.");
                    q.PhysicalAfter=local.Amount+amountMicro;q.OwnedAfter=stock.Amount-amountMicro;q.Provenance="Owned stock debit; imported attribution follows the physical delivery.";
                }
                q.CanApprove=true;q.Reason="Exact single-owner transfer; provider apply, synchronize and readback required.";
            }
            catch(Exception ex) when(ex is InvalidDataException||ex is OverflowException||ex is ArgumentException) {q.Reason=ex.Message;}
            q.Id=ColonyStateCodec.Hash(ColonyJson.Serialize(q,65536));return q;
        }
        static string ExecutePhysicalProcurement(ColonyState state,ColonyCommand command,ColonyEnvironment env)
        {
            if(command.Kind=="configurePhysicalProcurement")
            {
                Colony(state,command.ColonyId);var p=state.PhysicalPolicies.SingleOrDefault(x=>x.ColonyId==command.ColonyId);
                if(p==null){p=new ColonyPhysicalPolicy {ColonyId=command.ColonyId};state.PhysicalPolicies.Add(p);}
                p.Enabled=bool.Parse(Field(command,"Enabled"));p.NextReviewUt=env.Ut;p.Reason=p.Enabled?"Verified local procurement before imports and bounded native input buffers enabled.":"Automatic local procurement and physical input buffers disabled.";return p.ColonyId;
            }
            if(command.Kind=="cancelPhysicalTransfer")
            {
                var op=state.PhysicalTransfers.SingleOrDefault(o=>o.Id==command.TargetId&&o.ColonyId==command.ColonyId)??throw new InvalidDataException("Transfer not found.");
                if(op.State=="cancelled"||op.State=="complete")return op.Id;
                if(op.State!="reserved"||op.OwnedSourceDebited)throw new InvalidDataException("A physical attempt is held; cancellation cannot guess a refund or repeat its debit.");
                ReleasePhysicalReservation(state,op);op.State="cancelled";op.CompletedUt=env.Ut;op.Reason="Unattempted physical reservation cancelled.";var effect=PhysicalEffect(state,op);effect.State="cancelled";effect.Reason=op.Reason;return op.Id;
            }
            var quote=QuotePhysicalTransfer(state,command.ColonyId,command.TargetId,Field(command,"Direction"),Integer(command,"AmountMicroUnits"),env);
            if(!quote.CanApprove)throw new InvalidDataException(quote.Reason);
            if(quote.Id!=command.QuoteId)throw new InvalidDataException("Physical transfer quote changed; refresh and review actual stock and access.");
            string planId=quote.Direction=="toColony"?state.Plans.SingleOrDefault(p=>p.ColonyId==quote.ColonyId&&PlanningActive(p))?.Id??"":"";
            return ReservePhysicalTransfer(state,command.OperationId,quote,env,planId);
        }
        static string ReservePhysicalTransfer(ColonyState state,string operationId,ColonyPhysicalTransferQuote quote,ColonyEnvironment env,string planId)
        {
            if(!MakeProductionTransferRoom(state))throw new InvalidDataException("Saved physical operation capacity reached.");
            var local=env.Planning.LocalStocks.Single(s=>s.Id==quote.LocalStockId);var colony=Colony(state,quote.ColonyId);var stock=Stock(colony,quote.Resource);
            long material=0;
            if(planId.Length>0)
            {
                var plan=state.Plans.Single(p=>p.Id==planId&&PlanningActive(p));var claim=plan.Claims.SingleOrDefault(c=>c.Resource==quote.Resource);
                long promised=plan.Incoming.Where(i=>!i.Credited&&i.Resource==quote.Resource).Sum(i=>i.MaterialAmount)+PhysicalMaterialPromised(state,planId,quote.Resource);
                material=claim==null?0:Math.Min(quote.Amount,Math.Max(0,claim.Remaining-claim.Reserved-promised));
            }
            if(quote.Direction=="toColony")stock.IncomingReserved=checked(stock.IncomingReserved+quote.Amount);else stock.Reserved=checked(stock.Reserved+quote.Amount);
            var op=new ColonyPhysicalTransfer {Id=operationId,ColonyId=colony.Id,FacilityId=local.FacilityId,LocalStockId=local.Id,PlanId=planId,DepotId=local.DepotId,PartId=local.PartId,Resource=quote.Resource,Direction=quote.Direction,Amount=quote.Amount,PlannedMaterialAmount=material,CreatedUt=env.Ut,Provider=local.Provider,MembershipHash=local.MembershipHash,AccessWitness=local.AccessWitness,Provenance=quote.Provenance};
            state.PhysicalTransfers.Add(op);state.Effects.Add(new ColonyEffect {Id=PlanningChildId(op.Id,"physical-effect"),OperationId=op.Id,ColonyId=op.ColonyId,TargetId=op.Id,Kind="physicalTransfer"});
            Log(state,env.Ut,colony.Id,op.Id,"physicalReserved","Registered warehouse "+local.PartName+"; "+op.Direction+" "+op.Resource+"; exact physical witness required.");return op.Id;
        }
        static ColonyEffect PhysicalEffect(ColonyState state,ColonyPhysicalTransfer op) => state.Effects.Single(e=>e.Kind=="physicalTransfer"&&e.TargetId==op.Id);
        static void ReleasePhysicalReservation(ColonyState state,ColonyPhysicalTransfer op)
        {var stock=Stock(Colony(state,op.ColonyId),op.Resource);if(op.Direction=="toColony")stock.IncomingReserved-=op.Amount;else stock.Reserved-=op.Amount;}
        public static ColonyState HoldPreparedPhysicalTransfer(ColonyState prior,string operationId,string provider,string beforeWitness,string afterWitness,double physicalBefore,double physicalAfter)
        {
            var state=ColonyStateCodec.Copy(prior);var op=state.PhysicalTransfers.Single(o=>o.Id==operationId);
            if(op.State!="reserved"||op.OwnedSourceDebited||string.IsNullOrWhiteSpace(beforeWitness)||string.IsNullOrWhiteSpace(afterWitness))throw new InvalidDataException("An unattempted reservation and exact physical witnesses are required.");
            ColonyStateCodec.Time(physicalBefore);ColonyStateCodec.Time(physicalAfter);
            double delta=op.Amount/(double)ColonyLimits.Units,expected=op.Direction=="toColony"?physicalBefore-delta:physicalBefore+delta;
            if(physicalAfter!=expected)throw new InvalidDataException("Physical witness does not match the exact conserved manifest.");
            var lot=state.PhysicalLots.SingleOrDefault(l=>l.PartId==op.PartId&&l.Resource==op.Resource);
            if(lot==null){if(state.PhysicalLots.Count>=1024)throw new InvalidDataException("Physical provenance witness capacity reached.");lot=new ColonyPhysicalLot {ColonyId=op.ColonyId,FacilityId=op.FacilityId,PartId=op.PartId,Resource=op.Resource,LastPhysicalAmount=physicalBefore};state.PhysicalLots.Add(lot);}
            // A native consumer or producer can move resources between observations.
            // Never erase a known import's origin merely because the tank depleted.
            if(lot.LastPhysicalAmount!=physicalBefore)lot.ProvenanceUncertain=true;
            op.ProvenanceUncertain=lot.ProvenanceUncertain;op.PhysicalBefore=physicalBefore;op.PhysicalAfter=physicalAfter;op.Provider=provider;op.BeforeWitness=beforeWitness;op.AfterWitness=afterWitness;
            var stock=Stock(Colony(state,op.ColonyId),op.Resource);
            if(op.Direction=="toPhysical")
            {op.ImportedAttribution=Math.Min(op.Amount,stock.ImportedAmount);stock.Reserved-=op.Amount;stock.Amount-=op.Amount;stock.ImportedAmount-=op.ImportedAttribution;op.OwnedSourceDebited=true;}
            else op.ImportedAttribution=lot.ProvenanceUncertain&&lot.HadImportedStock?op.Amount:Math.Min(op.Amount,lot.ImportedAttribution);
            op.State="held";op.Reason="Exact physical attempt held before provider mutation; unknown outcomes cannot be repeated or refunded.";
            var effect=PhysicalEffect(state,op);effect.State="held";effect.Provider=provider;effect.BeforeWitness=beforeWitness;effect.AfterWitness=afterWitness;effect.Reason=op.Reason;
            state.Revision++;ColonyStateCodec.Serialize(state);return state;
        }
        public static ColonyState CompletePreparedPhysicalTransfer(ColonyState held,string operationId,double ut,string verifiedAfterWitness)
        {
            var state=ColonyStateCodec.Copy(held);var op=state.PhysicalTransfers.Single(o=>o.Id==operationId);
            if(op.State=="complete")return held;
            if(op.State!="held"||string.IsNullOrWhiteSpace(verifiedAfterWitness))throw new InvalidDataException("Completion requires a durable hold and exact provider readback.");
            var stock=Stock(Colony(state,op.ColonyId),op.Resource);var lot=state.PhysicalLots.Single(l=>l.PartId==op.PartId&&l.Resource==op.Resource);
            if(op.Direction=="toColony")
            {
                stock.IncomingReserved-=op.Amount;stock.Amount=checked(stock.Amount+op.Amount);stock.ImportedAmount=checked(stock.ImportedAmount+op.ImportedAttribution);
                lot.ImportedAttribution=Math.Max(0,lot.ImportedAttribution-op.ImportedAttribution);
                var plan=state.Plans.SingleOrDefault(p=>p.Id==op.PlanId&&PlanningActive(p));
                if(plan!=null)
                {
                    if(op.PlannedMaterialAmount>0){var claim=plan.Claims.Single(c=>c.Resource==op.Resource);claim.Reserved+=op.PlannedMaterialAmount;stock.Reserved+=op.PlannedMaterialAmount;}
                    long remaining=op.Amount;
                    foreach(var load in plan.Imports.Where(i=>i.Resource==op.Resource&&i.ShipmentId.Length==0))
                    {long before=PlanningImportFunds(load),take=Math.Min(remaining,PlanningImportAmount(load));load.SubstitutedLocallyAmount+=take;plan.RemainingFunds-=before-PlanningImportFunds(load);remaining-=take;if(remaining==0)break;}
                }
            }
            else {lot.ImportedAttribution=checked(lot.ImportedAttribution+op.ImportedAttribution);lot.HadImportedStock=lot.HadImportedStock||op.ImportedAttribution>0;}
            lot.LastPhysicalAmount=op.PhysicalAfter;lot.Witness=verifiedAfterWitness;lot.ProvenanceUncertain=lot.ProvenanceUncertain||op.ProvenanceUncertain;
            op.State="complete";op.CompletedUt=ut;op.AfterWitness=verifiedAfterWitness;op.Reason="Exact registered physical apply, synchronization and readback verified; one owner credited.";
            var effect=PhysicalEffect(state,op);effect.State="applied";effect.AfterWitness=verifiedAfterWitness;effect.Reason=op.Reason;
            Log(state,ut,op.ColonyId,op.Id,"physicalDelivered",op.Reason+" "+op.Provenance,0,op.Resource,op.Direction=="toColony"?op.Amount:-op.Amount);state.Revision++;ColonyStateCodec.Serialize(state);return state;
        }
        public static ColonyState RejectPreparedPhysicalTransfer(ColonyState prior,string operationId,double ut,string rollbackWitness)
        {
            var state=ColonyStateCodec.Copy(prior);var op=state.PhysicalTransfers.Single(o=>o.Id==operationId);
            if(op.State!="reserved"||op.OwnedSourceDebited||string.IsNullOrWhiteSpace(rollbackWitness))throw new InvalidDataException("Only exact confirmed physical rollback can release the prior reservation.");
            ReleasePhysicalReservation(state,op);op.State="cancelled";op.CompletedUt=ut;op.BeforeWitness=rollbackWitness;op.AfterWitness=rollbackWitness;op.Reason="Physical attempt rejected; exact rollback confirmed. Reservation released.";
            var effect=PhysicalEffect(state,op);effect.State="cancelled";effect.BeforeWitness=rollbackWitness;effect.AfterWitness=rollbackWitness;effect.Reason=op.Reason;state.Revision++;ColonyStateCodec.Serialize(state);return state;
        }
        static bool LocalProcurementAllowed(ColonyState state,string colonyId) => state.PhysicalPolicies.SingleOrDefault(p=>p.ColonyId==colonyId)?.Enabled!=false;
        static bool TryReserveLocalShortage(ColonyState state,ColonyRecord colony,string resource,long shortage,ColonyEnvironment env,string planId)
        {
            var reviewed=planId.Length==0?null:state.Plans.Single(p=>p.Id==planId).Quote.StartupPolicies;
            if(reviewed!=null ? !reviewed.LocalProcurementEnabled : !LocalProcurementAllowed(state,colony.Id))return false;
            if(state.PhysicalTransfers.Any(o=>o.ColonyId==colony.Id&&o.Resource==resource&&PhysicalPending(o)))return true;
            foreach(var local in env.Planning.LocalStocks.Where(s=>s.Resource==resource&&AccessibleLocal(s,colony,env)).OrderBy(s=>s.PartId))
            {
                long free=Math.Max(0,local.Amount-local.PhysicalReserve),receiving=Stock(colony,resource).Capacity-Stock(colony,resource).Amount-Stock(colony,resource).IncomingReserved;
                long amount=Math.Min(1000*ColonyLimits.Units,Math.Min(shortage,Math.Min(free,receiving)));if(amount<=0)continue;
                var quote=QuotePhysicalTransfer(state,colony.Id,local.Id,"toColony",amount,env);if(!quote.CanApprove)continue;
                ReservePhysicalTransfer(state,Guid.NewGuid().ToString("D"),quote,env,planId);return true;
            }
            return false;
        }
        // Scheduling hysteresis only; exact quantities, native input reserves and
        // all quote/provider checks remain unchanged. Small/depleted tanks and
        // genuine owned support shortages still receive prompt service.
        static bool MeaningfulAutomaticPhysicalBatch(ColonyLocalStock local, ColonyStock stock, long amount, bool refill)
        {
            long batch=Math.Min(ColonyLimits.Units,Math.Max(1,local.Capacity/100));
            if(amount>=batch)return true;
            if(refill)return local.Amount<=Math.Max(batch,local.PhysicalReserve/2);
            return local.Capacity-local.Amount<=batch || stock.Amount-stock.Reserved<stock.SupportFloor ||
                stock.Amount-stock.Reserved==0 && stock.IncomingReserved==0;
        }
        public static ColonyState RunPhysicalProcurementPolicies(ColonyState prior,ColonyEnvironment env)
        {
            ColonyStateCodec.Validate(prior);ValidateEnvironment(prior,env);
            return RunPhysicalProcurementPoliciesValidated(prior,env);
        }

        static ColonyState RunPhysicalProcurementPoliciesValidated(ColonyState prior,ColonyEnvironment env)
        {
            ValidateEnvironment(prior,env);
            ColonyStateCodec.ValidateLocalStocks(env.Planning.LocalStocks);
            if(prior.SimulatedUt!=env.Ut||prior.Effects.Any(e=>e.State=="held"||e.State=="applying"))return prior;
            // An approved automatic service policy may obtain its actual input
            // shortfall from local reserves. This spends no cash and does not
            // consume or credit the installed destination before its own worker.
            foreach(var service in prior.ServicePolicies.Where(p=>p.AutomaticEnabled))
            foreach(var target in env.Services.Targets.Where(t=>t.ColonyId==service.ColonyId&&t.Current&&t.CanApply&&t.QualifiedWorker&&t.ContextKey==env.ContextKey&&t.ObservedUt==env.Ut))
            {
                if(prior.PhysicalTransfers.Any(o=>o.ColonyId==service.ColonyId&&o.Resource==target.SourceResource&&PhysicalPending(o)))continue;
                var stock=Colony(prior,service.ColonyId).Stock.SingleOrDefault(s=>s.Resource==target.SourceResource);if(stock==null)continue;
                long desired=(long)decimal.Floor(target.Capacity*(decimal)service.TargetFillFraction)-target.Amount;
                long shortage=desired-Math.Max(0,stock.Amount-stock.Reserved-stock.SupportFloor)-stock.IncomingReserved;if(shortage<=0)continue;
                var state=ColonyStateCodec.Copy(prior);var colony=Colony(state,service.ColonyId);
                if(TryReserveLocalShortage(state,colony,target.SourceResource,shortage,env,""))return FinishPlanningTransition(state);
            }
            foreach(var saved in prior.PhysicalPolicies.Where(p=>p.Enabled&&p.NextReviewUt<=env.Ut).OrderBy(p=>p.NextReviewUt))
            {
                var state=ColonyStateCodec.Copy(prior);var policy=state.PhysicalPolicies.Single(p=>p.ColonyId==saved.ColonyId);var colony=Colony(state,policy.ColonyId);policy.NextReviewUt=env.Ut+policy.CadenceSeconds;
                foreach(var local in env.Planning.LocalStocks.Where(s=>s.NativeInput&&AccessibleLocal(s,colony,env)).OrderBy(s=>s.PartId))
                {
                    var stock=colony.Stock.SingleOrDefault(s=>s.Resource==local.Resource);if(stock==null)continue;
                    long target=(long)decimal.Floor(local.Capacity*(decimal)policy.NativeInputTargetFraction);
                    long amount=Math.Min(policy.MaximumTransfer,Math.Min(target-local.Amount,stock.Amount-stock.Reserved-stock.SupportFloor));if(amount<=0||!MeaningfulAutomaticPhysicalBatch(local,stock,amount,true))continue;
                    var q=QuotePhysicalTransfer(state,colony.Id,local.Id,"toPhysical",amount,env);if(!q.CanApprove)continue;
                    ReservePhysicalTransfer(state,Guid.NewGuid().ToString("D"),q,env,"");policy.Reason="One bounded owned stock delivery reserved for an actual native converter input; no modeled production outputs.";return FinishPlanningTransition(state);
                }
                policy.Reason="Current owned stock or native input buffer already covers the bounded target.";return FinishPlanningTransition(state);
            }
            return prior;
        }
    }
}
