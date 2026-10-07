using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;

namespace Expanse.Domain
{
    /// <summary>
    /// V2 extension DTOs staged separately from the frozen EXS1 types. They become capsule state
    /// only when the v1 runtime gate is frozen and the EXS2 codec integration is approved.
    /// </summary>
    public static class AcceptedStateV2Limits
    {
        public const int MaxRouteVersions = 16;
        public const int MaxRules = 16;
        public const int MaxActiveShipments = 32;
        public const int MaxFaultRecords = 8;
        public const int MaxManifestResources = 32;
        public const int MaxFaultResourceDeltas = 16;
        public const int MaxDepotRecords = 32;
    }

    /// <summary>An immutable route configuration. Editing creates a new version; existing references stay valid.</summary>
    public sealed class RouteVersionRecord
    {
        public string RouteId { get; set; } = "";
        public long Revision { get; set; }
        public long Version { get; set; }
        public string SourceDepotId { get; set; } = "";
        public long SourceMembershipRevision { get; set; }
        public string SourceMembershipHash { get; set; } = "";
        public string DestinationDepotId { get; set; } = "";
        public string DestinationKind { get; set; } = "physicalDepot";
        public long FundsPerUnit { get; set; }
        public long DestinationMembershipRevision { get; set; }
        public string DestinationMembershipHash { get; set; } = "";
        public double TravelDurationSeconds { get; set; }
        public string Provenance { get; set; } = "";
        public ResourceAmount[] Resources { get; set; } = Array.Empty<ResourceAmount>();
        public bool LegacyOpaque { get; set; }
    }

    /// <summary>A persisted repeat or keep-stock rule; one coalesced request is the maximum pending work per rule.</summary>
    public sealed class DeliveryRuleRecord
    {
        public string RuleId { get; set; } = "";
        public long Revision { get; set; }
        public string Kind { get; set; } = ""; // repeat | keepStock
        public string RouteId { get; set; } = "";
        public long RouteVersion { get; set; }
        public bool Enabled { get; set; }
        public double NextDueUt { get; set; }
        public double IntervalSeconds { get; set; }
        public bool WaitingRequest { get; set; }
        // While WaitingRequest is true these describe its first missed slot/count; after an accepted repeat
        // dispatch they retain the last scheduled slot and the number of slots coalesced into that dispatch.
        public double WaitingScheduledUt { get; set; }
        public long WaitingCoalescedSlots { get; set; }
        public string ResourceName { get; set; } = "";
        public long LowTriggerMicroUnits { get; set; }
        public long TargetMicroUnits { get; set; }
        public long BatchSizeMicroUnits { get; set; }
        public bool LegacyOpaque { get; set; }
    }

    /// <summary>Only active cargo is stored; each arrival subtracts exactly the credited amount.</summary>
    public sealed class ActiveShipmentRecord
    {
        public string ShipmentId { get; set; } = "";
        public long Revision { get; set; }
        public string RouteId { get; set; } = "";
        public long RouteVersion { get; set; }
        public string SourceDepotId { get; set; } = "";
        public string DestinationDepotId { get; set; } = "";
        public string DestinationKind { get; set; } = "physicalDepot";
        public long FundsPerUnit { get; set; }
        public double DepartureUt { get; set; }
        public double DueUt { get; set; }
        public ResourceAmount[] RemainingResources { get; set; } = Array.Empty<ResourceAmount>();
        public string HeldReason { get; set; } = "";
        public bool LegacyOpaque { get; set; }
    }

    /// <summary>
    /// Terminal evidence for an uncertain physical mutation. Only failed/unknown rollback is recorded;
    /// an exactly confirmed rollback leaves the prior accepted capsule untouched and rejects the operation.
    /// Any record blocks further writes to its depot until explicitly resolved by a later reviewed operation.
    /// </summary>
    public sealed class PhysicalFaultRecord
    {
        public string FaultId { get; set; } = "";
        public string OperationId { get; set; } = "";
        public string OperationKind { get; set; } = "";
        public long CommandSequence { get; set; }
        public string DepotId { get; set; } = "";
        public long MembershipRevision { get; set; }
        public string MembershipHash { get; set; } = "";
        public string Reason { get; set; } = "";
        public string RollbackStatus { get; set; } = "unknown"; // failed | unknown
        public PhysicalResourceDelta[] Deltas { get; set; } = Array.Empty<PhysicalResourceDelta>();
    }

    /// <summary>Exact KSP observations are stored as IEEE-754 values alongside the exact intended micro-unit delta.</summary>
    public sealed class PhysicalResourceDelta
    {
        public uint MemberPersistentId { get; set; }
        public string ResourceName { get; set; } = "";
        public double BeforeAmount { get; set; }
        public long IntendedDeltaMicroUnits { get; set; }
        public double IntendedAfterAmount { get; set; }
        public bool ObservedAfterKnown { get; set; }
        public double ObservedAfterAmount { get; set; }
        public bool RollbackObserved { get; set; }
        public double RollbackObservedAmount { get; set; }
    }

    /// <summary>Typed capability evidence for one endpoint; absence/unsafe status is a hold, never implicit support.</summary>
    public sealed class InventoryCapability
    {
        public string DepotId { get; set; } = "";
        public long MembershipRevision { get; set; }
        public string MembershipHash { get; set; } = "";
        public uint AnchorPersistentId { get; set; }
        public string MemberSetHash { get; set; } = "";
        public string ProviderId { get; set; } = "";
        public string ProviderVersion { get; set; } = "";
        public string Scene { get; set; } = "";
        public bool ObservationAvailable { get; set; }
        public bool ReadSupported { get; set; }
        public bool WriteSupported { get; set; }
        public bool SynchronousRollbackSupported { get; set; }
        public bool PersistenceSyncSupported { get; set; }
        public string HoldReason { get; set; } = "";
    }

    /// <summary>Bridge-authored registry snapshot, captured on the fenced game thread.</summary>
    public sealed class DepotRegistrySnapshot
    {
        public string RegistryVersion { get; set; } = "";
        public string RegistryHash { get; set; } = "";
        public DepotRecord[] Depots { get; set; } = Array.Empty<DepotRecord>();
    }

    /// <summary>A bounded physical intent is a proposal only. This contract does not activate any writer.</summary>
    public sealed class PhysicalEffectIntent
    {
        public string EffectKind { get; set; } = ""; // dispatchDebit | arrivalCredit
        public InventoryCapability Capability { get; set; } = new InventoryCapability();
        public uint[] MemberPersistentIds { get; set; } = Array.Empty<uint>();
        public long MembershipRevision { get; set; }
        public PhysicalEffectResourceDelta[] Deltas { get; set; } = Array.Empty<PhysicalEffectResourceDelta>();
    }

    public sealed class PhysicalEffectResourceDelta
    {
        public uint MemberPersistentId { get; set; }
        public string ResourceName { get; set; } = "";
        public long DeltaMicroUnits { get; set; } // negative debit, positive credit
    }

    /// <summary>
    /// Structural validator for the additive V2 collections. Canonical encoding and transitions are implemented
    /// only after the frozen EXS1 runtime gate; this validator is safe to use in standalone synthetic tests.
    /// </summary>
    public static class AcceptedStateV2Draft
    {
        public static bool IsDepotWriteBlocked(PhysicalFaultRecord[] faults, string depotId)
            => faults != null && faults.Any(x => x != null && String.Equals(x.DepotId, depotId, StringComparison.Ordinal));

        /// <summary>Null means the endpoint has enough explicit capability for preflight; it does not authorize mutation.</summary>
        public static string? ValidatePhysicalIntent(PhysicalEffectIntent intent)
        {
            if (intent == null || (intent.EffectKind != "dispatchDebit" && intent.EffectKind != "arrivalCredit")) return "Physical effect kind is unsupported.";
            var capability = intent.Capability;
            if (intent.MemberPersistentIds == null || intent.MemberPersistentIds.Length == 0 || intent.MemberPersistentIds.Length > 64 || intent.MemberPersistentIds.Any(x => x == 0) || intent.MemberPersistentIds.Distinct().Count() != intent.MemberPersistentIds.Length) return "Physical effect member list is invalid.";
            if (capability == null || !Valid(capability.DepotId) || !Valid(capability.ProviderId) || !Valid(capability.ProviderVersion) || !Valid(capability.Scene) || capability.MembershipRevision < 0 || capability.MembershipRevision != intent.MembershipRevision || !ValidHash(capability.MembershipHash) || capability.AnchorPersistentId == 0 || capability.MemberSetHash != OperationIdentity.ComputeMemberSetHash(capability.AnchorPersistentId, intent.MemberPersistentIds))
                return "Inventory capability identity or membership revision is invalid.";
            if (!capability.ObservationAvailable || !capability.ReadSupported || !capability.WriteSupported || !capability.SynchronousRollbackSupported || !capability.PersistenceSyncSupported)
                return Valid(capability.HoldReason, 256) ? capability.HoldReason : "Required physical endpoint capability is unavailable.";
            if (intent.MemberPersistentIds == null || intent.MemberPersistentIds.Length == 0 || intent.MemberPersistentIds.Length > 64 || intent.Deltas == null || intent.Deltas.Length == 0 || intent.Deltas.Length > AcceptedStateV2Limits.MaxFaultResourceDeltas)
                return "Physical effect exceeds member or resource-delta bounds.";
            var members = new HashSet<uint>(); foreach (var id in intent.MemberPersistentIds) if (id == 0 || !members.Add(id)) return "Physical effect contains an invalid or duplicate member ID.";
            var deltaKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in intent.Deltas)
            {
                if (row == null || !members.Contains(row.MemberPersistentId) || !Valid(row.ResourceName) || row.DeltaMicroUnits == 0 || (intent.EffectKind == "dispatchDebit" && row.DeltaMicroUnits >= 0) || (intent.EffectKind == "arrivalCredit" && row.DeltaMicroUnits <= 0) || !deltaKeys.Add(row.MemberPersistentId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\0" + row.ResourceName))
                    return "Physical effect contains an invalid, duplicated or direction-inconsistent delta.";
            }
            return null;
        }

        public static void ValidatePhysicalResult(PhysicalEffectResult result)
        {
            if (result == null || (result.Status != "applied" && result.Status != "rollbackConfirmed" && result.Status != "uncertain") || result.Deltas == null || result.Deltas.Length > AcceptedStateV2Limits.MaxFaultResourceDeltas || result.Reason == null || result.Reason.Length > 256 || (result.Status == "uncertain" && String.IsNullOrWhiteSpace(result.Reason)))
                throw new InvalidDataException("Physical effect result identity/status is invalid.");
            if (result.Status == "applied" && result.RollbackStatus != "none") throw new InvalidDataException("Applied effect must not claim rollback.");
            if (result.Status == "rollbackConfirmed" && result.RollbackStatus != "confirmed") throw new InvalidDataException("Rollback result must explicitly confirm complete rollback.");
            if (result.Status == "uncertain" && (result.RollbackStatus != "failed" && result.RollbackStatus != "unknown" || result.Deltas.Length == 0)) throw new InvalidDataException("Uncertain effect must retain failed/unknown rollback evidence.");
            foreach (var row in result.Deltas)
                if (row == null || row.MemberPersistentId == 0 || !Valid(row.ResourceName) || row.IntendedDeltaMicroUnits == 0 || !FiniteNonNegative(row.BeforeAmount) || !FiniteNonNegative(row.IntendedAfterAmount) || !FiniteNonNegative(row.ObservedAfterAmount) || !FiniteNonNegative(row.RollbackObservedAmount))
                    throw new InvalidDataException("Physical effect result contains invalid exact observations.");
        }

        public static string? ValidatePhysicalSuccessWitness(PhysicalEffectIntent intent, PhysicalSuccessWitness witness)
        {
            if (intent == null || witness == null || intent.Capability == null || witness.ProviderId != intent.Capability.ProviderId || witness.ProviderVersion != intent.Capability.ProviderVersion || witness.Rows == null || witness.Rows.Length != intent.Deltas.Length || witness.Rows.Length == 0 || witness.Rows.Length > AcceptedStateV2Limits.MaxFaultResourceDeltas)
                return "Physical success witness provider or bounded row count differs from the prepared intent.";
            var deltas = intent.Deltas.OrderBy(x => x.MemberPersistentId).ThenBy(x => x.ResourceName, StringComparer.Ordinal).ToArray();
            var rows = witness.Rows;
            for (var i = 0; i < rows.Length; i++)
            {
                var row = rows[i]; var delta = deltas[i];
                if (row == null || row.MemberPersistentId != delta.MemberPersistentId || row.ResourceName != delta.ResourceName || !FiniteNonNegative(row.BeforeAmount) || !FiniteNonNegative(row.IntendedAfterAmount) || !FiniteNonNegative(row.ObservedAfterAmount))
                    return "Physical success witness row identity or amount is invalid.";
                var intended = row.BeforeAmount + delta.DeltaMicroUnits / 1_000_000d;
                if (!FiniteNonNegative(intended) || row.IntendedAfterAmount != intended || row.ObservedAfterAmount != intended)
                    return "Physical success witness does not record exact prepared intent and confirmed readback equality.";
            }
            return null;
        }

        public static void Validate(AcceptedState legacyState, RouteVersionRecord[] routes, DeliveryRuleRecord[] rules, ActiveShipmentRecord[] shipments, PhysicalFaultRecord[] faults)
        {
            AcceptedStateCodec.Validate(legacyState);
            ValidateCollections(legacyState, routes, rules, shipments, faults);
        }

        public static void ValidateCollections(AcceptedState state, RouteVersionRecord[] routes, DeliveryRuleRecord[] rules, ActiveShipmentRecord[] shipments, PhysicalFaultRecord[] faults)
        {
            if (routes == null || rules == null || shipments == null || faults == null || routes.Length > AcceptedStateV2Limits.MaxRouteVersions || rules.Length > AcceptedStateV2Limits.MaxRules || shipments.Length > AcceptedStateV2Limits.MaxActiveShipments || faults.Length > AcceptedStateV2Limits.MaxFaultRecords)
                throw new InvalidDataException("V2 accepted collections exceed configured bounds.");

            var depots = state.Depots.ToDictionary(x => x.DepotId + "\0" + x.MembershipRevision.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\0" + x.MembershipHash, StringComparer.Ordinal);
            var routeVersions = new HashSet<string>(StringComparer.Ordinal);
            foreach (var route in routes)
            {
                if (route == null || !Valid(route.RouteId) || route.Revision < 0 || route.Version < 0 || (!route.LegacyOpaque && route.Version == 0))
                    throw new InvalidDataException("V2 route identity/version is missing or duplicated.");
                if (route.LegacyOpaque)
                {
                    if (!routeVersions.Add(route.RouteId + "\0legacy") || route.SourceDepotId.Length != 0 || route.DestinationDepotId.Length != 0 || route.Resources == null || route.Resources.Length != 0) throw new InvalidDataException("Opaque legacy route contains invented or duplicate configuration.");
                    continue;
                }
                if (!routeVersions.Add(route.RouteId + "\0" + route.Version.ToString(System.Globalization.CultureInfo.InvariantCulture))) throw new InvalidDataException("V2 route identity/version is duplicated.");
                if (!Valid(route.SourceDepotId) || !Valid(route.DestinationDepotId) || route.SourceDepotId == route.DestinationDepotId || route.SourceMembershipRevision < 0 || route.DestinationMembershipRevision < 0 || !ValidHash(route.SourceMembershipHash) || (!OreExportPolicy.IsVirtual(route) && !ValidHash(route.DestinationMembershipHash)) || !depots.ContainsKey(route.SourceDepotId + "\0" + route.SourceMembershipRevision.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\0" + route.SourceMembershipHash) || (!OreExportPolicy.IsVirtual(route) && !depots.ContainsKey(route.DestinationDepotId + "\0" + route.DestinationMembershipRevision.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\0" + route.DestinationMembershipHash)))
                    throw new InvalidDataException("Route endpoints must name retained depot membership revisions.");
                if (!FinitePositive(route.TravelDurationSeconds) || route.TravelDurationSeconds > 1e15 || !Valid(route.Provenance, 256) || route.Resources == null || route.Resources.Length == 0 || route.Resources.Length > AcceptedStateV2Limits.MaxManifestResources)
                    throw new InvalidDataException("Route duration, provenance or manifest is invalid.");
                ValidateResources(route.Resources, "Route manifest");
                if (route.DestinationKind != "physicalDepot" && !OreExportPolicy.ValidBuyer(route) || route.DestinationKind == "physicalDepot" && route.FundsPerUnit != 0) throw new InvalidDataException("Route buyer economics are invalid.");
            }

            var ruleIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rule in rules)
            {
                if (rule == null || !Valid(rule.RuleId) || !ruleIds.Add(rule.RuleId) || rule.Revision < 0) throw new InvalidDataException("V2 rule identity/revision is invalid.");
                if (rule.LegacyOpaque) continue;
                if ((rule.Kind != "repeat" && rule.Kind != "keepStock" && rule.Kind != "exportStock") || !routeVersions.Contains(rule.RouteId + "\0" + rule.RouteVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                    throw new InvalidDataException("Rule kind or route-version reference is invalid.");
                if (rule.Kind == "keepStock" && OreExportPolicy.IsVirtual(routes.Single(x => x.RouteId == rule.RouteId && x.Version == rule.RouteVersion))) throw new InvalidDataException("Keep-stock requires a physical destination depot.");
                if (!FiniteNonNegative(rule.NextDueUt) || !FiniteNonNegative(rule.WaitingScheduledUt) || rule.WaitingCoalescedSlots < 0) throw new InvalidDataException("Rule schedule state is invalid.");
                if (rule.Kind == "repeat")
                {
                    if (!FinitePositive(rule.IntervalSeconds) || rule.IntervalSeconds > 1e15 || rule.ResourceName.Length != 0 || rule.LowTriggerMicroUnits != 0 || rule.TargetMicroUnits != 0 || rule.BatchSizeMicroUnits != 0) throw new InvalidDataException("Repeat rule fields are invalid.");
                }
                else if (rule.Kind == "exportStock")
                {
                    var buyer = routes.Single(x => x.RouteId == rule.RouteId && x.Version == rule.RouteVersion);
                    if (!OreExportPolicy.ValidBuyer(buyer) || rule.ResourceName != "Ore" || rule.BatchSizeMicroUnits != buyer.Resources[0].AmountMicroUnits || rule.TargetMicroUnits < 0 || rule.TargetMicroUnits > Int64.MaxValue - rule.BatchSizeMicroUnits || rule.LowTriggerMicroUnits != 0 || rule.IntervalSeconds != 0 || rule.WaitingRequest || rule.NextDueUt != 0 || rule.WaitingScheduledUt != 0 || rule.WaitingCoalescedSlots != 0) throw new InvalidDataException("Export-stock rule batch, source reserve or buyer is invalid.");
                }
                else if (!Valid(rule.ResourceName) || rule.LowTriggerMicroUnits < 0 || rule.TargetMicroUnits <= 0 || rule.TargetMicroUnits < rule.LowTriggerMicroUnits || rule.BatchSizeMicroUnits <= 0 || rule.IntervalSeconds != 0 || rule.WaitingRequest || rule.WaitingCoalescedSlots != 0)
                    throw new InvalidDataException("Keep-stock rule fields are invalid.");
            }

            var shipmentIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var shipment in shipments)
            {
                if (shipment == null || !Valid(shipment.ShipmentId) || !shipmentIds.Add(shipment.ShipmentId) || shipment.Revision < 0) throw new InvalidDataException("V2 shipment identity/revision is invalid.");
                if (shipment.LegacyOpaque) continue;
                if (!routeVersions.Contains(shipment.RouteId + "\0" + shipment.RouteVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)) || !Valid(shipment.SourceDepotId) || !Valid(shipment.DestinationDepotId)) throw new InvalidDataException("Shipment route reference is invalid.");
                var route = routes.Single(x => x.RouteId == shipment.RouteId && x.Version == shipment.RouteVersion);
                if (route.LegacyOpaque || route.SourceDepotId != shipment.SourceDepotId || route.DestinationDepotId != shipment.DestinationDepotId || route.DestinationKind != shipment.DestinationKind || route.FundsPerUnit != shipment.FundsPerUnit) throw new InvalidDataException("Shipment endpoints disagree with its immutable route version.");
                if (!FiniteNonNegative(shipment.DepartureUt) || !FiniteNonNegative(shipment.DueUt) || shipment.DueUt <= shipment.DepartureUt || shipment.RemainingResources == null || shipment.RemainingResources.Length == 0 || shipment.RemainingResources.Length > AcceptedStateV2Limits.MaxManifestResources)
                    throw new InvalidDataException("Active shipment timing or cargo is invalid.");
                ValidateResources(shipment.RemainingResources, "Shipment cargo");
                if (OreExportPolicy.IsVirtual(route) && (!OreExportPolicy.IsExactManifest(shipment.RemainingResources) || shipment.RemainingResources[0].AmountMicroUnits != route.Resources[0].AmountMicroUnits)) throw new InvalidDataException("Virtual recovery cargo must remain an exact Ore batch.");
                if (shipment.HeldReason.Length > 256) throw new InvalidDataException("Shipment hold reason exceeds bound.");
            }

            var faultIds = new HashSet<string>(StringComparer.Ordinal); var faultOps = new HashSet<string>(StringComparer.Ordinal);
            foreach (var fault in faults)
            {
                if (fault == null || !Valid(fault.FaultId) || !Valid(fault.OperationId) || (fault.OperationKind != "dispatch" && fault.OperationKind != "arrival") || fault.CommandSequence <= 0 || !depots.ContainsKey(fault.DepotId + "\0" + fault.MembershipRevision.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\0" + fault.MembershipHash) || fault.MembershipRevision < 0 || !Valid(fault.Reason, 256) || (fault.RollbackStatus != "failed" && fault.RollbackStatus != "unknown") || !faultIds.Add(fault.FaultId) || !faultOps.Add(fault.OperationId))
                    throw new InvalidDataException("Physical fault identity, endpoint, or rollback status is invalid.");
                if (fault.Deltas == null || fault.Deltas.Length == 0 || fault.Deltas.Length > AcceptedStateV2Limits.MaxFaultResourceDeltas) throw new InvalidDataException("Physical fault must retain a bounded nonempty delta list.");
                var deltaKeys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var delta in fault.Deltas)
                {
                    if (delta == null || delta.MemberPersistentId == 0 || !Valid(delta.ResourceName) || delta.IntendedDeltaMicroUnits == 0 || !FiniteNonNegative(delta.BeforeAmount) || !FiniteNonNegative(delta.IntendedAfterAmount) || !FiniteNonNegative(delta.ObservedAfterAmount) || !FiniteNonNegative(delta.RollbackObservedAmount) || !deltaKeys.Add(delta.MemberPersistentId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\0" + delta.ResourceName))
                        throw new InvalidDataException("Physical fault delta is invalid or duplicated.");
                }
            }
        }

        static void ValidateResources(ResourceAmount[] rows, string label)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows) if (row == null || !Valid(row.ResourceName) || row.AmountMicroUnits <= 0 || !names.Add(row.ResourceName)) throw new InvalidDataException(label + " has missing, duplicate or nonpositive rows.");
        }
        static bool Valid(string? value, int max = 128) => !String.IsNullOrWhiteSpace(value) && value!.Length <= max;
        static bool ValidHash(string? value) => value != null && value.Length == 64 && value.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
        static bool FinitePositive(double value) => value > 0 && !Double.IsNaN(value) && !Double.IsInfinity(value);
        static bool FiniteNonNegative(double value) => value >= 0 && !Double.IsNaN(value) && !Double.IsInfinity(value);
    }
}
