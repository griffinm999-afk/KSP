using System;

namespace Expanse.Domain
{
    public class EffectEnvelope { public int ProtocolVersion { get; set; } = 1; public string MessageType { get; set; } = ""; }

    public sealed class EffectAttach : EffectEnvelope
    {
        public Guid SessionId { get; set; } public Guid LoadEpoch { get; set; }
        public int BridgeProcessId { get; set; } public long BridgeProcessStartUtcTicks { get; set; } public string BridgeExecutablePath { get; set; } = "";
        public string InstallNamespace { get; set; } = ""; public string SaveFolder { get; set; } = "";
        public string WorldId { get; set; } = ""; public string RunId { get; set; } = ""; public string CheckpointId { get; set; } = "";
        public long Revision { get; set; } public long AcceptedSequence { get; set; } public long CompactionWatermark { get; set; }
        public string StateHash { get; set; } = ""; public bool CanWrite { get; set; } public string? UnavailableReason { get; set; }
        public string[] Capabilities { get; set; } = Array.Empty<string>();
        public InventoryCapability[] InventoryEndpoints { get; set; } = Array.Empty<InventoryCapability>();
        public DepotRegistrySnapshot? RegistrySnapshot { get; set; }
        public RecoveryCapsule? Capsule { get; set; }
    }

    public sealed class EffectPoll : EffectEnvelope
    {
        public Guid SessionId { get; set; } public Guid LoadEpoch { get; set; }
        public string InstallNamespace { get; set; } = ""; public string SaveFolder { get; set; } = "";
        public string WorldId { get; set; } = ""; public string RunId { get; set; } = ""; public string CheckpointId { get; set; } = "";
        public long Revision { get; set; } public long AcceptedSequence { get; set; } public long CompactionWatermark { get; set; }
        public string StateHash { get; set; } = "";
        public StockObservation[] InventoryObservations { get; set; } = Array.Empty<StockObservation>();
        public TransientShipmentHold[] RuntimeShipmentHolds { get; set; } = Array.Empty<TransientShipmentHold>();
    }

    public sealed class EffectProposal : EffectEnvelope
    {
        public Guid SessionId { get; set; } public Guid LoadEpoch { get; set; }
        public string InstallNamespace { get; set; } = ""; public string SaveFolder { get; set; } = "";
        public string WorldId { get; set; } = ""; public string RunId { get; set; } = "";
        public string OperationId { get; set; } = ""; public string ClientRequestId { get; set; } = "";
        public long CommandSequence { get; set; } public long ExpectedRevision { get; set; }
        public string ExpectedStateHash { get; set; } = ""; public string PayloadHash { get; set; } = "";
        public string OperationKind { get; set; } = "counterIncrement"; public long CounterDelta { get; set; }
        public long TargetCompactionWatermark { get; set; }
        public DeliveryEffectPayload? Delivery { get; set; }
        public PhysicalEffectIntent? PhysicalEffect { get; set; }
    }

    public sealed class EffectReceipt : EffectEnvelope
    {
        public Guid SessionId { get; set; } public Guid LoadEpoch { get; set; }
        public string InstallNamespace { get; set; } = ""; public string SaveFolder { get; set; } = "";
        public string WorldId { get; set; } = ""; public string RunId { get; set; } = "";
        public string OperationId { get; set; } = ""; public string ClientRequestId { get; set; } = "";
        public long CommandSequence { get; set; } public string PayloadHash { get; set; } = "";
        public string OperationKind { get; set; } = "counterIncrement"; public long TargetCompactionWatermark { get; set; }
        public string Outcome { get; set; } = ""; public double AppliedUt { get; set; } public long ActualCounterDelta { get; set; }
        public long AcceptedRevision { get; set; } public long AcceptedSequence { get; set; } public string StateHash { get; set; } = "";
        public RecoveryCapsule? AcceptedCapsule { get; set; } public string? Reason { get; set; }
        public PhysicalEffectResult? PhysicalResult { get; set; }
        public EconomicEffectResult? EconomicResult { get; set; }
    }

    public sealed class EffectIdle : EffectEnvelope { public string Reason { get; set; } = ""; }
    public sealed class EffectNeedAttach : EffectEnvelope { public string Reason { get; set; } = ""; }

    public sealed class SubmitCommand : EffectEnvelope
    {
        public string ClientRequestId { get; set; } = ""; public string WorldId { get; set; } = ""; public string RunId { get; set; } = "";
        public string CommandKind { get; set; } = "counterIncrement"; public long CounterDelta { get; set; }
        public DeliveryCommandPayload? Delivery { get; set; }
    }
    public sealed class SubmitCommandResult : EffectEnvelope
    {
        public string ClientRequestId { get; set; } = ""; public string Status { get; set; } = "rejected";
        // A rejected response may only release a retry identity when the Host has
        // identified its durable operation as terminal (for example, a held row).
        public bool ConfirmedTerminal { get; set; }
        public string? OperationId { get; set; } public string? Reason { get; set; }
        public string? AcceptedOutcome { get; set; } public long? AcceptedRevision { get; set; } public long? AcceptedSequence { get; set; } public string? StateHash { get; set; } public RecoveryCapsule? AcceptedCapsule { get; set; }
    }
    public sealed class GetCommandStatus : EffectEnvelope { public string ClientRequestId { get; set; } = ""; public string WorldId { get; set; } = ""; public string RunId { get; set; } = ""; }
    public sealed class GetAcceptedState : EffectEnvelope { public string WorldId { get; set; } = ""; public string RunId { get; set; } = ""; }
    public sealed class TransientShipmentHold
    {
        public string ShipmentId { get; set; } = "";
        public string Reason { get; set; } = "";
    }
    public sealed class GetDeliveryReadiness : EffectEnvelope
    {
        public string WorldId { get; set; } = ""; public string RunId { get; set; } = "";
        public string RouteId { get; set; } = ""; public long RouteVersion { get; set; }
    }
    public sealed class DeliveryReadinessResult : EffectEnvelope
    {
        public string Status { get; set; } = "held"; public string WorldId { get; set; } = ""; public string RunId { get; set; } = "";
        public string RouteId { get; set; } = ""; public long RouteVersion { get; set; }
        public string Reason { get; set; } = "Delivery capability is unavailable.";
        public string SourceProviderId { get; set; } = ""; public string DestinationProviderId { get; set; } = "";
        public long? AcceptedSequence { get; set; }
    }
    public sealed class AcceptedStateQueryResult : EffectEnvelope
    {
        public string Status { get; set; } = "unavailable"; public string WorldId { get; set; } = ""; public string RunId { get; set; } = ""; public string? Reason { get; set; }
        public long? Revision { get; set; } public long? AcceptedSequence { get; set; } public string? StateHash { get; set; } public RecoveryCapsule? AcceptedCapsule { get; set; }
        // Poll-scoped delivery status; it is not part of the accepted save state.
        public TransientShipmentHold[] TransientShipmentHolds { get; set; } = Array.Empty<TransientShipmentHold>();
    }
}
