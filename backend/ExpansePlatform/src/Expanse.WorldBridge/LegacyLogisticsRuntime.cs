using System;
using System.Linq;
using System.Threading;
using Expanse.Domain;

namespace Expanse.WorldBridge
{
    public sealed partial class WorldBridgeAddon
    {
        private string logisticsRuntimeScope;
        private double logisticsRuntimeUt = -1;
        private double logisticsRuntimeNextAt;
        private string logisticsCompactAfterHash;
        private string logisticsRuntimeStatus = "Waiting for a selected save.";
        private TransientShipmentHold[] logisticsArrivalHolds = new TransientShipmentHold[0];
        private long logisticsRuntimeEffects;
        private int logisticsDueArrivals;

        public string LogisticsRuntimeStatus { get { return logisticsRuntimeStatus; } }
        public long LogisticsRuntimeEffectCount { get { return logisticsRuntimeEffects; } }
        public int LogisticsRuntimeDueArrivals { get { return logisticsDueArrivals; } }

        // Runs solely on the game thread. One effect per cadence bounds event work.
        // Persistent receipts, rule revisions and cargo in the selected capsule own
        // progress; these fields are only throttle/diagnostic state and reset on load.
        private void TickLegacyLogisticsRuntime()
        {
            double now = clock.Elapsed.TotalSeconds;
            if (now < logisticsRuntimeNextAt) return;
            logisticsRuntimeNextAt = now + 0.5;
            if (!CanAcceptRegistryEffects || economicRecoveryApplying || Planetarium.Pause || (HighLogic.LoadedSceneIsFlight && FlightDriver.Pause)) return;
            if (ColonyRuntime.Current != null && ColonyRuntime.Current.HasUnresolvedExternalEffects)
            { logisticsRuntimeStatus = "A colony effect needs reconciliation before shared funds or physical inventory can change."; return; }
            lock (effectGate)
            {
                if (effectExchangeInFlight || pendingEffectProposal != null || pendingEffectReceipt != null) return;
                RecoveryCapsuleModule recovery = RecoveryCapsuleModule.Instance;
                DepotRegistryModule registry = DepotRegistryModule.Instance;
                double ut = Planetarium.GetUniversalTime();
                if (!SafeFinite(ut) || ut < 0 || recovery == null || registry == null) return;
                string scope = sessionId + "\n" + loadEpoch + "\n" + runId + "\n" + recovery.WorldId;
                if (scope != logisticsRuntimeScope)
                {
                    logisticsRuntimeScope = scope; logisticsRuntimeUt = -1; logisticsCompactAfterHash = null;
                    logisticsArrivalHolds = new TransientShipmentHold[0];
                }
                if (ut < logisticsRuntimeUt) { logisticsRuntimeStatus = "Universal time moved backwards; waiting for the selected-save load boundary."; return; }
                if (ut == logisticsRuntimeUt) return;
                logisticsRuntimeUt = ut;
                try
                {
                    // Update the same context used to fence external commands before
                    // building a locally owned proposal. No worker can start an exchange
                    // while this synchronous effect gate is held.
                    PublishEffectContext(true, CurrentScene(), HighLogic.SaveFolder, ut);
                    EffectContext current = Interlocked.CompareExchange(ref latestEffectContext, null, null);
                    if (current == null || !current.CanWrite) return;
                    AcceptedState state = AcceptedStateCodec.Deserialize(recovery.GetAcceptedStateBytes());
                    var context = new LogisticsRuntimeContext { SessionId = current.SessionId, LoadEpoch = current.LoadEpoch,
                        InstallNamespace = current.InstallNamespace, SaveFolder = current.SaveFolder, RunId = current.RunId,
                        EconomicRecoveryAvailable = EconomicRecoveryAvailable, Registry = current.RegistrySnapshot,
                        Capabilities = current.InventoryEndpoints, Stocks = current.InventoryObservations,
                        ReservedPhysicalTanks=ColonyRuntime.Current==null?new PhysicalTankReservation[0]:ColonyRuntime.Current.GetPhysicalTankReservations() };
                    LogisticsRuntimeDecision decision = LogisticsRuntimePlanner.Plan(state, context, ut);
                    EffectProposal proposal = logisticsCompactAfterHash == current.StateHash ? LogisticsRuntimePlanner.PlanCompaction(state, context) : decision.Proposal;
                    logisticsCompactAfterHash = null;
                    logisticsRuntimeStatus = decision.Reason;
                    logisticsArrivalHolds = decision.ArrivalHolds;
                    logisticsDueArrivals = decision.DueArrivals;
                    if (proposal == null) return;
                    EffectReceipt receipt = ApplyEffectProposal(proposal);
                    logisticsRuntimeStatus = receipt.Outcome + ": " + (receipt.Reason ?? decision.Reason);
                    if (receipt.Outcome == "accepted")
                    {
                        logisticsRuntimeEffects++;
                        registry.InvalidateEffectInventoryObservations();
                    }
                    else if (receipt.Reason != null && (receipt.Reason.IndexOf("36 KiB", StringComparison.OrdinalIgnoreCase) >= 0 || receipt.Reason.IndexOf("receipt capacity", StringComparison.OrdinalIgnoreCase) >= 0))
                        logisticsCompactAfterHash = recovery.StateHash;
                    PublishEffectContext(true, CurrentScene(), HighLogic.SaveFolder, ut);
                }
                catch (Exception ex) { logisticsRuntimeStatus = "Logistics held: " + BoundEffect(ex.Message, 192); }
            }
        }
    }
}
