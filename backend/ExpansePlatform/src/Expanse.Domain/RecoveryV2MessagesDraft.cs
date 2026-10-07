using System;

namespace Expanse.Domain
{
    /// <summary>Typed v2 command alternatives. Kept separate until the frozen v1 wire gate is complete.</summary>
    public sealed class DeliveryCommandPayload
    {
        public string Kind { get; set; } = ""; // routeUpsert | ruleUpsert | ruleCancel | sendOnce
        public RouteVersionRecord? RouteVersion { get; set; }
        public DeliveryRuleRecord? Rule { get; set; }
        public string RouteId { get; set; } = "";
        public long RouteVersionNumber { get; set; }
        public string RuleId { get; set; } = "";
    }

    /// <summary>Typed deterministic reducer payload attached to one Host-prepared effect proposal.</summary>
    public sealed class DeliveryEffectPayload
    {
        public EconomicRecoveryIntent? RecoveryIntent { get; set; }
        public string Kind { get; set; } = ""; // syncDepots | routeUpsert | ruleUpsert | ruleCancel | dispatch | arrival
        public RouteVersionRecord? RouteVersion { get; set; }
        public DeliveryRuleRecord? Rule { get; set; }
        public string RuleId { get; set; } = "";
        public ActiveShipmentRecord? Shipment { get; set; }
        /// <summary>Present only for Host-scheduled dispatches; applied atomically with cargo and physical debit.</summary>
        public DeliveryRuleRecord? ScheduleRuleUpdate { get; set; }
        public string ShipmentId { get; set; } = "";
        public ResourceAmount[] Credits { get; set; } = Array.Empty<ResourceAmount>();
        public ResourceAmount[] RemainingCargo { get; set; } = Array.Empty<ResourceAmount>();
        public PhysicalEffectIntent? PhysicalEffect { get; set; }
        public DepotRegistrySnapshot? DepotRegistrySnapshot { get; set; }
    }

    /// <summary>Game-thread receipt evidence. The Host must verify the full accepted capsule and the exact delta rows.</summary>
    public sealed class PhysicalEffectResult
    {
        public string Status { get; set; } = ""; // applied | rollbackConfirmed | uncertain
        public string RollbackStatus { get; set; } = "none"; // none | confirmed | failed | unknown
        public PhysicalResourceDelta[] Deltas { get; set; } = Array.Empty<PhysicalResourceDelta>();
        public string Reason { get; set; } = "";
    }
}
