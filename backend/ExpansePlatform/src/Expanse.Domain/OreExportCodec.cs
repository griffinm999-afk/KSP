using System;
using System.IO;
using System.Linq;

namespace Expanse.Domain
{
    public static partial class OperationIdentity
    {
        public static string RecoveryPayloadHash(EconomicRecoveryIntent intent)
        {
            if (intent == null || String.IsNullOrWhiteSpace(intent.ShipmentId) || intent.ShipmentId.Length > 128 || intent.FundsDelta <= 0) throw new ArgumentException("Recovery intent must name one exact Ore shipment and its positive total compensation.");
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream))
            { HashText(writer, "recoverySale"); HashText(writer, intent.ShipmentId); HashI64(writer, intent.FundsDelta); return Hash(stream); }
        }
    }
    public static partial class AcceptedStateCodec
    {
        static FundsSuccessWitness? CloneFundsWitness(FundsSuccessWitness? x) => x == null ? null : new FundsSuccessWitness { BeforeFunds=x.BeforeFunds, IntendedDeltaFunds=x.IntendedDeltaFunds, IntendedAfterFunds=x.IntendedAfterFunds, ObservedAfterFunds=x.ObservedAfterFunds };
        static EconomicEffectResult CloneEconomicResult(EconomicEffectResult x) => new EconomicEffectResult { Status=x.Status, BeforeFunds=x.BeforeFunds, IntendedDeltaFunds=x.IntendedDeltaFunds, IntendedAfterFunds=x.IntendedAfterFunds, ObservedAfterKnown=x.ObservedAfterKnown, ObservedAfterFunds=x.ObservedAfterFunds, Reason=x.Reason };
        static EconomicFaultRecord CloneEconomicFault(EconomicFaultRecord x) => new EconomicFaultRecord { FaultId=x.FaultId, OperationId=x.OperationId, CommandSequence=x.CommandSequence, ShipmentId=x.ShipmentId, Result=CloneEconomicResult(x.Result) };
        static void ValidateFundsWitness(FundsSuccessWitness x) => OreExportPolicy.ValidateResult(new EconomicEffectResult { Status="applied", BeforeFunds=x.BeforeFunds, IntendedDeltaFunds=x.IntendedDeltaFunds, IntendedAfterFunds=x.IntendedAfterFunds, ObservedAfterKnown=true, ObservedAfterFunds=x.ObservedAfterFunds });
        static void ValidateEconomicFaults(AcceptedState s)
        {
            if (s.EconomicFaults == null || s.EconomicFaults.Length > AcceptedStateV2Limits.MaxFaultRecords || s.EconomicFaults.Any(x => x == null) || s.EconomicFaults.Select(x=>x.OperationId).Distinct(StringComparer.Ordinal).Count() != s.EconomicFaults.Length) throw new InvalidDataException("Economic fault bound or identity is invalid.");
            foreach (var f in s.EconomicFaults)
            {
                if (f == null || String.IsNullOrWhiteSpace(f.FaultId) || f.FaultId.Length > 128 || String.IsNullOrWhiteSpace(f.OperationId) || f.OperationId.Length > 128 || f.Result == null || f.Result.Status != "uncertain" || !s.ActiveShipments.Any(x=>x.ShipmentId == f.ShipmentId && x.DestinationKind == OreExportPolicy.VirtualDestinationKind) || !s.Receipts.Any(x=>x.OperationId == f.OperationId && x.CommandSequence == f.CommandSequence && x.OperationKind == "recoverySale" && x.Outcome == "faulted")) throw new InvalidDataException("Economic fault must retain cargo and its terminal receipt.");
                OreExportPolicy.ValidateResult(f.Result);
                var cargo = s.ActiveShipments.Single(x=>x.ShipmentId == f.ShipmentId);
                if(f.Result.IntendedDeltaFunds != OreExportPolicy.RecoveryFunds(cargo.RemainingResources,cargo.FundsPerUnit)) throw new InvalidDataException("Economic fault differs from immutable cargo terms.");
            }
        }
        static void WriteFundsWitness(BinaryWriter w, FundsSuccessWitness x) { F64(w,x.BeforeFunds); I64(w,x.IntendedDeltaFunds); F64(w,x.IntendedAfterFunds); F64(w,x.ObservedAfterFunds); }
        static FundsSuccessWitness ReadFundsWitness(BinaryReader r) => new FundsSuccessWitness { BeforeFunds=ReadF64(r), IntendedDeltaFunds=ReadI64(r), IntendedAfterFunds=ReadF64(r), ObservedAfterFunds=ReadF64(r) };
        static void WriteEconomicResult(BinaryWriter w, EconomicEffectResult x) { Text(w,x.Status); F64(w,x.BeforeFunds); I64(w,x.IntendedDeltaFunds); F64(w,x.IntendedAfterFunds); Bool(w,x.ObservedAfterKnown); F64(w,x.ObservedAfterFunds); Text(w,x.Reason); }
        static EconomicEffectResult ReadEconomicResult(BinaryReader r) => new EconomicEffectResult { Status=ReadText(r), BeforeFunds=ReadF64(r), IntendedDeltaFunds=ReadI64(r), IntendedAfterFunds=ReadF64(r), ObservedAfterKnown=ReadBool(r), ObservedAfterFunds=ReadF64(r), Reason=ReadText(r) };
        // EXS4 appends extensions after the unchanged EXS3 core, preserving prior wire hashes.
        static void WriteEconomics(BinaryWriter w, AcceptedState s)
        {
            var routes=s.RouteVersions.OrderBy(x=>x.RouteId,StringComparer.Ordinal).ThenBy(x=>x.Version).ToArray(); I32(w,routes.Length);
            foreach(var x in routes) { Text(w,x.RouteId); I64(w,x.Version); Text(w,x.DestinationKind); I64(w,x.FundsPerUnit); }
            var shipments=s.ActiveShipments.OrderBy(x=>x.ShipmentId,StringComparer.Ordinal).ToArray(); I32(w,shipments.Length);
            foreach(var x in shipments) { Text(w,x.ShipmentId); Text(w,x.DestinationKind); I64(w,x.FundsPerUnit); }
            var receipts=s.Receipts.OrderBy(x=>x.CommandSequence).ToArray(); I32(w,receipts.Length);
            foreach(var x in receipts) { I64(w,x.CommandSequence); Bool(w,x.FundsWitness != null); if(x.FundsWitness != null) WriteFundsWitness(w,x.FundsWitness); }
            var faults=s.EconomicFaults.OrderBy(x=>x.CommandSequence).ToArray(); I32(w,faults.Length);
            foreach(var x in faults) { Text(w,x.FaultId); Text(w,x.OperationId); I64(w,x.CommandSequence); Text(w,x.ShipmentId); WriteEconomicResult(w,x.Result); }
        }
        static void ReadEconomics(BinaryReader r, AcceptedState s)
        {
            if(ReadCount(r,AcceptedStateV2Limits.MaxRouteVersions) != s.RouteVersions.Length) throw new InvalidDataException("Economic route extension count differs.");
            foreach(var x in s.RouteVersions) { if(ReadText(r) != x.RouteId || ReadI64(r) != x.Version) throw new InvalidDataException("Economic route extension identity differs."); x.DestinationKind=ReadText(r); x.FundsPerUnit=ReadI64(r); }
            if(ReadCount(r,AcceptedStateV2Limits.MaxActiveShipments) != s.ActiveShipments.Length) throw new InvalidDataException("Economic cargo extension count differs.");
            foreach(var x in s.ActiveShipments) { if(ReadText(r) != x.ShipmentId) throw new InvalidDataException("Economic cargo identity differs."); x.DestinationKind=ReadText(r); x.FundsPerUnit=ReadI64(r); }
            if(ReadCount(r,MaxReceipts) != s.Receipts.Length) throw new InvalidDataException("Economic receipt extension count differs.");
            foreach(var x in s.Receipts) { if(ReadI64(r) != x.CommandSequence) throw new InvalidDataException("Economic receipt identity differs."); if(ReadBool(r)) x.FundsWitness=ReadFundsWitness(r); }
            s.EconomicFaults=new EconomicFaultRecord[ReadCount(r,AcceptedStateV2Limits.MaxFaultRecords)];
            for(var i=0;i<s.EconomicFaults.Length;i++) s.EconomicFaults[i]=new EconomicFaultRecord { FaultId=ReadText(r), OperationId=ReadText(r), CommandSequence=ReadI64(r), ShipmentId=ReadText(r), Result=ReadEconomicResult(r) };
        }
        static StateTransitionResult? RecoveryPreflight(AcceptedState prior, string operationId, string requestId, long sequence, string payloadHash, long expectedRevision, string expectedHash, EconomicRecoveryIntent intent, double appliedUt)
        {
            Validate(prior);
            var receipt=prior.Receipts.SingleOrDefault(x=>x.OperationId == operationId);
            if(receipt != null) return new StateTransitionResult { State=prior, Outcome=receipt.PayloadHash == payloadHash && receipt.CommandSequence == sequence && receipt.OperationKind == "recoverySale" ? "duplicate" : "rejected", Reason="Recovery operation was already terminal." };
            if(prior.WritesBlocked) return new StateTransitionResult { State=prior, Outcome="held", Reason="Unresolved effect fault blocks writes." };
            if(!ExactExpectedPrefix(prior,expectedRevision,expectedHash,sequence) || prior.SchemaVersion != 2 || OperationIdentity.Create(prior.WorldId,sequence,requestId) != operationId || !FiniteNonNegative(appliedUt)) return new StateTransitionResult { State=prior, Outcome="rejected", Reason="Recovery identity, prefix or UT is invalid." };
            try { if(OperationIdentity.RecoveryPayloadHash(intent) != payloadHash) return new StateTransitionResult { State=prior, Outcome="rejected", Reason="Recovery payload differs." }; } catch(ArgumentException ex) { return new StateTransitionResult { State=prior, Outcome="rejected", Reason=ex.Message }; }
            var shipment=prior.ActiveShipments.SingleOrDefault(x=>x.ShipmentId == intent.ShipmentId && !x.LegacyOpaque);
            if(shipment == null || shipment.DestinationKind != OreExportPolicy.VirtualDestinationKind || shipment.DestinationDepotId != OreExportPolicy.KerbinBuyerId || !OreExportPolicy.IsExactManifest(shipment.RemainingResources)) return new StateTransitionResult { State=prior, Outcome="rejected", Reason="Recovery requires one active immutable Ore export batch." };
            try { if(intent.FundsDelta != OreExportPolicy.RecoveryFunds(shipment.RemainingResources, shipment.FundsPerUnit)) return new StateTransitionResult { State=prior, Outcome="rejected", Reason="Recovery compensation differs from immutable cargo terms." }; } catch(ArgumentException ex) { return new StateTransitionResult { State=prior, Outcome="rejected", Reason=ex.Message }; }
            if(appliedUt < shipment.DueUt) return new StateTransitionResult { State=prior, Outcome="held", Reason="Virtual Kerbin recovery is not due." };
            if(prior.Receipts.Length >= MaxReceipts) return new StateTransitionResult { State=prior, Outcome="held", Reason="Recovery receipt capacity reached." };
            return null;
        }
        public static StateTransitionResult SettleRecovery(AcceptedState prior, string operationId, string clientRequestId, long sequence, string payloadHash, long expectedRevision, string expectedHash, EconomicRecoveryIntent intent, double appliedUt, FundsSuccessWitness witness)
        {
            var held=RecoveryPreflight(prior,operationId,clientRequestId,sequence,payloadHash,expectedRevision,expectedHash,intent,appliedUt); if(held != null) return held;
            try { if(witness == null) throw new InvalidDataException("Recovery requires exact funds witness."); ValidateFundsWitness(witness); if(witness.IntendedDeltaFunds != intent.FundsDelta) throw new InvalidDataException("Funds witness differs from recovery intent."); } catch(InvalidDataException ex) { return new StateTransitionResult { State=prior, Outcome="rejected", Reason=ex.Message }; }
            var next=Clone(prior); next.CapsuleEncodingVersion=4; next.ActiveShipments=next.ActiveShipments.Where(x=>x.ShipmentId != intent.ShipmentId).ToArray(); next.Revision++; next.AcceptedSequence=sequence;
            next.Receipts=next.Receipts.Concat(new[] { new AcceptedReceipt { WorldId=prior.WorldId, CommandSequence=sequence, OperationId=operationId, PayloadHash=payloadHash, OperationKind="recoverySale", AppliedUt=appliedUt, FundsWitness=CloneFundsWitness(witness) } }).ToArray();
            try { Validate(next); CreateCapsule(next); } catch(InvalidDataException ex) { return new StateTransitionResult { State=prior, Outcome="held", Reason=ex.Message }; }
            return new StateTransitionResult { State=next, Outcome="accepted" };
        }
        public static StateTransitionResult FaultRecovery(AcceptedState prior, string operationId, string clientRequestId, long sequence, string payloadHash, long expectedRevision, string expectedHash, EconomicRecoveryIntent intent, EconomicEffectResult result, double appliedUt)
        {
            var held=RecoveryPreflight(prior,operationId,clientRequestId,sequence,payloadHash,expectedRevision,expectedHash,intent,appliedUt); if(held != null) return held;
            try { OreExportPolicy.ValidateResult(result); if(result.IntendedDeltaFunds != intent.FundsDelta) throw new InvalidDataException("Funds result differs from recovery intent."); if(result.Status != "uncertain") throw new InvalidDataException("Fault requires uncertain funds evidence."); } catch(InvalidDataException ex) { return new StateTransitionResult { State=prior, Outcome="rejected", Reason=ex.Message }; }
            var next=Clone(prior); next.CapsuleEncodingVersion=4; next.Revision++; next.AcceptedSequence=sequence;
            next.EconomicFaults=next.EconomicFaults.Concat(new[] { new EconomicFaultRecord { FaultId="fault:"+operationId, OperationId=operationId, CommandSequence=sequence, ShipmentId=intent.ShipmentId, Result=CloneEconomicResult(result) } }).ToArray();
            next.Receipts=next.Receipts.Concat(new[] { new AcceptedReceipt { WorldId=prior.WorldId, CommandSequence=sequence, OperationId=operationId, PayloadHash=payloadHash, OperationKind="recoverySale", Outcome="faulted", AppliedUt=appliedUt } }).ToArray();
            try { Validate(next); CreateCapsule(next); } catch(InvalidDataException ex) { return new StateTransitionResult { State=prior, Outcome="held", Reason=ex.Message }; }
            return new StateTransitionResult { State=next, Outcome="faulted" };
        }
    }
}
