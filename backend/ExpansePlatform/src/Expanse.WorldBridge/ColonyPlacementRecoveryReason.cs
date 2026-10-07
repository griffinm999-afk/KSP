using System;

namespace Expanse.WorldBridge
{
    // Text only. Reconcile still chooses the same hold; no marker, retry,
    // assembly, payment, escrow or physical authority changes here.
    internal static class ColonyPlacementRecoveryReason
    {
        internal static string Truthful(ColonyPlacementStatus status, int markerVessels, string existing)
        {
            if (status == null || markerVessels != 0 || status.Stage != ColonyPlacementStage.RecoveryHold || status.AssemblyAttempted ||
                !string.IsNullOrEmpty(status.VesselId) || !string.IsNullOrEmpty(status.FoundationId) || status.CreatedUt != 0 || status.AnchoredUt != 0 ||
                existing != "Creation may have occurred but its marked building is absent; do not respawn or refund") return existing;
            if (!string.IsNullOrEmpty(status.Reason) && status.Reason.StartsWith("Pre-assembly validation failed; no registered building created: ", StringComparison.Ordinal))
                return status.Reason; // Retain the actual recorded validation failure.
            return "Saved placement records no assembly attempt and no marked building; remains held for explicit same-terms reconciliation. No respawn or refund was attempted.";
        }
    }
}
