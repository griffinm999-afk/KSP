using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Expanse.Domain
{
    // Plain DTOs intentionally avoid framework-specific serialization dependencies.
    public sealed class AcceptedState
    {
        public int SchemaVersion { get; set; } = 1;
        /// <summary>Capsule wire encoding, preserved so loaded EXS2 state hashes remain byte-identical until an accepted upgrade.</summary>
        public int CapsuleEncodingVersion { get; set; } = 2;
        public string WorldId { get; set; } = "";
        public string CheckpointId { get; set; } = "";
        public string? ParentCheckpointId { get; set; }
        public string? ParentBranchId { get; set; }
        public long Revision { get; set; }
        public long AcceptedSequence { get; set; }
        public long CompactionWatermark { get; set; }
        public long Counter { get; set; }
        public DepotRecord[] Depots { get; set; } = Array.Empty<DepotRecord>();
        public string DepotRegistryVersion { get; set; } = "";
        public string DepotRegistryHash { get; set; } = "";
        public RouteRecord[] Routes { get; set; } = Array.Empty<RouteRecord>();
        public RuleRecord[] Rules { get; set; } = Array.Empty<RuleRecord>();
        public ShipmentRecord[] Shipments { get; set; } = Array.Empty<ShipmentRecord>();
        public AcceptedReceipt[] Receipts { get; set; } = Array.Empty<AcceptedReceipt>();
        public RouteVersionRecord[] RouteVersions { get; set; } = Array.Empty<RouteVersionRecord>();
        public DeliveryRuleRecord[] DeliveryRules { get; set; } = Array.Empty<DeliveryRuleRecord>();
        public ActiveShipmentRecord[] ActiveShipments { get; set; } = Array.Empty<ActiveShipmentRecord>();
        public PhysicalFaultRecord[] Faults { get; set; } = Array.Empty<PhysicalFaultRecord>();
        public EconomicFaultRecord[] EconomicFaults { get; set; } = Array.Empty<EconomicFaultRecord>();
        public bool WritesBlocked => Faults != null && Faults.Length > 0 || EconomicFaults != null && EconomicFaults.Length > 0;
    }

    public sealed class DepotRecord { public string DepotId { get; set; } = ""; public long MembershipRevision { get; set; } public string MembershipHash { get; set; } = ""; public bool Active { get; set; } = true; }
    public sealed class RouteRecord { public string RouteId { get; set; } = ""; public long Revision { get; set; } }
    public sealed class RuleRecord { public string RuleId { get; set; } = ""; public long Revision { get; set; } }
    public sealed class ShipmentRecord { public string ShipmentId { get; set; } = ""; public long Revision { get; set; } }

    public sealed class AcceptedReceipt
    {
        public string WorldId { get; set; } = "";
        public long CommandSequence { get; set; }
        public string OperationId { get; set; } = "";
        public string PayloadHash { get; set; } = "";
        public string Outcome { get; set; } = "accepted";
        public string OperationKind { get; set; } = "counterIncrement";
        public long CounterDelta { get; set; }
        public long TargetCompactionWatermark { get; set; }
        public double AppliedUt { get; set; }
        public PhysicalSuccessWitness? PhysicalWitness { get; set; }
        public FundsSuccessWitness? FundsWitness { get; set; }
    }

    /// <summary>Preconstructed successful physical readback witness committed only after exact post-write readback.</summary>
    public sealed class PhysicalSuccessWitness
    {
        public string ProviderId { get; set; } = "";
        public string ProviderVersion { get; set; } = "";
        public PhysicalSuccessWitnessRow[] Rows { get; set; } = Array.Empty<PhysicalSuccessWitnessRow>();
    }

    public sealed class PhysicalSuccessWitnessRow
    {
        public uint MemberPersistentId { get; set; }
        public string ResourceName { get; set; } = "";
        public double BeforeAmount { get; set; }
        public double IntendedAfterAmount { get; set; }
        public double ObservedAfterAmount { get; set; }
    }

    public sealed class RecoveryCapsule
    {
        public int SchemaVersion { get; set; } = 1;
        public string WorldId { get; set; } = "";
        public string StateBytesBase64 { get; set; } = "";
        public string StateSha256 { get; set; } = "";
    }

    public sealed class StateTransitionResult
    {
        public AcceptedState State { get; set; } = new AcceptedState();
        public string Outcome { get; set; } = "rejected";
        public string? Reason { get; set; }
    }

    /// <summary>Canonical, explicitly typed binary accepted-state encoding used for hashes and capsules.</summary>
    public static partial class AcceptedStateCodec
    {
        public const int MaxDecodedBytes = 36 * 1024;
        public const int MaxEncodedCapsuleBytes = 48 * 1024;
        public const int MaxReceipts = 32;
        public const int MaxDepots = 8, MaxRoutes = 16, MaxRules = 16, MaxShipments = 32;

        public static byte[] Serialize(AcceptedState state)
        {
            Validate(state);
            if (state.SchemaVersion == 2) return SerializeV2(state);
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), true))
            {
                writer.Write(new byte[] { 69, 88, 83, 49 });
                I32(writer, 1); Text(writer, state.WorldId); Text(writer, state.CheckpointId);
                NullableText(writer, state.ParentCheckpointId); NullableText(writer, state.ParentBranchId);
                I64(writer, state.Revision); I64(writer, state.AcceptedSequence); I64(writer, state.CompactionWatermark); I64(writer, state.Counter);
                WriteDepots(writer, state.Depots); WriteSimple(writer, state.Routes, x => x.RouteId, x => x.Revision);
                WriteSimple(writer, state.Rules, x => x.RuleId, x => x.Revision); WriteSimple(writer, state.Shipments, x => x.ShipmentId, x => x.Revision);
                var receipts = (AcceptedReceipt[])state.Receipts.Clone(); Array.Sort(receipts, (a, b) => a.CommandSequence.CompareTo(b.CommandSequence));
                I32(writer, receipts.Length);
                foreach (var receipt in receipts) { Text(writer, receipt.WorldId); I64(writer, receipt.CommandSequence); Text(writer, receipt.OperationId); Text(writer, receipt.PayloadHash); Text(writer, receipt.Outcome); Text(writer, receipt.OperationKind); I64(writer, receipt.CounterDelta); I64(writer, receipt.TargetCompactionWatermark); F64(writer, receipt.AppliedUt); }
                writer.Flush();
                if (stream.Length > MaxDecodedBytes) throw new InvalidDataException("Accepted state exceeds 36 KiB decoded limit.");
                return stream.ToArray();
            }
        }

        public static AcceptedState Deserialize(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 4 || bytes.Length > MaxDecodedBytes) throw new InvalidDataException("Invalid accepted-state length.");
            if (bytes[0] == 69 && bytes[1] == 88 && bytes[2] == 83 && (bytes[3] == 50 || bytes[3] == 51 || bytes[3] == 52)) return DeserializeV2(bytes);
            using (var stream = new MemoryStream(bytes, false)) using (var reader = new BinaryReader(stream, new UTF8Encoding(false, true), true))
            {
                var magic = reader.ReadBytes(4); if (magic[0] != 69 || magic[1] != 88 || magic[2] != 83 || magic[3] != 49) throw new InvalidDataException("Unknown accepted-state encoding.");
                if (ReadI32(reader) != 1) throw new InvalidDataException("Unsupported accepted-state schema.");
                var state = new AcceptedState { CapsuleEncodingVersion = 1, WorldId = ReadText(reader), CheckpointId = ReadText(reader), ParentCheckpointId = ReadNullableText(reader), ParentBranchId = ReadNullableText(reader),
                    Revision = ReadI64(reader), AcceptedSequence = ReadI64(reader), CompactionWatermark = ReadI64(reader), Counter = ReadI64(reader),
                    Depots = ReadDepots(reader), Routes = ReadSimple(reader, x => new RouteRecord { RouteId = x.Id, Revision = x.Revision }),
                    Rules = ReadSimple(reader, x => new RuleRecord { RuleId = x.Id, Revision = x.Revision }), Shipments = ReadSimple(reader, x => new ShipmentRecord { ShipmentId = x.Id, Revision = x.Revision }) };
                var count = ReadCount(reader, MaxReceipts); var receipts = new AcceptedReceipt[count];
                for (var i = 0; i < count; i++) receipts[i] = new AcceptedReceipt { WorldId = ReadText(reader), CommandSequence = ReadI64(reader), OperationId = ReadText(reader), PayloadHash = ReadText(reader), Outcome = ReadText(reader), OperationKind = ReadText(reader), CounterDelta = ReadI64(reader), TargetCompactionWatermark = ReadI64(reader), AppliedUt = ReadF64(reader) };
                state.Receipts = receipts;
                if (stream.Position != stream.Length) throw new InvalidDataException("Trailing bytes in accepted state.");
                Validate(state); return state;
            }
        }

        static byte[] SerializeV2(AcceptedState state)
        {
            var hasPhysicalWitness = state.Receipts.Any(x => x?.PhysicalWitness is not null);
            var hasEconomics = state.RouteVersions.Any(x => x.DestinationKind != "physicalDepot" || x.FundsPerUnit != 0) || state.ActiveShipments.Any(x => x.DestinationKind != "physicalDepot" || x.FundsPerUnit != 0) || state.Receipts.Any(x => x.FundsWitness != null) || state.EconomicFaults.Length > 0;
            var encodingVersion = hasEconomics || state.CapsuleEncodingVersion >= 4 ? 4 : hasPhysicalWitness || state.CapsuleEncodingVersion >= 3 ? 3 : 2;
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), true))
            {
                writer.Write(new byte[] { 69, 88, 83, (byte)(48 + encodingVersion) });
                I32(writer, encodingVersion); Text(writer, state.WorldId); Text(writer, state.CheckpointId);
                NullableText(writer, state.ParentCheckpointId); NullableText(writer, state.ParentBranchId);
                I64(writer, state.Revision); I64(writer, state.AcceptedSequence); I64(writer, state.CompactionWatermark); I64(writer, state.Counter);
                Text(writer, state.DepotRegistryVersion); Text(writer, state.DepotRegistryHash); WriteDepotsV2(writer, state.Depots);
                WriteRouteVersions(writer, state.RouteVersions);
                WriteDeliveryRules(writer, state.DeliveryRules);
                WriteActiveShipments(writer, state.ActiveShipments);
                WriteReceipts(writer, state.Receipts, encodingVersion >= 3);
                WriteFaults(writer, state.Faults);
                if (encodingVersion >= 4) WriteEconomics(writer, state);
                writer.Flush();
                if (stream.Length > MaxDecodedBytes) throw new InvalidDataException("Accepted state exceeds 36 KiB decoded limit.");
                return stream.ToArray();
            }
        }

        static AcceptedState DeserializeV2(byte[] bytes)
        {
            using (var stream = new MemoryStream(bytes, false)) using (var reader = new BinaryReader(stream, new UTF8Encoding(false, true), true))
            {
                var magic = reader.ReadBytes(4);
                var wireVersion = ReadI32(reader);
                var isV4 = magic[0] == 69 && magic[1] == 88 && magic[2] == 83 && magic[3] == 52 && wireVersion == 4;
                var isV3 = magic[0] == 69 && magic[1] == 88 && magic[2] == 83 && magic[3] == 51 && wireVersion == 3;
                if (!(magic[0] == 69 && magic[1] == 88 && magic[2] == 83 && magic[3] == 50 && wireVersion == 2) && !isV3 && !isV4) throw new InvalidDataException("Unsupported v2 accepted-state encoding.");
                var state = new AcceptedState
                {
                    SchemaVersion = 2, CapsuleEncodingVersion = isV4 ? 4 : isV3 ? 3 : 2, WorldId = ReadText(reader), CheckpointId = ReadText(reader),
                    ParentCheckpointId = ReadNullableText(reader), ParentBranchId = ReadNullableText(reader),
                    Revision = ReadI64(reader), AcceptedSequence = ReadI64(reader), CompactionWatermark = ReadI64(reader), Counter = ReadI64(reader),
                    DepotRegistryVersion = ReadText(reader), DepotRegistryHash = ReadText(reader), Depots = ReadDepotsV2(reader), RouteVersions = ReadRouteVersions(reader), DeliveryRules = ReadDeliveryRules(reader),
                    ActiveShipments = ReadActiveShipments(reader), Receipts = ReadReceipts(reader, isV3 || isV4), Faults = ReadFaults(reader)
                };
                if (isV4) ReadEconomics(reader, state);
                if (stream.Position != stream.Length) throw new InvalidDataException("Trailing bytes in accepted-state v2.");
                Validate(state); return state;
            }
        }

        static void WriteRouteVersions(BinaryWriter w, RouteVersionRecord[] xs)
        {
            var a = xs.OrderBy(x => x.RouteId, StringComparer.Ordinal).ThenBy(x => x.Version).ToArray(); I32(w, a.Length);
            foreach (var x in a) { Text(w, x.RouteId); I64(w, x.Revision); I64(w, x.Version); Text(w, x.SourceDepotId); I64(w, x.SourceMembershipRevision); Text(w, x.SourceMembershipHash); Text(w, x.DestinationDepotId); I64(w, x.DestinationMembershipRevision); Text(w, x.DestinationMembershipHash); F64(w, x.TravelDurationSeconds); Text(w, x.Provenance); Bool(w, x.LegacyOpaque); WriteResources(w, x.Resources); }
        }
        static RouteVersionRecord[] ReadRouteVersions(BinaryReader r)
        {
            var a = new RouteVersionRecord[ReadCount(r, AcceptedStateV2Limits.MaxRouteVersions)];
            for (var i = 0; i < a.Length; i++) a[i] = new RouteVersionRecord { RouteId = ReadText(r), Revision = ReadI64(r), Version = ReadI64(r), SourceDepotId = ReadText(r), SourceMembershipRevision = ReadI64(r), SourceMembershipHash = ReadText(r), DestinationDepotId = ReadText(r), DestinationMembershipRevision = ReadI64(r), DestinationMembershipHash = ReadText(r), TravelDurationSeconds = ReadF64(r), Provenance = ReadText(r), LegacyOpaque = ReadBool(r), Resources = ReadResources(r) };
            return a;
        }
        static void WriteDeliveryRules(BinaryWriter w, DeliveryRuleRecord[] xs)
        {
            var a = xs.OrderBy(x => x.RuleId, StringComparer.Ordinal).ToArray(); I32(w, a.Length);
            foreach (var x in a) { Text(w, x.RuleId); I64(w, x.Revision); Text(w, x.Kind); Text(w, x.RouteId); I64(w, x.RouteVersion); Bool(w, x.Enabled); F64(w, x.NextDueUt); F64(w, x.IntervalSeconds); Bool(w, x.WaitingRequest); F64(w, x.WaitingScheduledUt); I64(w, x.WaitingCoalescedSlots); Text(w, x.ResourceName); I64(w, x.LowTriggerMicroUnits); I64(w, x.TargetMicroUnits); I64(w, x.BatchSizeMicroUnits); Bool(w, x.LegacyOpaque); }
        }
        static DeliveryRuleRecord[] ReadDeliveryRules(BinaryReader r)
        {
            var a = new DeliveryRuleRecord[ReadCount(r, AcceptedStateV2Limits.MaxRules)];
            for (var i = 0; i < a.Length; i++) a[i] = new DeliveryRuleRecord { RuleId = ReadText(r), Revision = ReadI64(r), Kind = ReadText(r), RouteId = ReadText(r), RouteVersion = ReadI64(r), Enabled = ReadBool(r), NextDueUt = ReadF64(r), IntervalSeconds = ReadF64(r), WaitingRequest = ReadBool(r), WaitingScheduledUt = ReadF64(r), WaitingCoalescedSlots = ReadI64(r), ResourceName = ReadText(r), LowTriggerMicroUnits = ReadI64(r), TargetMicroUnits = ReadI64(r), BatchSizeMicroUnits = ReadI64(r), LegacyOpaque = ReadBool(r) };
            return a;
        }
        static void WriteActiveShipments(BinaryWriter w, ActiveShipmentRecord[] xs)
        {
            var a = xs.OrderBy(x => x.ShipmentId, StringComparer.Ordinal).ToArray(); I32(w, a.Length);
            foreach (var x in a) { Text(w, x.ShipmentId); I64(w, x.Revision); Text(w, x.RouteId); I64(w, x.RouteVersion); Text(w, x.SourceDepotId); Text(w, x.DestinationDepotId); F64(w, x.DepartureUt); F64(w, x.DueUt); WriteResources(w, x.RemainingResources); Text(w, x.HeldReason); Bool(w, x.LegacyOpaque); }
        }
        static ActiveShipmentRecord[] ReadActiveShipments(BinaryReader r)
        {
            var a = new ActiveShipmentRecord[ReadCount(r, AcceptedStateV2Limits.MaxActiveShipments)];
            for (var i = 0; i < a.Length; i++) a[i] = new ActiveShipmentRecord { ShipmentId = ReadText(r), Revision = ReadI64(r), RouteId = ReadText(r), RouteVersion = ReadI64(r), SourceDepotId = ReadText(r), DestinationDepotId = ReadText(r), DepartureUt = ReadF64(r), DueUt = ReadF64(r), RemainingResources = ReadResources(r), HeldReason = ReadText(r), LegacyOpaque = ReadBool(r) };
            return a;
        }
        static void WriteReceipts(BinaryWriter w, AcceptedReceipt[] xs, bool hasWitnessExtension)
        {
            var a = (AcceptedReceipt[])xs.Clone(); Array.Sort(a, (x, y) => x.CommandSequence.CompareTo(y.CommandSequence)); I32(w, a.Length);
            foreach (var x in a)
            {
                Text(w, x.WorldId); I64(w, x.CommandSequence); Text(w, x.OperationId); Text(w, x.PayloadHash); Text(w, x.Outcome); Text(w, x.OperationKind); I64(w, x.CounterDelta); I64(w, x.TargetCompactionWatermark); F64(w, x.AppliedUt);
                if (hasWitnessExtension) { Bool(w, x.PhysicalWitness != null); if (x.PhysicalWitness != null) WritePhysicalWitness(w, x.PhysicalWitness); }
            }
        }
        static AcceptedReceipt[] ReadReceipts(BinaryReader r, bool hasWitnessExtension)
        {
            var a = new AcceptedReceipt[ReadCount(r, MaxReceipts)];
            for (var i = 0; i < a.Length; i++)
            {
                var receipt = new AcceptedReceipt { WorldId = ReadText(r), CommandSequence = ReadI64(r), OperationId = ReadText(r), PayloadHash = ReadText(r), Outcome = ReadText(r), OperationKind = ReadText(r), CounterDelta = ReadI64(r), TargetCompactionWatermark = ReadI64(r), AppliedUt = ReadF64(r) };
                if (hasWitnessExtension && ReadBool(r)) receipt.PhysicalWitness = ReadPhysicalWitness(r);
                a[i] = receipt;
            }
            return a;
        }
        static void WritePhysicalWitness(BinaryWriter w, PhysicalSuccessWitness witness)
        {
            Text(w, witness.ProviderId); Text(w, witness.ProviderVersion);
            var rows = witness.Rows.OrderBy(x => x.MemberPersistentId).ThenBy(x => x.ResourceName, StringComparer.Ordinal).ToArray(); I32(w, rows.Length);
            foreach (var row in rows) { U32(w, row.MemberPersistentId); Text(w, row.ResourceName); F64(w, row.BeforeAmount); F64(w, row.IntendedAfterAmount); F64(w, row.ObservedAfterAmount); }
        }
        static PhysicalSuccessWitness ReadPhysicalWitness(BinaryReader r)
        {
            var witness = new PhysicalSuccessWitness { ProviderId = ReadText(r), ProviderVersion = ReadText(r) };
            witness.Rows = new PhysicalSuccessWitnessRow[ReadCount(r, AcceptedStateV2Limits.MaxFaultResourceDeltas)];
            for (var i = 0; i < witness.Rows.Length; i++) witness.Rows[i] = new PhysicalSuccessWitnessRow { MemberPersistentId = ReadU32(r), ResourceName = ReadText(r), BeforeAmount = ReadF64(r), IntendedAfterAmount = ReadF64(r), ObservedAfterAmount = ReadF64(r) };
            return witness;
        }
        static void WriteFaults(BinaryWriter w, PhysicalFaultRecord[] xs)
        {
            var a = xs.OrderBy(x => x.CommandSequence).ThenBy(x => x.FaultId, StringComparer.Ordinal).ToArray(); I32(w, a.Length);
            foreach (var x in a)
            {
                Text(w, x.FaultId); Text(w, x.OperationId); Text(w, x.OperationKind); I64(w, x.CommandSequence); Text(w, x.DepotId); I64(w, x.MembershipRevision); Text(w, x.MembershipHash); Text(w, x.Reason); Text(w, x.RollbackStatus);
                var deltas = x.Deltas.OrderBy(d => d.MemberPersistentId).ThenBy(d => d.ResourceName, StringComparer.Ordinal).ToArray(); I32(w, deltas.Length);
                foreach (var d in deltas) { U32(w, d.MemberPersistentId); Text(w, d.ResourceName); F64(w, d.BeforeAmount); I64(w, d.IntendedDeltaMicroUnits); F64(w, d.IntendedAfterAmount); Bool(w, d.ObservedAfterKnown); F64(w, d.ObservedAfterAmount); Bool(w, d.RollbackObserved); F64(w, d.RollbackObservedAmount); }
            }
        }
        static PhysicalFaultRecord[] ReadFaults(BinaryReader r)
        {
            var a = new PhysicalFaultRecord[ReadCount(r, AcceptedStateV2Limits.MaxFaultRecords)];
            for (var i = 0; i < a.Length; i++)
            {
                var x = new PhysicalFaultRecord { FaultId = ReadText(r), OperationId = ReadText(r), OperationKind = ReadText(r), CommandSequence = ReadI64(r), DepotId = ReadText(r), MembershipRevision = ReadI64(r), MembershipHash = ReadText(r), Reason = ReadText(r), RollbackStatus = ReadText(r) };
                x.Deltas = new PhysicalResourceDelta[ReadCount(r, AcceptedStateV2Limits.MaxFaultResourceDeltas)];
                for (var j = 0; j < x.Deltas.Length; j++) x.Deltas[j] = new PhysicalResourceDelta { MemberPersistentId = ReadU32(r), ResourceName = ReadText(r), BeforeAmount = ReadF64(r), IntendedDeltaMicroUnits = ReadI64(r), IntendedAfterAmount = ReadF64(r), ObservedAfterKnown = ReadBool(r), ObservedAfterAmount = ReadF64(r), RollbackObserved = ReadBool(r), RollbackObservedAmount = ReadF64(r) };
                a[i] = x;
            }
            return a;
        }
        static void WriteResources(BinaryWriter w, ResourceAmount[] xs) { var a = xs.OrderBy(x => x.ResourceName, StringComparer.Ordinal).ToArray(); I32(w, a.Length); foreach (var x in a) { Text(w, x.ResourceName); I64(w, x.AmountMicroUnits); } }
        static ResourceAmount[] ReadResources(BinaryReader r) { var a = new ResourceAmount[ReadCount(r, AcceptedStateV2Limits.MaxManifestResources)]; for (var i = 0; i < a.Length; i++) a[i] = new ResourceAmount { ResourceName = ReadText(r), AmountMicroUnits = ReadI64(r) }; return a; }

        public static string ComputeHash(AcceptedState state)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Serialize(state)); var b = new StringBuilder(hash.Length * 2);
                foreach (var v in hash) b.Append(v.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
                return b.ToString();
            }
        }

        public static RecoveryCapsule CreateCapsule(AcceptedState state)
        {
            var bytes = Serialize(state); var capsule = new RecoveryCapsule { WorldId = state.WorldId, StateBytesBase64 = Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_'), StateSha256 = ComputeHash(state) };
            if (Encoding.UTF8.GetByteCount(capsule.StateBytesBase64) > MaxEncodedCapsuleBytes) throw new InvalidDataException("Encoded capsule exceeds 48 KiB.");
            return capsule;
        }

        // KSP ConfigNode treats // as a comment marker even inside a value.
        // URL-safe Base64 avoids that sequence; legacy standard Base64 remains
        // readable so existing saves retain their canonical decoded-byte hash.
        public static byte[] DecodeCapsuleBytes(string encoded)
        {
            if (String.IsNullOrWhiteSpace(encoded) || Encoding.UTF8.GetByteCount(encoded) > MaxEncodedCapsuleBytes)
                throw new InvalidDataException("Encoded capsule is missing or exceeds 48 KiB.");
            try { return Convert.FromBase64String(encoded.Replace('-', '+').Replace('_', '/')); }
            catch (FormatException ex) { throw new InvalidDataException("Capsule Base64 is invalid.", ex); }
        }

        public static AcceptedState ReadCapsule(RecoveryCapsule capsule)
        {
            if (capsule == null || capsule.SchemaVersion != 1 || string.IsNullOrWhiteSpace(capsule.WorldId) || string.IsNullOrWhiteSpace(capsule.StateBytesBase64)) throw new InvalidDataException("Invalid recovery capsule.");
            if (Encoding.UTF8.GetByteCount(capsule.StateBytesBase64) > MaxEncodedCapsuleBytes) throw new InvalidDataException("Encoded capsule exceeds 48 KiB.");
            byte[] bytes = DecodeCapsuleBytes(capsule.StateBytesBase64);
            var state = Deserialize(bytes); var actual = ComputeHash(state);
            if (!String.Equals(state.WorldId, capsule.WorldId, StringComparison.Ordinal) || !FixedEquals(actual, capsule.StateSha256)) throw new InvalidDataException("Capsule identity or state hash mismatch.");
            return state;
        }

        public static void Validate(AcceptedState s)
        {
            if (s == null || (s.SchemaVersion != 1 && s.SchemaVersion != 2) || (s.SchemaVersion == 2 && s.CapsuleEncodingVersion is not (2 or 3 or 4)) || String.IsNullOrWhiteSpace(s.WorldId) || String.IsNullOrWhiteSpace(s.CheckpointId)) throw new InvalidDataException("Missing identity or unsupported schema.");
            if (s.Revision < 0 || s.AcceptedSequence < 0 || s.CompactionWatermark < 0 || s.CompactionWatermark > s.AcceptedSequence || s.Counter < 0) throw new InvalidDataException("Negative or inconsistent accepted prefix.");
            if (s.Depots == null || s.Routes == null || s.Rules == null || s.Shipments == null || s.Receipts == null || s.Depots.Length > (s.SchemaVersion == 2 ? AcceptedStateV2Limits.MaxDepotRecords : MaxDepots) || s.Routes.Length > MaxRoutes || s.Rules.Length > MaxRules || s.Shipments.Length > MaxShipments || s.Receipts.Length > MaxReceipts) throw new InvalidDataException("Accepted state exceeds collection bounds.");
            var sequences = new HashSet<long>(); var operations = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in s.Receipts)
            {
                var allowedKind = r != null && (r.OperationKind == "counterIncrement" || r.OperationKind == "compact" || (s.SchemaVersion == 2 && (r.OperationKind == "syncDepots" || r.OperationKind == "routeUpsert" || r.OperationKind == "ruleUpsert" || r.OperationKind == "rulePause" || r.OperationKind == "ruleCancel" || r.OperationKind == "dispatch" || r.OperationKind == "arrival" || r.OperationKind == "physicalEffectFault" || r.OperationKind == "recoverySale")));
                var allowedOutcome = r != null && (r.Outcome == "accepted" || (s.SchemaVersion == 2 && (r.OperationKind == "dispatch" || r.OperationKind == "arrival" || r.OperationKind == "recoverySale") && r.Outcome == "faulted"));
                var invalidPayload = r == null || !allowedKind || (r.OperationKind == "counterIncrement" ? r.CounterDelta <= 0 || r.TargetCompactionWatermark != 0 : r.OperationKind == "compact" ? r.CounterDelta != 0 || r.TargetCompactionWatermark < 0 || r.TargetCompactionWatermark > s.CompactionWatermark || r.TargetCompactionWatermark >= r.CommandSequence : r.CounterDelta != 0 || r.TargetCompactionWatermark != 0);
                if (r == null || r.WorldId != s.WorldId || r.CommandSequence <= s.CompactionWatermark || r.CommandSequence > s.AcceptedSequence || String.IsNullOrWhiteSpace(r.OperationId) || String.IsNullOrWhiteSpace(r.PayloadHash) || !allowedOutcome || Double.IsNaN(r.AppliedUt) || Double.IsInfinity(r.AppliedUt) || r.AppliedUt < 0 || invalidPayload || !sequences.Add(r.CommandSequence) || !operations.Add(r.OperationId)) throw new InvalidDataException("Invalid or conflicting receipt.");
                if (r.PhysicalWitness is not null) ValidatePhysicalSuccessWitness(r, s.SchemaVersion);
                if (r.FundsWitness != null) { if (s.SchemaVersion != 2 || r.OperationKind != "recoverySale" || r.Outcome != "accepted") throw new InvalidDataException("Funds witness has wrong receipt kind."); ValidateFundsWitness(r.FundsWitness); }
                if (r.OperationKind == "recoverySale" && r.Outcome == "accepted" && r.FundsWitness == null) throw new InvalidDataException("Recovery sale requires funds readback witness.");
            }
            if (s.SchemaVersion == 1)
            {
                Unique(s.Depots, x => x.DepotId);
                if (s.RouteVersions == null || s.RouteVersions.Length != 0 || s.DeliveryRules == null || s.DeliveryRules.Length != 0 || s.ActiveShipments == null || s.ActiveShipments.Length != 0 || s.Faults == null || s.Faults.Length != 0 || s.EconomicFaults == null || s.EconomicFaults.Length != 0) throw new InvalidDataException("EXS1 cannot contain unencoded EXS2 collections.");
                Unique(s.Routes, x => x.RouteId); Unique(s.Rules, x => x.RuleId); Unique(s.Shipments, x => x.ShipmentId);
            }
            else
            {
                if (s.DepotRegistryVersion == null || s.DepotRegistryVersion.Length > 128 || s.DepotRegistryHash == null || s.DepotRegistryHash.Length > 64) throw new InvalidDataException("EXS2 registry metadata exceeds bounds.");
                var depotKeys = new HashSet<string>(StringComparer.Ordinal); var activeIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var depot in s.Depots) if (depot == null || String.IsNullOrWhiteSpace(depot.DepotId) || depot.DepotId.Length > 128 || depot.MembershipRevision < 0 || depot.MembershipHash == null || (depot.MembershipHash.Length != 0 && !IsSha256(depot.MembershipHash)) || !depotKeys.Add(depot.DepotId + "\0" + depot.MembershipRevision.ToString(CultureInfo.InvariantCulture) + "\0" + depot.MembershipHash) || (depot.Active && !activeIds.Add(depot.DepotId))) throw new InvalidDataException("EXS2 depot identity/revision is invalid.");
                var active = s.Depots.Where(d => d.Active).OrderBy(d => d.DepotId, StringComparer.Ordinal).ToArray();
                if (active.Length > MaxDepots || (active.Length == 0 && s.DepotRegistryVersion.Length == 0 && s.DepotRegistryHash.Length == 0) == false && (String.IsNullOrEmpty(s.DepotRegistryVersion) || OperationIdentity.ComputeDepotRegistryHash(active) != s.DepotRegistryHash)) throw new InvalidDataException("EXS2 active depot mirror does not match its registry fingerprint.");
                if (s.Routes.Length != 0 || s.Rules.Length != 0 || s.Shipments.Length != 0) throw new InvalidDataException("EXS2 must use its versioned route/rule/shipment collections.");
                ValidateEconomicFaults(s);
                AcceptedStateV2Draft.ValidateCollections(s, s.RouteVersions, s.DeliveryRules, s.ActiveShipments, s.Faults);
                foreach (var fault in s.Faults)
                {
                    var receipt = Array.Find(s.Receipts, x => x.OperationId == fault.OperationId && x.CommandSequence == fault.CommandSequence);
                    if (receipt == null || receipt.Outcome != "faulted" || receipt.OperationKind != fault.OperationKind) throw new InvalidDataException("Every physical fault must have its terminal faulted receipt.");
                }
                foreach (var receipt in s.Receipts) if (receipt.Outcome == "faulted" && !s.Faults.Any(x => x.OperationId == receipt.OperationId && x.CommandSequence == receipt.CommandSequence) && !s.EconomicFaults.Any(x => x.OperationId == receipt.OperationId && x.CommandSequence == receipt.CommandSequence)) throw new InvalidDataException("Fault receipt has no matching physical fault evidence.");
            }
        }

        static void ValidatePhysicalSuccessWitness(AcceptedReceipt receipt, int schemaVersion)
        {
            var witness = receipt.PhysicalWitness!;
            if (schemaVersion != 2 || receipt.Outcome != "accepted" || (receipt.OperationKind != "dispatch" && receipt.OperationKind != "arrival") || String.IsNullOrWhiteSpace(witness.ProviderId) || witness.ProviderId.Length > 128 || String.IsNullOrWhiteSpace(witness.ProviderVersion) || witness.ProviderVersion.Length > 128 || witness.Rows == null || witness.Rows.Length == 0 || witness.Rows.Length > AcceptedStateV2Limits.MaxFaultResourceDeltas)
                throw new InvalidDataException("Physical success witness identity or bounds are invalid.");
            PhysicalSuccessWitnessRow? previous = null;
            foreach (var row in witness.Rows)
            {
                if (row == null || row.MemberPersistentId == 0 || String.IsNullOrWhiteSpace(row.ResourceName) || row.ResourceName.Length > 128 || !FiniteNonNegative(row.BeforeAmount) || !FiniteNonNegative(row.IntendedAfterAmount) || !FiniteNonNegative(row.ObservedAfterAmount))
                    throw new InvalidDataException("Physical success witness contains an invalid bounded readback row.");
                if (previous is not null && (previous.MemberPersistentId > row.MemberPersistentId || previous.MemberPersistentId == row.MemberPersistentId && StringComparer.Ordinal.Compare(previous.ResourceName, row.ResourceName) >= 0)) throw new InvalidDataException("Physical success witness rows are not in unique canonical order.");
                previous = row;
            }
        }

        public static StateTransitionResult Increment(AcceptedState prior, string worldId, string operationId, string clientRequestId, long sequence, string payloadHash, long delta, double appliedUt)
        {
            Validate(prior);
            if (worldId != prior.WorldId || sequence <= 0 || delta <= 0 || String.IsNullOrWhiteSpace(clientRequestId) || OperationIdentity.Create(worldId, sequence, clientRequestId) != operationId || OperationIdentity.CounterIncrementPayloadHash(delta) != payloadHash)
                return new StateTransitionResult { State = prior, Outcome = "rejected", Reason = "Operation identity or payload hash is invalid." };
            foreach (var receipt in prior.Receipts) if (receipt.OperationId == operationId || receipt.CommandSequence == sequence)
                return new StateTransitionResult { State = prior, Outcome = receipt.OperationId == operationId && receipt.CommandSequence == sequence && receipt.PayloadHash == payloadHash ? "duplicate" : "rejected", Reason = "Operation or sequence already has a different accepted receipt." };
            if (sequence <= prior.CompactionWatermark) return new StateTransitionResult { State = prior, Outcome = "alreadySettledCompacted", Reason = "Command sequence is at or below the compaction watermark." };
            if (sequence != prior.AcceptedSequence + 1 || delta <= 0 || prior.Counter > Int64.MaxValue - delta || prior.Revision == Int64.MaxValue || Double.IsNaN(appliedUt) || Double.IsInfinity(appliedUt) || appliedUt < 0) return new StateTransitionResult { State = prior, Outcome = "rejected", Reason = "Sequence, delta, or counter is invalid." };
            if (prior.Receipts.Length >= MaxReceipts) return new StateTransitionResult { State = prior, Outcome = "held", Reason = "Receipt capacity reached; verified compaction is required." };
            var next = Clone(prior); next.Counter += delta; next.Revision++; next.AcceptedSequence = sequence;
            var receipts = new AcceptedReceipt[prior.Receipts.Length + 1]; Array.Copy(prior.Receipts, receipts, prior.Receipts.Length);
            receipts[receipts.Length - 1] = new AcceptedReceipt { WorldId = worldId, CommandSequence = sequence, OperationId = operationId, PayloadHash = payloadHash, Outcome = "accepted", OperationKind = "counterIncrement", CounterDelta = delta, AppliedUt = appliedUt };
            next.Receipts = receipts; Validate(next); return new StateTransitionResult { State = next, Outcome = "accepted" };
        }

        /// <summary>Promotes an EXS1 projection only as part of a caller-validated accepted v2 transition.</summary>
        public static AcceptedState MigrateToV2ForAcceptedMutation(AcceptedState prior)
        {
            Validate(prior);
            if (prior.SchemaVersion == 2) return Clone(prior);
            var next = Clone(prior); next.SchemaVersion = 2;
            next.CapsuleEncodingVersion = 2;
            next.DepotRegistryVersion = ""; next.DepotRegistryHash = "";
            next.Depots = prior.Depots.Select(x => new DepotRecord { DepotId = x.DepotId, MembershipRevision = x.MembershipRevision, MembershipHash = "", Active = false }).ToArray();
            next.RouteVersions = prior.Routes.Select(x => new RouteVersionRecord { RouteId = x.RouteId, Revision = x.Revision, Version = 0, LegacyOpaque = true }).ToArray();
            next.DeliveryRules = prior.Rules.Select(x => new DeliveryRuleRecord { RuleId = x.RuleId, Revision = x.Revision, LegacyOpaque = true }).ToArray();
            next.ActiveShipments = prior.Shipments.Select(x => new ActiveShipmentRecord { ShipmentId = x.ShipmentId, Revision = x.Revision, LegacyOpaque = true }).ToArray();
            next.Routes = Array.Empty<RouteRecord>(); next.Rules = Array.Empty<RuleRecord>(); next.Shipments = Array.Empty<ShipmentRecord>();
            Validate(next); Serialize(next); return next;
        }

        public static StateTransitionResult SyncDepots(AcceptedState prior, string operationId, string clientRequestId, long sequence, string payloadHash, long expectedRevision, string expectedHash, DepotRegistrySnapshot snapshot, double appliedUt)
        {
            Validate(prior);
            if (prior.WritesBlocked) return new StateTransitionResult { State = prior, Outcome = "held", Reason = "Physical fault blocks writes." };
            if (!ExactExpectedPrefix(prior, expectedRevision, expectedHash, sequence)) return new StateTransitionResult { State = prior, Outcome = "rejected", Reason = "Expected accepted prefix does not match." };
            string canonicalHash;
            try { canonicalHash = OperationIdentity.ComputeDepotRegistryHash(snapshot?.Depots ?? throw new ArgumentNullException(nameof(snapshot))); }
            catch (ArgumentException ex) { return new StateTransitionResult { State = prior, Outcome = "rejected", Reason = ex.Message }; }
            if (String.IsNullOrWhiteSpace(snapshot.RegistryVersion) || snapshot.RegistryVersion.Length > 128 || snapshot.RegistryHash != canonicalHash || OperationIdentity.DepotRegistryPayloadHash(snapshot) != payloadHash || OperationIdentity.Create(prior.WorldId, sequence, clientRequestId) != operationId || !FiniteNonNegative(appliedUt)) return new StateTransitionResult { State = prior, Outcome = "rejected", Reason = "Registry payload identity or fingerprint is invalid." };
            if (prior.Receipts.Length >= MaxReceipts) return new StateTransitionResult { State = prior, Outcome = "held", Reason = "Receipt capacity reached; verified compaction is required." };
            var next = MigrateToV2ForAcceptedMutation(prior);
            if (next.DepotRegistryVersion == snapshot.RegistryVersion && next.DepotRegistryHash == snapshot.RegistryHash) return new StateTransitionResult { State = prior, Outcome = "duplicate", Reason = "Registry mirror is already synchronized." };
            var rows = next.Depots.Select(d => new DepotRecord { DepotId = d.DepotId, MembershipRevision = d.MembershipRevision, MembershipHash = d.MembershipHash, Active = false }).ToList();
            foreach (var incoming in snapshot.Depots)
            {
                rows.RemoveAll(d => d.DepotId == incoming.DepotId && d.MembershipRevision == incoming.MembershipRevision && d.MembershipHash == incoming.MembershipHash);
                rows.Add(new DepotRecord { DepotId = incoming.DepotId, MembershipRevision = incoming.MembershipRevision, MembershipHash = incoming.MembershipHash, Active = true });
            }
            if (rows.Count > AcceptedStateV2Limits.MaxDepotRecords) return new StateTransitionResult { State = prior, Outcome = "held", Reason = "Depot tombstone capacity exhausted; retained route/cargo history cannot be discarded." };
            next.Depots = rows.ToArray(); next.DepotRegistryVersion = snapshot.RegistryVersion; next.DepotRegistryHash = snapshot.RegistryHash;
            return AcceptV2Mutation(prior, next, operationId, clientRequestId, sequence, payloadHash, "syncDepots", 0, 0, appliedUt);
        }

        public static StateTransitionResult UpsertRoute(AcceptedState prior, string operationId, string clientRequestId, long sequence, string payloadHash, long expectedRevision, string expectedHash, RouteVersionRecord route, double appliedUt)
        {
            Validate(prior);
            var kind = "routeUpsert";
            if (prior.WritesBlocked) return new StateTransitionResult { State = prior, Outcome = "held", Reason = "Physical fault blocks writes." };
            if (!ExactExpectedPrefix(prior, expectedRevision, expectedHash, sequence)) return new StateTransitionResult { State = prior, Outcome = "rejected", Reason = "Expected accepted prefix does not match." };
            if (route == null || route.LegacyOpaque || OperationIdentity.Create(prior.WorldId, sequence, clientRequestId) != operationId || OperationIdentity.RouteVersionPayloadHash(route) != payloadHash || !FiniteNonNegative(appliedUt)) return new StateTransitionResult { State = prior, Outcome = "rejected", Reason = "Route operation identity, payload hash or UT is invalid." };
            if (prior.Receipts.Length >= MaxReceipts) return new StateTransitionResult { State = prior, Outcome = "held", Reason = "Receipt capacity reached; verified compaction is required." };
            var next = MigrateToV2ForAcceptedMutation(prior);
            var priorVersions = next.RouteVersions.Where(x => !x.LegacyOpaque && x.RouteId == route.RouteId).ToArray();
            var expectedVersion = priorVersions.Length == 0 ? 1 : priorVersions.Max(x => x.Version) + 1;
            if (!next.Depots.Any(d => d.Active && d.DepotId == route.SourceDepotId && d.MembershipRevision == route.SourceMembershipRevision && d.MembershipHash == route.SourceMembershipHash) || (!OreExportPolicy.IsVirtual(route) && !next.Depots.Any(d => d.Active && d.DepotId == route.DestinationDepotId && d.MembershipRevision == route.DestinationMembershipRevision && d.MembershipHash == route.DestinationMembershipHash))) return new StateTransitionResult { State = prior, Outcome = "held", Reason = "Route endpoints do not match the active synchronized depot registry." };
            if (route.Version != expectedVersion || next.RouteVersions.Length >= AcceptedStateV2Limits.MaxRouteVersions) return new StateTransitionResult { State = prior, Outcome = "held", Reason = "Route version must append contiguously and fit the bounded route history." };
            var appended = (RouteVersionRecord[])next.RouteVersions.Clone(); var routeCopy = CloneRoute(route); routeCopy.Revision = expectedRevision + 1; appended = appended.Concat(new[] { routeCopy }).ToArray(); next.RouteVersions = appended;
            return AcceptV2Mutation(prior, next, operationId, clientRequestId, sequence, payloadHash, kind, 0, 0, appliedUt);
        }

        public static StateTransitionResult UpsertRule(AcceptedState prior, string operationId, string clientRequestId, long sequence, string payloadHash, long expectedRevision, string expectedHash, DeliveryRuleRecord rule, double appliedUt)
        {
            Validate(prior);
            const string kind = "ruleUpsert";
            if (prior.WritesBlocked) return new StateTransitionResult { State = prior, Outcome = "held", Reason = "Physical fault blocks writes." };
            if (!ExactExpectedPrefix(prior, expectedRevision, expectedHash, sequence)) return new StateTransitionResult { State = prior, Outcome = "rejected", Reason = "Expected accepted prefix does not match." };
            if (rule == null || rule.LegacyOpaque || OperationIdentity.Create(prior.WorldId, sequence, clientRequestId) != operationId || OperationIdentity.RulePayloadHash(rule) != payloadHash || !FiniteNonNegative(appliedUt)) return new StateTransitionResult { State = prior, Outcome = "rejected", Reason = "Rule operation identity, payload hash or UT is invalid." };
            if (prior.Receipts.Length >= MaxReceipts) return new StateTransitionResult { State = prior, Outcome = "held", Reason = "Receipt capacity reached; verified compaction is required." };
            var next = MigrateToV2ForAcceptedMutation(prior);
            var oldRule = next.DeliveryRules.SingleOrDefault(x => x.RuleId == rule.RuleId);
            if (oldRule is not null && oldRule.LegacyOpaque) return new StateTransitionResult { State = prior, Outcome = "rejected", Reason = "An opaque legacy rule cannot be edited." };
            if (rule.Revision != (oldRule is null ? 1 : oldRule.Revision + 1)) return new StateTransitionResult { State = prior, Outcome = "rejected", Reason = "Rule revision is stale." };
            if (!next.RouteVersions.Any(x => !x.LegacyOpaque && x.RouteId == rule.RouteId && x.Version == rule.RouteVersion)) return new StateTransitionResult { State = prior, Outcome = "rejected", Reason = "Rule must reference a complete immutable route version." };
            var ruleRoute = next.RouteVersions.Single(x => !x.LegacyOpaque && x.RouteId == rule.RouteId && x.Version == rule.RouteVersion);
            if (rule.Kind == "keepStock" && (ruleRoute.Resources.Length != 1 || ruleRoute.Resources[0].ResourceName != rule.ResourceName || ruleRoute.Resources[0].AmountMicroUnits != rule.BatchSizeMicroUnits)) return new StateTransitionResult { State = prior, Outcome = "rejected", Reason = "Keep-stock route must carry exactly its configured resource at the rule's batch-size cap." };
            var ruleCopy = CloneRule(rule);
            if (oldRule is not null && rule.Kind == "repeat" && oldRule.Kind == "repeat" && rule.IntervalSeconds == oldRule.IntervalSeconds)
            {
                ruleCopy.NextDueUt = oldRule.NextDueUt;
                ruleCopy.WaitingRequest = oldRule.WaitingRequest;
                ruleCopy.WaitingScheduledUt = oldRule.WaitingScheduledUt;
                ruleCopy.WaitingCoalescedSlots = oldRule.WaitingCoalescedSlots;
            }
            else if (oldRule is not null && rule.Kind == "repeat")
            {
                ruleCopy.WaitingRequest = false;
                ruleCopy.WaitingScheduledUt = 0;
                ruleCopy.WaitingCoalescedSlots = 0;
            }
            var rules = next.DeliveryRules.Where(x => x.RuleId != rule.RuleId).Select(CloneRule).ToList(); rules.Add(ruleCopy);
            if (rules.Count > AcceptedStateV2Limits.MaxRules) return new StateTransitionResult { State = prior, Outcome = "held", Reason = "Rule capacity reached." };
            next.DeliveryRules = rules.ToArray();
            return AcceptV2Mutation(prior, next, operationId, clientRequestId, sequence, payloadHash, kind, 0, 0, appliedUt);
        }

        public static StateTransitionResult CancelRule(AcceptedState prior, string operationId, string clientRequestId, long sequence, string payloadHash, long expectedRevision, string expectedHash, string ruleId, double appliedUt)
        {
            Validate(prior);
            const string kind = "ruleCancel";
            if (prior.WritesBlocked) return new StateTransitionResult { State = prior, Outcome = "held", Reason = "Physical fault blocks writes." };
            if (!ExactExpectedPrefix(prior, expectedRevision, expectedHash, sequence)) return new StateTransitionResult { State = prior, Outcome = "rejected", Reason = "Expected accepted prefix does not match." };
            if (String.IsNullOrWhiteSpace(ruleId) || ruleId.Length > 128 || OperationIdentity.Create(prior.WorldId, sequence, clientRequestId) != operationId || OperationIdentity.RuleCancelPayloadHash(ruleId) != payloadHash || !FiniteNonNegative(appliedUt)) return new StateTransitionResult { State = prior, Outcome = "rejected", Reason = "Rule cancellation identity, payload hash or UT is invalid." };
            if (prior.Receipts.Length >= MaxReceipts) return new StateTransitionResult { State = prior, Outcome = "held", Reason = "Receipt capacity reached; verified compaction is required." };
            var next = MigrateToV2ForAcceptedMutation(prior);
            if (!next.DeliveryRules.Any(x => x.RuleId == ruleId && !x.LegacyOpaque)) return new StateTransitionResult { State = prior, Outcome = "rejected", Reason = "The named automatic order does not exist." };
            next.DeliveryRules = next.DeliveryRules.Where(x => x.RuleId != ruleId).Select(CloneRule).ToArray();
            return AcceptV2Mutation(prior, next, operationId, clientRequestId, sequence, payloadHash, kind, 0, 0, appliedUt);
        }

        /// <summary>Atomically accepts source debit intent and its matching in-transit shipment.</summary>
        public static StateTransitionResult Dispatch(AcceptedState prior, string operationId, string clientRequestId, long sequence, string payloadHash, long expectedRevision, string expectedHash, ActiveShipmentRecord shipment, PhysicalEffectIntent intent, double appliedUt)
            => Dispatch(prior, operationId, clientRequestId, sequence, payloadHash, expectedRevision, expectedHash, shipment, intent, appliedUt, null, null);

        /// <summary>Atomically accepts a scheduled debit, shipment and complete rule schedule update.</summary>
        public static StateTransitionResult Dispatch(AcceptedState prior, string operationId, string clientRequestId, long sequence, string payloadHash, long expectedRevision, string expectedHash, ActiveShipmentRecord shipment, PhysicalEffectIntent intent, double appliedUt, DeliveryRuleRecord? scheduleRuleUpdate)
            => Dispatch(prior, operationId, clientRequestId, sequence, payloadHash, expectedRevision, expectedHash, shipment, intent, appliedUt, scheduleRuleUpdate, null);

        public static StateTransitionResult Dispatch(AcceptedState prior, string operationId, string clientRequestId, long sequence, string payloadHash, long expectedRevision, string expectedHash, ActiveShipmentRecord shipment, PhysicalEffectIntent intent, double appliedUt, DeliveryRuleRecord? scheduleRuleUpdate, PhysicalSuccessWitness? physicalWitness)
        {
            Validate(prior);
            if (prior.WritesBlocked) return new StateTransitionResult { State = prior, Outcome = "held", Reason = "Physical fault blocks writes." };
            if (!ExactExpectedPrefix(prior, expectedRevision, expectedHash, sequence)) return new StateTransitionResult { State = prior, Outcome = "rejected", Reason = "Expected accepted prefix does not match." };
            if (prior.SchemaVersion != 2 || shipment == null || intent == null || shipment.LegacyOpaque || OperationIdentity.Create(prior.WorldId, sequence, clientRequestId) != operationId || OperationIdentity.DispatchPayloadHash(shipment, intent, scheduleRuleUpdate) != payloadHash || !FiniteNonNegative(appliedUt)) return new StateTransitionResult { State = prior, Outcome = "rejected", Reason = "Dispatch identity, payload hash or UT is invalid." };
            var route = prior.RouteVersions.SingleOrDefault(x => !x.LegacyOpaque && x.RouteId == shipment.RouteId && x.Version == shipment.RouteVersion);
            var source = prior.Depots.SingleOrDefault(x => x.Active && x.DepotId == shipment.SourceDepotId);
            var destination = prior.Depots.SingleOrDefault(x => x.Active && x.DepotId == shipment.DestinationDepotId);
            if (route == null || source == null || (!OreExportPolicy.IsVirtual(route) && destination == null) || route.SourceDepotId != shipment.SourceDepotId || route.DestinationDepotId != shipment.DestinationDepotId || route.SourceMembershipRevision != source.MembershipRevision || route.SourceMembershipHash != source.MembershipHash || (!OreExportPolicy.IsVirtual(route) && (route.DestinationMembershipRevision != destination!.MembershipRevision || route.DestinationMembershipHash != destination.MembershipHash)) || route.DestinationKind != shipment.DestinationKind || route.FundsPerUnit != shipment.FundsPerUnit || intent.EffectKind != "dispatchDebit" || intent.Capability.DepotId != source.DepotId || intent.Capability.MembershipRevision != source.MembershipRevision || intent.Capability.MembershipHash != source.MembershipHash || AcceptedStateV2Draft.ValidatePhysicalIntent(intent) != null)
                return new StateTransitionResult { State = prior, Outcome = "held", Reason = "Dispatch source/destination capability or immutable route membership is stale or unavailable." };
            if (shipment.DepartureUt != 0 || shipment.DueUt != 0 || !DispatchManifestMatches(route, shipment, scheduleRuleUpdate) || !DeltasMatch(intent.Deltas, shipment.RemainingResources, negative: true))
                return new StateTransitionResult { State = prior, Outcome = "rejected", Reason = "Dispatch template cargo or debit deltas disagree with immutable route or schedule rule." };
            if (scheduleRuleUpdate is not null && !ValidScheduledRuleUpdate(prior, route, scheduleRuleUpdate))
                return new StateTransitionResult { State = prior, Outcome = "rejected", Reason = "Scheduled dispatch rule update does not match the accepted rule and route." };
            if (physicalWitness is not null && AcceptedStateV2Draft.ValidatePhysicalSuccessWitness(intent, physicalWitness) is string witnessError)
                return new StateTransitionResult { State = prior, Outcome = "rejected", Reason = witnessError };
            var dueUt = appliedUt + route.TravelDurationSeconds;
            if (!FiniteNonNegative(dueUt) || dueUt <= appliedUt) return new StateTransitionResult { State = prior, Outcome = "rejected", Reason = "Actual dispatch UT plus route duration overflowed." };
            if (prior.ActiveShipments.Any(x => x.ShipmentId == shipment.ShipmentId) || prior.ActiveShipments.Length >= AcceptedStateV2Limits.MaxActiveShipments) return new StateTransitionResult { State = prior, Outcome = "held", Reason = "Shipment identity is already active or active-shipment capacity is full." };
            var next = Clone(prior); var acceptedShipment = CloneShipment(shipment); acceptedShipment.DepartureUt = appliedUt; acceptedShipment.DueUt = dueUt; next.ActiveShipments = next.ActiveShipments.Concat(new[] { acceptedShipment }).ToArray();
            if (scheduleRuleUpdate is not null)
                next.DeliveryRules = next.DeliveryRules.Select(x => x.RuleId == scheduleRuleUpdate.RuleId ? CloneRule(scheduleRuleUpdate) : x).ToArray();
            return AcceptV2Mutation(prior, next, operationId, clientRequestId, sequence, payloadHash, "dispatch", 0, 0, appliedUt, physicalWitness);
        }

        static bool DispatchManifestMatches(RouteVersionRecord route, ActiveShipmentRecord shipment, DeliveryRuleRecord? scheduleRuleUpdate)
        {
            if (scheduleRuleUpdate == null) return SameResources(route.Resources, shipment.RemainingResources);
            if (scheduleRuleUpdate.Kind == "repeat" || scheduleRuleUpdate.Kind == "exportStock") return SameResources(route.Resources, shipment.RemainingResources);
            return scheduleRuleUpdate.Kind == "keepStock" && route.Resources.Length == 1 && route.Resources[0].ResourceName == scheduleRuleUpdate.ResourceName && route.Resources[0].AmountMicroUnits == scheduleRuleUpdate.BatchSizeMicroUnits && shipment.RemainingResources.Length == 1 && shipment.RemainingResources[0].ResourceName == scheduleRuleUpdate.ResourceName && shipment.RemainingResources[0].AmountMicroUnits > 0 && shipment.RemainingResources[0].AmountMicroUnits <= scheduleRuleUpdate.BatchSizeMicroUnits;
        }

        static bool ValidScheduledRuleUpdate(AcceptedState prior, RouteVersionRecord route, DeliveryRuleRecord update)
        {
            var old = prior.DeliveryRules.SingleOrDefault(x => x.RuleId == update.RuleId && !x.LegacyOpaque);
            if (old == null || update.Revision != old.Revision + 1 || update.Kind != old.Kind || update.RouteId != old.RouteId || update.RouteVersion != old.RouteVersion || update.RouteId != route.RouteId || update.RouteVersion != route.Version || update.Enabled != old.Enabled || update.IntervalSeconds != old.IntervalSeconds || update.ResourceName != old.ResourceName || update.LowTriggerMicroUnits != old.LowTriggerMicroUnits || update.TargetMicroUnits != old.TargetMicroUnits || update.BatchSizeMicroUnits != old.BatchSizeMicroUnits || update.LegacyOpaque || update.WaitingRequest || !FiniteNonNegative(update.WaitingScheduledUt) || update.WaitingCoalescedSlots < 0)
                return false;
            if (update.Kind == "repeat") return FiniteNonNegative(update.NextDueUt) && (update.NextDueUt > old.NextDueUt || old.WaitingRequest && update.NextDueUt == old.NextDueUt) && update.WaitingCoalescedSlots > 0;
            return (update.Kind == "keepStock" || update.Kind == "exportStock") && update.NextDueUt == old.NextDueUt && update.WaitingScheduledUt == old.WaitingScheduledUt && update.WaitingCoalescedSlots == old.WaitingCoalescedSlots;
        }

        /// <summary>Accepts only verified destination credits; residual cargo remains active and unchanged in identity.</summary>
        public static StateTransitionResult Arrive(AcceptedState prior, string operationId, string clientRequestId, long sequence, string payloadHash, long expectedRevision, string expectedHash, string shipmentId, ResourceAmount[] credits, ResourceAmount[] remaining, PhysicalEffectIntent intent, double appliedUt)
            => Arrive(prior, operationId, clientRequestId, sequence, payloadHash, expectedRevision, expectedHash, shipmentId, credits, remaining, intent, appliedUt, null);

        public static StateTransitionResult Arrive(AcceptedState prior, string operationId, string clientRequestId, long sequence, string payloadHash, long expectedRevision, string expectedHash, string shipmentId, ResourceAmount[] credits, ResourceAmount[] remaining, PhysicalEffectIntent intent, double appliedUt, PhysicalSuccessWitness? physicalWitness)
        {
            Validate(prior);
            if (prior.WritesBlocked) return new StateTransitionResult { State = prior, Outcome = "held", Reason = "Physical fault blocks writes." };
            if (!ExactExpectedPrefix(prior, expectedRevision, expectedHash, sequence)) return new StateTransitionResult { State = prior, Outcome = "rejected", Reason = "Expected accepted prefix does not match." };
            if (prior.SchemaVersion != 2 || String.IsNullOrWhiteSpace(shipmentId) || credits == null || remaining == null || intent == null || OperationIdentity.Create(prior.WorldId, sequence, clientRequestId) != operationId || OperationIdentity.ArrivalPayloadHash(shipmentId, credits, remaining, intent) != payloadHash || !FiniteNonNegative(appliedUt)) return new StateTransitionResult { State = prior, Outcome = "rejected", Reason = "Arrival identity, payload hash or UT is invalid." };
            var shipment = prior.ActiveShipments.SingleOrDefault(x => x.ShipmentId == shipmentId && !x.LegacyOpaque);
            var route = shipment == null ? null : prior.RouteVersions.SingleOrDefault(x => !x.LegacyOpaque && x.RouteId == shipment.RouteId && x.Version == shipment.RouteVersion);
            var destination = shipment == null ? null : prior.Depots.SingleOrDefault(x => x.Active && x.DepotId == shipment.DestinationDepotId);
            if (shipment == null || route == null || OreExportPolicy.IsVirtual(route) || destination == null || route.DestinationDepotId != shipment.DestinationDepotId || route.DestinationMembershipRevision != destination.MembershipRevision || route.DestinationMembershipHash != destination.MembershipHash || intent.EffectKind != "arrivalCredit" || intent.Capability.DepotId != destination.DepotId || intent.Capability.MembershipRevision != destination.MembershipRevision || intent.Capability.MembershipHash != destination.MembershipHash || AcceptedStateV2Draft.ValidatePhysicalIntent(intent) != null)
                return new StateTransitionResult { State = prior, Outcome = "held", Reason = "Arrival destination capability or immutable route membership is stale or unavailable; cargo remains in transit." };
            if (appliedUt < shipment.DueUt || !ResourceSplitMatches(shipment.RemainingResources, credits, remaining) || !DeltasMatch(intent.Deltas, credits, negative: false)) return new StateTransitionResult { State = prior, Outcome = "rejected", Reason = "Arrival is early or credited/residual cargo does not conserve the shipment." };
            if (physicalWitness is not null && AcceptedStateV2Draft.ValidatePhysicalSuccessWitness(intent, physicalWitness) is string witnessError)
                return new StateTransitionResult { State = prior, Outcome = "rejected", Reason = witnessError };
            var next = Clone(prior);
            if (remaining.Length == 0) next.ActiveShipments = next.ActiveShipments.Where(x => x.ShipmentId != shipmentId).ToArray();
            else next.ActiveShipments = next.ActiveShipments.Select(x => x.ShipmentId == shipmentId ? CloneShipmentWith(x, remaining) : x).ToArray();
            return AcceptV2Mutation(prior, next, operationId, clientRequestId, sequence, payloadHash, "arrival", 0, 0, appliedUt, physicalWitness);
        }

        /// <summary>Consumes an uncertain physical operation identity and persists bounded fault evidence.</summary>
        public static StateTransitionResult FaultPhysicalEffect(AcceptedState prior, string operationId, string clientRequestId, long sequence, string payloadHash, long expectedRevision, string expectedHash, string operationKind, string depotId, long membershipRevision, string membershipHash, PhysicalEffectIntent intent, PhysicalEffectResult result, double appliedUt)
        {
            Validate(prior);
            if (prior.WritesBlocked) return new StateTransitionResult { State = prior, Outcome = "held", Reason = "An unresolved physical fault already blocks writes." };
            if (!ExactExpectedPrefix(prior, expectedRevision, expectedHash, sequence)) return new StateTransitionResult { State = prior, Outcome = "rejected", Reason = "Expected accepted prefix does not match." };
            if (prior.SchemaVersion != 2 || (operationKind != "dispatch" && operationKind != "arrival") || OperationIdentity.Create(prior.WorldId, sequence, clientRequestId) != operationId || !FiniteNonNegative(appliedUt) || AcceptedStateV2Draft.ValidatePhysicalIntent(intent) != null || result == null || result.Status != "uncertain") return new StateTransitionResult { State = prior, Outcome = "rejected", Reason = "Fault identity, capability or uncertain result is invalid." };
            try { AcceptedStateV2Draft.ValidatePhysicalResult(result); } catch (InvalidDataException ex) { return new StateTransitionResult { State = prior, Outcome = "rejected", Reason = ex.Message }; }
            if (result.Deltas.Length != intent.Deltas.Length) return new StateTransitionResult { State = prior, Outcome = "rejected", Reason = "Fault evidence does not cover every intended resource delta." };
            var intended = intent.Deltas.OrderBy(x => x.MemberPersistentId).ThenBy(x => x.ResourceName, StringComparer.Ordinal).ToArray();
            var actual = result.Deltas.OrderBy(x => x.MemberPersistentId).ThenBy(x => x.ResourceName, StringComparer.Ordinal).ToArray();
            for (var i = 0; i < intended.Length; i++) if (intended[i].MemberPersistentId != actual[i].MemberPersistentId || intended[i].ResourceName != actual[i].ResourceName || intended[i].DeltaMicroUnits != actual[i].IntendedDeltaMicroUnits) return new StateTransitionResult { State = prior, Outcome = "rejected", Reason = "Fault evidence does not match the prepared member/resource intent." };
            if (!prior.Depots.Any(d => d.DepotId == depotId && d.MembershipRevision == membershipRevision && d.MembershipHash == membershipHash)) return new StateTransitionResult { State = prior, Outcome = "rejected", Reason = "Fault endpoint identity is not retained in the accepted registry." };
            var next = MigrateToV2ForAcceptedMutation(prior); var fault = new PhysicalFaultRecord { FaultId = "fault:" + operationId, OperationId = operationId, OperationKind = operationKind, CommandSequence = sequence, DepotId = depotId, MembershipRevision = membershipRevision, MembershipHash = membershipHash, Reason = String.IsNullOrWhiteSpace(result.Reason) ? "Physical mutation outcome is uncertain." : result.Reason.Substring(0, Math.Min(256, result.Reason.Length)), RollbackStatus = result.RollbackStatus, Deltas = actual };
            next.Faults = next.Faults.Concat(new[] { fault }).ToArray();
            if (next.Receipts.Length >= MaxReceipts || next.Faults.Length > AcceptedStateV2Limits.MaxFaultRecords) return new StateTransitionResult { State = prior, Outcome = "held", Reason = "No bounded receipt/fault capacity is available." };
            next.Revision = prior.Revision + 1; next.AcceptedSequence = sequence;
            next.Receipts = next.Receipts.Concat(new[] { new AcceptedReceipt { WorldId = prior.WorldId, CommandSequence = sequence, OperationId = operationId, PayloadHash = payloadHash, Outcome = "faulted", OperationKind = operationKind, AppliedUt = appliedUt } }).ToArray();
            try { Validate(next); Serialize(next); CreateCapsule(next); } catch (InvalidDataException ex) { return new StateTransitionResult { State = prior, Outcome = "held", Reason = ex.Message }; }
            return new StateTransitionResult { State = next, Outcome = "faulted" };
        }

        static bool SameResources(ResourceAmount[] expected, ResourceAmount[] actual)
            => expected.Length == actual.Length && expected.OrderBy(x => x.ResourceName, StringComparer.Ordinal).Zip(actual.OrderBy(x => x.ResourceName, StringComparer.Ordinal), (a,b) => a.ResourceName == b.ResourceName && a.AmountMicroUnits == b.AmountMicroUnits).All(x => x);
        static bool DeltasMatch(PhysicalEffectResourceDelta[] deltas, ResourceAmount[] resources, bool negative)
        {
            if (deltas == null || resources == null) return false;
            foreach (var resource in resources)
            {
                var rows = deltas.Where(x => x.ResourceName == resource.ResourceName).ToArray();
                if (rows.Length == 0) return false;
                long sum = 0; foreach (var row in rows) { if ((negative && row.DeltaMicroUnits >= 0) || (!negative && row.DeltaMicroUnits <= 0) || (negative && row.DeltaMicroUnits == Int64.MinValue)) return false; var amount = negative ? -row.DeltaMicroUnits : row.DeltaMicroUnits; if (sum > Int64.MaxValue - amount) return false; sum += amount; }
                if (sum != resource.AmountMicroUnits) return false;
            }
            return deltas.All(d => resources.Any(r => r.ResourceName == d.ResourceName));
        }
        static bool ResourceSplitMatches(ResourceAmount[] original, ResourceAmount[] credits, ResourceAmount[] remaining)
        {
            if (original == null || credits == null || remaining == null || credits.Length > AcceptedStateV2Limits.MaxManifestResources || remaining.Length > AcceptedStateV2Limits.MaxManifestResources) return false;
            var creditNames = new HashSet<string>(StringComparer.Ordinal); var remainingNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in credits) if (row == null || String.IsNullOrWhiteSpace(row.ResourceName) || row.AmountMicroUnits <= 0 || !creditNames.Add(row.ResourceName)) return false;
            foreach (var row in remaining) if (row == null || String.IsNullOrWhiteSpace(row.ResourceName) || row.AmountMicroUnits <= 0 || !remainingNames.Add(row.ResourceName)) return false;
            foreach (var row in original)
            {
                var credit = credits.SingleOrDefault(x => x.ResourceName == row.ResourceName)?.AmountMicroUnits ?? 0;
                var residual = remaining.SingleOrDefault(x => x.ResourceName == row.ResourceName)?.AmountMicroUnits ?? 0;
                if (credit < 0 || residual < 0 || credit > Int64.MaxValue - residual || credit + residual != row.AmountMicroUnits) return false;
            }
            return credits.All(c => original.Any(o => o.ResourceName == c.ResourceName)) && remaining.All(r => original.Any(o => o.ResourceName == r.ResourceName));
        }
        static ActiveShipmentRecord CloneShipment(ActiveShipmentRecord x) => new ActiveShipmentRecord { ShipmentId=x.ShipmentId, Revision=x.Revision, RouteId=x.RouteId, RouteVersion=x.RouteVersion, SourceDepotId=x.SourceDepotId, DestinationDepotId=x.DestinationDepotId, DestinationKind=x.DestinationKind, FundsPerUnit=x.FundsPerUnit, DepartureUt=x.DepartureUt, DueUt=x.DueUt, RemainingResources=x.RemainingResources.Select(r=>new ResourceAmount{ResourceName=r.ResourceName,AmountMicroUnits=r.AmountMicroUnits}).ToArray(), HeldReason=x.HeldReason, LegacyOpaque=x.LegacyOpaque };
        static ActiveShipmentRecord CloneShipmentWith(ActiveShipmentRecord x, ResourceAmount[] rows) { var result=CloneShipment(x); result.Revision++; result.RemainingResources=rows.Select(r=>new ResourceAmount{ResourceName=r.ResourceName,AmountMicroUnits=r.AmountMicroUnits}).ToArray(); return result; }

        static bool ExactExpectedPrefix(AcceptedState state, long revision, string hash, long sequence)
            => state.Revision == revision && ComputeHash(state) == hash && sequence > 0 && state.AcceptedSequence < Int64.MaxValue && sequence == state.AcceptedSequence + 1 && state.Revision < Int64.MaxValue;

        static StateTransitionResult AcceptV2Mutation(AcceptedState prior, AcceptedState next, string operationId, string requestId, long sequence, string payloadHash, string kind, long delta, long target, double appliedUt, PhysicalSuccessWitness? physicalWitness = null)
        {
            if (next.Receipts.Length >= MaxReceipts) return new StateTransitionResult { State = prior, Outcome = "held", Reason = "Receipt capacity reached." };
            next.Revision = prior.Revision + 1; next.AcceptedSequence = sequence;
            next.Receipts = next.Receipts.Concat(new[] { new AcceptedReceipt { WorldId = prior.WorldId, CommandSequence = sequence, OperationId = operationId, PayloadHash = payloadHash, Outcome = "accepted", OperationKind = kind, CounterDelta = delta, TargetCompactionWatermark = target, AppliedUt = appliedUt, PhysicalWitness = ClonePhysicalWitness(physicalWitness) } }).ToArray();
            if (physicalWitness is not null) next.CapsuleEncodingVersion = Math.Max(3, next.CapsuleEncodingVersion);
            try { Validate(next); Serialize(next); CreateCapsule(next); }
            catch (InvalidDataException ex) { return new StateTransitionResult { State = prior, Outcome = "held", Reason = ex.Message }; }
            return new StateTransitionResult { State = next, Outcome = "accepted" };
        }

        static RouteVersionRecord CloneRoute(RouteVersionRecord x) => new RouteVersionRecord { RouteId = x.RouteId, Revision = x.Revision, Version = x.Version, SourceDepotId = x.SourceDepotId, SourceMembershipRevision = x.SourceMembershipRevision, SourceMembershipHash = x.SourceMembershipHash, DestinationDepotId = x.DestinationDepotId, DestinationKind = x.DestinationKind, FundsPerUnit = x.FundsPerUnit, DestinationMembershipRevision = x.DestinationMembershipRevision, DestinationMembershipHash = x.DestinationMembershipHash, TravelDurationSeconds = x.TravelDurationSeconds, Provenance = x.Provenance, Resources = x.Resources.Select(r => new ResourceAmount { ResourceName = r.ResourceName, AmountMicroUnits = r.AmountMicroUnits }).ToArray(), LegacyOpaque = x.LegacyOpaque };
        static DeliveryRuleRecord CloneRule(DeliveryRuleRecord x) => new DeliveryRuleRecord { RuleId = x.RuleId, Revision = x.Revision, Kind = x.Kind, RouteId = x.RouteId, RouteVersion = x.RouteVersion, Enabled = x.Enabled, NextDueUt = x.NextDueUt, IntervalSeconds = x.IntervalSeconds, WaitingRequest = x.WaitingRequest, WaitingScheduledUt = x.WaitingScheduledUt, WaitingCoalescedSlots = x.WaitingCoalescedSlots, ResourceName = x.ResourceName, LowTriggerMicroUnits = x.LowTriggerMicroUnits, TargetMicroUnits = x.TargetMicroUnits, BatchSizeMicroUnits = x.BatchSizeMicroUnits, LegacyOpaque = x.LegacyOpaque };

        public static AcceptedState Compact(AcceptedState state, string operationId, string clientRequestId, long sequence, string payloadHash, long expectedRevision, string expectedHash, long watermark, double appliedUt)
        {
            Validate(state); if (state.Revision != expectedRevision || ComputeHash(state) != expectedHash) throw new InvalidDataException("State changed before compaction.");
            if (watermark < state.CompactionWatermark || watermark > state.AcceptedSequence || sequence != state.AcceptedSequence + 1 || state.Revision == Int64.MaxValue || sequence <= 0 || OperationIdentity.Create(state.WorldId, sequence, clientRequestId) != operationId || OperationIdentity.CompactionPayloadHash(watermark) != payloadHash || Double.IsNaN(appliedUt) || Double.IsInfinity(appliedUt) || appliedUt < 0) throw new InvalidDataException("Invalid compaction operation.");
            if (state.SchemaVersion == 2 && (state.Faults.Any(x => x.CommandSequence <= watermark) || state.EconomicFaults.Any(x => x.CommandSequence <= watermark))) throw new InvalidDataException("Compaction cannot remove the terminal receipt for an unresolved physical fault.");
            var next = Clone(state); var retained = new List<AcceptedReceipt>(); foreach (var r in next.Receipts) if (r.CommandSequence > watermark) retained.Add(r);
            if (retained.Count >= MaxReceipts) throw new InvalidDataException("Compaction target would not leave room for the compaction receipt.");
            next.CompactionWatermark = watermark; next.Revision++; next.AcceptedSequence = sequence;
            next.ParentCheckpointId = state.CheckpointId;
            next.CheckpointId = OperationIdentity.CreateCheckpointId(state.CheckpointId, expectedHash, operationId);
            retained.Add(new AcceptedReceipt { WorldId = state.WorldId, CommandSequence = sequence, OperationId = operationId, PayloadHash = payloadHash, Outcome = "accepted", OperationKind = "compact", TargetCompactionWatermark = watermark, AppliedUt = appliedUt });
            next.Receipts = retained.ToArray(); Validate(next); Serialize(next); return next;
        }

        static PhysicalSuccessWitness? ClonePhysicalWitness(PhysicalSuccessWitness? witness) => witness is null ? null : new PhysicalSuccessWitness { ProviderId = witness.ProviderId, ProviderVersion = witness.ProviderVersion, Rows = witness.Rows.Select(x => new PhysicalSuccessWitnessRow { MemberPersistentId = x.MemberPersistentId, ResourceName = x.ResourceName, BeforeAmount = x.BeforeAmount, IntendedAfterAmount = x.IntendedAfterAmount, ObservedAfterAmount = x.ObservedAfterAmount }).ToArray() };
        static AcceptedReceipt CloneReceipt(AcceptedReceipt r) => new AcceptedReceipt { WorldId = r.WorldId, CommandSequence = r.CommandSequence, OperationId = r.OperationId, PayloadHash = r.PayloadHash, Outcome = r.Outcome, OperationKind = r.OperationKind, CounterDelta = r.CounterDelta, TargetCompactionWatermark = r.TargetCompactionWatermark, AppliedUt = r.AppliedUt, PhysicalWitness = ClonePhysicalWitness(r.PhysicalWitness), FundsWitness = CloneFundsWitness(r.FundsWitness) };
        static AcceptedState Clone(AcceptedState s) => new AcceptedState { SchemaVersion = s.SchemaVersion, CapsuleEncodingVersion = s.CapsuleEncodingVersion, WorldId = s.WorldId, CheckpointId = s.CheckpointId, ParentCheckpointId = s.ParentCheckpointId, ParentBranchId = s.ParentBranchId, Revision = s.Revision, AcceptedSequence = s.AcceptedSequence, CompactionWatermark = s.CompactionWatermark, Counter = s.Counter, DepotRegistryVersion = s.DepotRegistryVersion, DepotRegistryHash = s.DepotRegistryHash, Depots = (DepotRecord[])s.Depots.Clone(), Routes = (RouteRecord[])s.Routes.Clone(), Rules = (RuleRecord[])s.Rules.Clone(), Shipments = (ShipmentRecord[])s.Shipments.Clone(), Receipts = s.Receipts.Select(CloneReceipt).ToArray(), RouteVersions = (RouteVersionRecord[])s.RouteVersions.Clone(), DeliveryRules = (DeliveryRuleRecord[])s.DeliveryRules.Clone(), ActiveShipments = (ActiveShipmentRecord[])s.ActiveShipments.Clone(), Faults = (PhysicalFaultRecord[])s.Faults.Clone(), EconomicFaults = s.EconomicFaults.Select(CloneEconomicFault).ToArray() };
        static void Unique<T>(T[] xs, Func<T, string> id) { var set = new HashSet<string>(StringComparer.Ordinal); foreach (var x in xs) if (x == null || String.IsNullOrWhiteSpace(id(x)) || !set.Add(id(x))) throw new InvalidDataException("Missing or duplicate active identity."); }
        static void WriteDepots(BinaryWriter w, DepotRecord[] xs) { var a = (DepotRecord[])xs.Clone(); Array.Sort(a, (x,y) => String.CompareOrdinal(x.DepotId,y.DepotId)); I32(w,a.Length); foreach(var x in a){Text(w,x.DepotId);I64(w,x.MembershipRevision);} }
        static void WriteSimple<T>(BinaryWriter w,T[] xs,Func<T,string> id,Func<T,long> rev){var a=(T[])xs.Clone();Array.Sort(a,(x,y)=>String.CompareOrdinal(id(x),id(y)));I32(w,a.Length);foreach(var x in a){Text(w,id(x));I64(w,rev(x));}}
        static T[] ReadSimple<T>(BinaryReader r,Func<(string Id,long Revision),T> make){var n=ReadCount(r,32);var a=new T[n];for(int i=0;i<n;i++)a[i]=make((ReadText(r),ReadI64(r)));return a;}
        static DepotRecord[] ReadDepots(BinaryReader r){var n=ReadCount(r,MaxDepots);var a=new DepotRecord[n];for(int i=0;i<n;i++)a[i]=new DepotRecord{DepotId=ReadText(r),MembershipRevision=ReadI64(r)};return a;}
        static void WriteDepotsV2(BinaryWriter w, DepotRecord[] xs) { var a=(DepotRecord[])xs.Clone(); Array.Sort(a,(x,y)=>{var c=String.CompareOrdinal(x.DepotId,y.DepotId);if(c!=0)return c;c=x.MembershipRevision.CompareTo(y.MembershipRevision);return c!=0?c:String.CompareOrdinal(x.MembershipHash,y.MembershipHash);}); I32(w,a.Length); foreach(var x in a){Text(w,x.DepotId);I64(w,x.MembershipRevision);Text(w,x.MembershipHash);Bool(w,x.Active);} }
        static DepotRecord[] ReadDepotsV2(BinaryReader r) { var n=ReadCount(r,AcceptedStateV2Limits.MaxDepotRecords);var a=new DepotRecord[n];for(int i=0;i<n;i++)a[i]=new DepotRecord{DepotId=ReadText(r),MembershipRevision=ReadI64(r),MembershipHash=ReadText(r),Active=ReadBool(r)};return a; }
        static int ReadCount(BinaryReader r,int max){var n=ReadI32(r);if(n<0||n>max)throw new InvalidDataException("Collection bound exceeded.");return n;}
        static void I32(BinaryWriter w,int x){w.Write((byte)1);w.Write(x);} static int ReadI32(BinaryReader r){if(r.ReadByte()!=1)throw new InvalidDataException("Unexpected canonical field tag.");return r.ReadInt32();}
        static void I64(BinaryWriter w,long x){w.Write((byte)2);w.Write(x);} static long ReadI64(BinaryReader r){if(r.ReadByte()!=2)throw new InvalidDataException("Unexpected canonical field tag.");return r.ReadInt64();}
        static void U32(BinaryWriter w,uint x){w.Write((byte)7);w.Write(x);} static uint ReadU32(BinaryReader r){if(r.ReadByte()!=7)throw new InvalidDataException("Unexpected canonical field tag.");return r.ReadUInt32();}
        static void Bool(BinaryWriter w,bool x){w.Write((byte)6);w.Write(x);} static bool ReadBool(BinaryReader r){if(r.ReadByte()!=6)throw new InvalidDataException("Unexpected canonical field tag.");return r.ReadBoolean();}
        static void F64(BinaryWriter w,double x){w.Write((byte)5);w.Write(BitConverter.DoubleToInt64Bits(x));} static double ReadF64(BinaryReader r){if(r.ReadByte()!=5)throw new InvalidDataException("Unexpected canonical field tag.");var x=BitConverter.Int64BitsToDouble(r.ReadInt64());if(Double.IsNaN(x)||Double.IsInfinity(x)||x<0)throw new InvalidDataException("Invalid applied UT.");return x;}
        static void Text(BinaryWriter w,string x){if(x==null)throw new InvalidDataException("Null text field.");var b=new UTF8Encoding(false,true).GetBytes(x);if(b.Length>4096)throw new InvalidDataException("Text field too long.");w.Write((byte)3);w.Write(b.Length);w.Write(b);}
        static string ReadText(BinaryReader r){if(r.ReadByte()!=3)throw new InvalidDataException("Unexpected canonical field tag.");var n=r.ReadInt32();if(n<0||n>4096||n>r.BaseStream.Length-r.BaseStream.Position)throw new InvalidDataException("Invalid text length.");return new UTF8Encoding(false,true).GetString(r.ReadBytes(n));}
        static void NullableText(BinaryWriter w,string? x){w.Write((byte)4);w.Write(x!=null);if(x!=null)Text(w,x);} static string? ReadNullableText(BinaryReader r){if(r.ReadByte()!=4)throw new InvalidDataException("Unexpected canonical field tag.");return r.ReadBoolean()?ReadText(r):null;}
        static bool FixedEquals(string a,string b){if(b==null||a.Length!=b.Length)return false;int d=0;for(int i=0;i<a.Length;i++)d|=a[i]^b[i];return d==0;}
        static bool IsSha256(string value) => value.Length == 64 && value.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
        static bool FiniteNonNegative(double value) => value >= 0 && !Double.IsNaN(value) && !Double.IsInfinity(value);
    }

    public static partial class OperationIdentity
    {
        static bool IsSha256(string value) => value != null && value.Length == 64 && value.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
        /// <summary>Deterministic identity binds an operation ID to its world, sequence and client retry ID.</summary>
        public static string Create(string worldId, long commandSequence, string clientRequestId)
        {
            if (String.IsNullOrWhiteSpace(worldId) || String.IsNullOrWhiteSpace(clientRequestId) || commandSequence <= 0) throw new ArgumentException("Operation identity fields are invalid.");
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), true))
            using (var sha = SHA256.Create())
            {
                var w = new UTF8Encoding(false, true).GetBytes(worldId); var r = new UTF8Encoding(false, true).GetBytes(clientRequestId);
                writer.Write((byte)1); writer.Write(w.Length); writer.Write(w); writer.Write((byte)2); writer.Write(commandSequence);
                writer.Write((byte)3); writer.Write(r.Length); writer.Write(r); writer.Flush();
                var hash = sha.ComputeHash(stream.ToArray()); var b = new StringBuilder(67); b.Append("op1-"); foreach (var x in hash) b.Append(x.ToString("x2", CultureInfo.InvariantCulture)); return b.ToString();
            }
        }

        public static string CounterIncrementPayloadHash(long delta)
        {
            if (delta <= 0) throw new ArgumentOutOfRangeException(nameof(delta));
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream)) using (var sha = SHA256.Create())
            { writer.Write((byte)1); var kind = Encoding.UTF8.GetBytes("counterIncrement"); writer.Write(kind.Length); writer.Write(kind); writer.Write((byte)2); writer.Write(delta); writer.Flush(); var hash = sha.ComputeHash(stream.ToArray()); var b = new StringBuilder(64); foreach (var x in hash) b.Append(x.ToString("x2", CultureInfo.InvariantCulture)); return b.ToString(); }
        }

        public static string CompactionPayloadHash(long targetWatermark)
        {
            if (targetWatermark < 0) throw new ArgumentOutOfRangeException(nameof(targetWatermark));
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream)) using (var sha = SHA256.Create())
            { writer.Write((byte)1); var kind = Encoding.UTF8.GetBytes("compact"); writer.Write(kind.Length); writer.Write(kind); writer.Write((byte)2); writer.Write(targetWatermark); writer.Flush(); var hash = sha.ComputeHash(stream.ToArray()); var b = new StringBuilder(64); foreach (var x in hash) b.Append(x.ToString("x2", CultureInfo.InvariantCulture)); return b.ToString(); }
        }

        public static string RouteVersionPayloadHash(RouteVersionRecord route)
        {
            if (route == null || route.LegacyOpaque || String.IsNullOrWhiteSpace(route.RouteId) || route.Version <= 0 || String.IsNullOrWhiteSpace(route.SourceDepotId) || String.IsNullOrWhiteSpace(route.DestinationDepotId) || route.SourceDepotId == route.DestinationDepotId || route.SourceMembershipRevision < 0 || route.DestinationMembershipRevision < 0 || !IsSha256(route.SourceMembershipHash) || (!OreExportPolicy.IsVirtual(route) && !IsSha256(route.DestinationMembershipHash)) || Double.IsNaN(route.TravelDurationSeconds) || Double.IsInfinity(route.TravelDurationSeconds) || route.TravelDurationSeconds <= 0 || String.IsNullOrWhiteSpace(route.Provenance) || route.Resources == null || route.Resources.Length == 0 || route.Resources.Length > AcceptedStateV2Limits.MaxManifestResources) throw new ArgumentException("Invalid route version payload.", nameof(route));
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), true))
            {
                if (route.DestinationKind != "physicalDepot" && !OreExportPolicy.ValidBuyer(route) || route.DestinationKind == "physicalDepot" && route.FundsPerUnit != 0) throw new ArgumentException("Invalid route buyer economics.");
                if (OreExportPolicy.IsVirtual(route)) { HashText(writer, route.DestinationKind); HashI64(writer, route.FundsPerUnit); }
                HashText(writer, "routeUpsert"); HashText(writer, route.RouteId); HashI64(writer, route.Version); HashText(writer, route.SourceDepotId); HashI64(writer, route.SourceMembershipRevision); HashText(writer, route.SourceMembershipHash); HashText(writer, route.DestinationDepotId); HashI64(writer, route.DestinationMembershipRevision); HashText(writer, route.DestinationMembershipHash); HashF64(writer, route.TravelDurationSeconds); HashText(writer, route.Provenance);
                var rows = route.Resources.OrderBy(x => x.ResourceName, StringComparer.Ordinal).ToArray(); HashI32(writer, rows.Length); var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in rows) { if (row == null || String.IsNullOrWhiteSpace(row.ResourceName) || row.AmountMicroUnits <= 0 || !names.Add(row.ResourceName)) throw new ArgumentException("Route resources must be unique and positive.", nameof(route)); HashText(writer, row.ResourceName); HashI64(writer, row.AmountMicroUnits); }
                return Hash(stream);
            }
        }

        public static string ComputeDepotRegistryHash(IEnumerable<DepotRecord> activeDepots)
        {
            if (activeDepots == null) throw new ArgumentNullException(nameof(activeDepots));
            var rows = activeDepots.OrderBy(x => x.DepotId, StringComparer.Ordinal).ToArray();
            if (rows.Length > AcceptedStateCodec.MaxDepots || rows.Any(x => x == null || !x.Active || String.IsNullOrWhiteSpace(x.DepotId) || x.DepotId.Length > 128 || x.MembershipRevision < 0 || !IsSha256(x.MembershipHash)) || rows.Select(x => x.DepotId).Distinct(StringComparer.Ordinal).Count() != rows.Length) throw new ArgumentException("Registry snapshot must contain bounded unique active depot identities and SHA-256 membership hashes.", nameof(activeDepots));
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), true))
            {
                HashText(writer, "depotRegistry"); HashI32(writer, rows.Length);
                foreach (var row in rows) { HashText(writer, row.DepotId); HashI64(writer, row.MembershipRevision); HashText(writer, row.MembershipHash); }
                return Hash(stream);
            }
        }

        public static string ComputeMemberSetHash(uint anchorPersistentId, IEnumerable<uint> memberPersistentIds)
        {
            if (anchorPersistentId == 0 || memberPersistentIds == null) throw new ArgumentException("Anchor and member IDs are required.");
            var ids = memberPersistentIds.OrderBy(x => x).ToArray();
            if (ids.Length == 0 || ids.Length > 64 || ids.Any(x => x == 0) || ids.Distinct().Count() != ids.Length) throw new ArgumentException("Member IDs must be bounded, positive and unique.");
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), true))
            { HashText(writer, "depotMembers"); writer.Write((byte)7); writer.Write(anchorPersistentId); HashI32(writer, ids.Length); foreach (var id in ids) { writer.Write((byte)7); writer.Write(id); } return Hash(stream); }
        }

        public static string DepotRegistryPayloadHash(DepotRegistrySnapshot snapshot)
        {
            if (snapshot == null || String.IsNullOrWhiteSpace(snapshot.RegistryVersion) || snapshot.RegistryVersion.Length > 128 || snapshot.RegistryHash != ComputeDepotRegistryHash(snapshot.Depots)) throw new ArgumentException("Registry snapshot fingerprint is invalid.", nameof(snapshot));
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), true))
            { HashText(writer, "syncDepots"); HashText(writer, snapshot.RegistryVersion); HashText(writer, snapshot.RegistryHash); return Hash(stream); }
        }

        public static string DispatchPayloadHash(ActiveShipmentRecord shipment, PhysicalEffectIntent intent)
            => DispatchPayloadHash(shipment, intent, null);

        public static string DispatchPayloadHash(ActiveShipmentRecord shipment, PhysicalEffectIntent intent, DeliveryRuleRecord? scheduleRuleUpdate)
        {
            if (shipment == null || intent == null || intent.EffectKind != "dispatchDebit" || String.IsNullOrWhiteSpace(shipment.ShipmentId) || shipment.LegacyOpaque || shipment.RemainingResources == null) throw new ArgumentException("Dispatch payload is incomplete.");
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), true))
            {
                // The prepared operation binds immutable cargo and intent, never a guessed dispatch UT.
                // Bridge supplies actual applied UT; the deterministic reducer derives departure/due from it.
                if (shipment.DestinationKind != "physicalDepot" || shipment.FundsPerUnit != 0) { HashText(writer, shipment.DestinationKind); HashI64(writer, shipment.FundsPerUnit); }
                HashText(writer, "dispatch"); HashText(writer, shipment.ShipmentId); HashText(writer, shipment.RouteId); HashI64(writer, shipment.RouteVersion); HashText(writer, shipment.SourceDepotId); HashText(writer, shipment.DestinationDepotId); HashResources(writer, shipment.RemainingResources); HashPhysicalIntent(writer, intent);
                // Preserve the frozen send-once hash when absent; the marker is additive only for scheduled dispatches.
                if (scheduleRuleUpdate != null) { HashBool(writer, true); HashText(writer, RulePayloadHash(scheduleRuleUpdate)); }
                return Hash(stream);
            }
        }

        public static string ArrivalPayloadHash(string shipmentId, ResourceAmount[] credits, ResourceAmount[] remaining, PhysicalEffectIntent intent)
        {
            if (String.IsNullOrWhiteSpace(shipmentId) || credits == null || remaining == null || intent == null || intent.EffectKind != "arrivalCredit") throw new ArgumentException("Arrival payload is incomplete.");
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), true))
            {
                HashText(writer, "arrival"); HashText(writer, shipmentId); HashResources(writer, credits); HashResources(writer, remaining); HashPhysicalIntent(writer, intent); return Hash(stream);
            }
        }

        static void HashShipment(BinaryWriter w, ActiveShipmentRecord s)
        { HashText(w, s.ShipmentId); HashText(w, s.RouteId); HashI64(w, s.RouteVersion); HashText(w, s.SourceDepotId); HashText(w, s.DestinationDepotId); HashF64(w, s.DepartureUt); HashF64(w, s.DueUt); HashResources(w, s.RemainingResources); }
        static void HashResources(BinaryWriter w, ResourceAmount[] rows)
        { var sorted = rows.OrderBy(x => x.ResourceName, StringComparer.Ordinal).ToArray(); HashI32(w, sorted.Length); foreach (var row in sorted) { HashText(w, row.ResourceName); HashI64(w, row.AmountMicroUnits); } }
        static void HashPhysicalIntent(BinaryWriter w, PhysicalEffectIntent i)
        {
            var c = i.Capability; HashText(w, i.EffectKind); HashText(w, c.DepotId); HashI64(w, c.MembershipRevision); HashText(w, c.MembershipHash); HashText(w, c.ProviderId); HashText(w, c.ProviderVersion); HashText(w, c.Scene); HashBool(w, c.ObservationAvailable); HashBool(w, c.ReadSupported); HashBool(w, c.WriteSupported); HashBool(w, c.SynchronousRollbackSupported); HashBool(w, c.PersistenceSyncSupported); HashI64(w, c.AnchorPersistentId); HashText(w, c.MemberSetHash);
            var members = i.MemberPersistentIds.OrderBy(x => x).ToArray(); HashI32(w, members.Length); foreach (var id in members) { w.Write((byte)7); w.Write(id); }
            var deltas = i.Deltas.OrderBy(x => x.MemberPersistentId).ThenBy(x => x.ResourceName, StringComparer.Ordinal).ToArray(); HashI32(w, deltas.Length); foreach (var d in deltas) { w.Write((byte)7); w.Write(d.MemberPersistentId); HashText(w, d.ResourceName); HashI64(w, d.DeltaMicroUnits); }
        }

        public static string RulePayloadHash(DeliveryRuleRecord rule)
        {
            if (rule == null || rule.LegacyOpaque || String.IsNullOrWhiteSpace(rule.RuleId) || (rule.Kind != "repeat" && rule.Kind != "keepStock" && rule.Kind != "exportStock") || String.IsNullOrWhiteSpace(rule.RouteId) || rule.RouteVersion <= 0) throw new ArgumentException("Invalid delivery rule payload.", nameof(rule));
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), true))
            {
                HashText(writer, "ruleUpsert"); HashText(writer, rule.RuleId); HashText(writer, rule.Kind); HashText(writer, rule.RouteId); HashI64(writer, rule.RouteVersion); HashBool(writer, rule.Enabled); HashF64(writer, rule.NextDueUt); HashF64(writer, rule.IntervalSeconds); HashBool(writer, rule.WaitingRequest); HashF64(writer, rule.WaitingScheduledUt); HashI64(writer, rule.WaitingCoalescedSlots); HashText(writer, rule.ResourceName); HashI64(writer, rule.LowTriggerMicroUnits); HashI64(writer, rule.TargetMicroUnits); HashI64(writer, rule.BatchSizeMicroUnits);
                return Hash(stream);
            }
        }

        public static string RuleCancelPayloadHash(string ruleId)
        {
            if (String.IsNullOrWhiteSpace(ruleId) || ruleId.Length > 128) throw new ArgumentException("Invalid automatic order ID.", nameof(ruleId));
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), true))
            { HashText(writer, "ruleCancel"); HashText(writer, ruleId); return Hash(stream); }
        }

        static void HashText(BinaryWriter w, string value) { var bytes = new UTF8Encoding(false, true).GetBytes(value ?? throw new ArgumentNullException(nameof(value))); w.Write((byte)3); w.Write(bytes.Length); w.Write(bytes); }
        static void HashI32(BinaryWriter w, int value) { w.Write((byte)1); w.Write(value); }
        static void HashI64(BinaryWriter w, long value) { w.Write((byte)2); w.Write(value); }
        static void HashF64(BinaryWriter w, double value) { if (Double.IsNaN(value) || Double.IsInfinity(value)) throw new ArgumentException("Hash cannot encode a non-finite double."); w.Write((byte)5); w.Write(BitConverter.DoubleToInt64Bits(value)); }
        static void HashBool(BinaryWriter w, bool value) { w.Write((byte)6); w.Write(value); }
        static string Hash(MemoryStream stream)
        {
            using (var sha = SHA256.Create()) { var hash = sha.ComputeHash(stream.ToArray()); var b = new StringBuilder(hash.Length * 2); foreach (var x in hash) b.Append(x.ToString("x2", CultureInfo.InvariantCulture)); return b.ToString(); }
        }

        public static string CreateCheckpointId(string parentCheckpointId, string expectedStateHash, string operationId)
        {
            if (String.IsNullOrWhiteSpace(parentCheckpointId) || String.IsNullOrWhiteSpace(expectedStateHash) || String.IsNullOrWhiteSpace(operationId)) throw new ArgumentException("Checkpoint identity fields are invalid.");
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), true)) using (var sha = SHA256.Create())
            {
                writer.Write((byte)1); var p = Encoding.UTF8.GetBytes(parentCheckpointId); writer.Write(p.Length); writer.Write(p);
                writer.Write((byte)2); var h = Encoding.UTF8.GetBytes(expectedStateHash); writer.Write(h.Length); writer.Write(h);
                writer.Write((byte)3); var op = Encoding.UTF8.GetBytes(operationId); writer.Write(op.Length); writer.Write(op); writer.Flush();
                var hash = sha.ComputeHash(stream.ToArray()); var b = new StringBuilder(67); b.Append("cp1-"); foreach (var x in hash) b.Append(x.ToString("x2", CultureInfo.InvariantCulture)); return b.ToString();
            }
        }
    }
}

