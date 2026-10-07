using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using Expanse.Domain;

namespace Expanse.WorldBridge
{
    public sealed partial class WorldBridgeAddon
    {
        private sealed class EffectContext
        {
            public Guid SessionId, LoadEpoch;
            public int BridgeProcessId;
            public long BridgeProcessStartUtcTicks;
            public string BridgeExecutablePath;
            public string InstallNamespace, SaveFolder, WorldId, RunId, CheckpointId, StateHash, UnavailableReason;
            public long Revision, Sequence, Watermark;
            public bool CanWrite;
            public bool EconomicRecoveryReady;
            public RecoveryCapsule Capsule;
            public DepotRegistrySnapshot RegistrySnapshot;
            public InventoryCapability[] InventoryEndpoints;
            public StockObservation[] InventoryObservations;
            public TransientShipmentHold[] RuntimeShipmentHolds;
            public string AttachmentKey;
        }

        private readonly object effectGate = new object();
        private readonly BrpInventoryGateway physicalGateway = new BrpInventoryGateway();
        private readonly RemoteBrpInventoryGateway remotePhysicalGateway = new RemoteBrpInventoryGateway();
        private readonly LoadedBrpInventoryGateway loadedPhysicalGateway = new LoadedBrpInventoryGateway();
        private bool effectExchangeInFlight;
        private string endpointCapabilityCacheKey;
        private float endpointCapabilityCapturedAt;
        private InventoryCapability[] endpointCapabilityCache;
        private readonly AutoResetEvent effectWakeWorker = new AutoResetEvent(false);
        private volatile EffectContext latestEffectContext;
        private EffectProposal pendingEffectProposal;
        private EffectReceipt pendingEffectReceipt;
        private Thread effectWorker;
        private NamedPipeClientStream activeEffectPipe;
        private bool reportedEffectPipeError;
        private long lastEffectPipeErrorAt;
        private string lastNeedAttachDiagnostic;
        private long lastNeedAttachLoggedAt;

        // Colony and delivery effects share the same career funds and physical
        // resources. A saved uncertain delivery must fence new colony writes.
        public bool LegacyAuthorityWriteHeld
        {
            get
            {
                var capsule = RecoveryCapsuleModule.Instance;
                if (loadUnresolved || !HighLogic.LoadedSceneIsGame || HighLogic.CurrentGame == null ||
                    capsule == null || capsule.AuthorityWriteHeld || economicRecoveryApplying) return true;
                lock (effectGate) return effectExchangeInFlight || pendingEffectProposal != null;
            }
        }

        private void StartEffectsWorker()
        {
            effectWorker = new Thread(EffectsPipeWorker) { IsBackground = true, Name = "Expanse recovery effects client" };
            effectWorker.Start();
        }

        private void StopEffectsWorker()
        {
            try { if (activeEffectPipe != null) activeEffectPipe.Dispose(); } catch { }
            try { effectWakeWorker.Set(); } catch { }
        }

        private void PublishEffectContext(bool activeWorld, string scene, string saveFolder, double? ut)
        {
            EffectContext context = new EffectContext
            {
                InstallNamespace = installNamespace ?? Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory),
                SaveFolder = activeWorld ? (saveFolder ?? HighLogic.SaveFolder ?? string.Empty) : string.Empty,
                SessionId = Guid.Parse(sessionId),
                LoadEpoch = Guid.Parse(loadEpoch),
                BridgeProcessId = bridgeProcessId,
                BridgeProcessStartUtcTicks = bridgeProcessStartUtcTicks,
                BridgeExecutablePath = bridgeExecutablePath ?? string.Empty,
                RunId = runId ?? string.Empty,
                WorldId = string.Empty,
                CheckpointId = string.Empty,
                StateHash = string.Empty,
                Revision = 0,
                Sequence = 0,
                Watermark = 0,
                CanWrite = false,
                EconomicRecoveryReady = EconomicRecoveryAvailable && !economicRecoveryApplying,
                UnavailableReason = null,
                Capsule = null,
                RegistrySnapshot = null,
                InventoryEndpoints = new InventoryCapability[0],
                InventoryObservations = new StockObservation[0]
            };
            context.RuntimeShipmentHolds = logisticsArrivalHolds;

            RecoveryCapsuleModule capsuleModule = RecoveryCapsuleModule.Instance;
            DepotRegistryModule registry = DepotRegistryModule.Instance;
            if (activeWorld && registry != null && registry.IsReady && !registry.IsCorrupt)
                context.RegistrySnapshot = registry.CreateEffectRegistrySnapshot();
            if (context.RegistrySnapshot != null) context.InventoryEndpoints = BuildInventoryEndpoints(context.RegistrySnapshot, scene);
            if (context.RegistrySnapshot != null && registry != null)
                context.InventoryObservations = registry.CloneInventoryObservations(sessionId, loadEpoch, activeWorld, context.RegistrySnapshot.RegistryHash, clock.Elapsed.TotalSeconds);
            if (activeWorld && capsuleModule != null && capsuleModule.HasAcceptedState && !capsuleModule.IsCorrupt)
            {
                context.WorldId = capsuleModule.WorldId;
                context.CheckpointId = capsuleModule.CheckpointId;
                context.Revision = capsuleModule.Revision;
                context.Sequence = capsuleModule.AcceptedSequence;
                context.Watermark = capsuleModule.CompactionWatermark;
                context.StateHash = capsuleModule.StateHash;
                context.Capsule = capsuleModule.GetCapsuleCopy();
                if (!string.Equals(registry == null ? null : registry.WorldId, capsuleModule.WorldId, StringComparison.OrdinalIgnoreCase))
                    context.UnavailableReason = "Recovery world identity does not match the loaded depot registry";
                else if (capsuleModule.IsCorrupt) context.UnavailableReason = capsuleModule.HoldReason ?? "Recovery capsule is corrupt";
                else if (loadUnresolved) context.UnavailableReason = "KSP load or revert is unresolved";
                else if (!HighLogic.LoadedSceneIsGame || HighLogic.CurrentGame == null || IsNoWorldScene(scene)) context.UnavailableReason = "No loaded KSP world";
                else if (!string.IsNullOrEmpty(bridgeProcessIdentityError)) context.UnavailableReason = bridgeProcessIdentityError;
                else if (string.IsNullOrWhiteSpace(context.SaveFolder)) context.UnavailableReason = "KSP save folder is unavailable";
                else context.CanWrite = CanAcceptRegistryEffects;
            }
            else if (activeWorld)
            {
                context.WorldId = registry == null ? string.Empty : registry.WorldId ?? string.Empty;
                context.UnavailableReason = context.UnavailableReason ?? (capsuleModule == null ? "Recovery Scenario module is missing" :
                    capsuleModule.IsCorrupt ? capsuleModule.HoldReason ?? "Recovery capsule is corrupt" : "Recovery state is not initialized");
            }
            else context.UnavailableReason = loadUnresolved ? "KSP load or revert is unresolved" : "No loaded KSP world";

            context.AttachmentKey = context.SessionId.ToString("D") + "\n" + context.LoadEpoch.ToString("D") + "\n" + context.InstallNamespace + "\n" + context.SaveFolder + "\n" + context.WorldId + "\n" + context.RunId + "\n" + context.CheckpointId + "\n" + context.Revision.ToString(CultureInfo.InvariantCulture) + "\n" + context.Sequence.ToString(CultureInfo.InvariantCulture) + "\n" + context.Watermark.ToString(CultureInfo.InvariantCulture) + "\n" + context.StateHash + "\n" + (context.RegistrySnapshot == null ? "" : context.RegistrySnapshot.RegistryHash) + "\n" + (context.CanWrite ? "1" : "0") + "\n" + (context.EconomicRecoveryReady ? "1" : "0") + "\n" + InventoryEndpointsFingerprint(context.InventoryEndpoints);
            Interlocked.Exchange(ref latestEffectContext, context);
            try { effectWakeWorker.Set(); } catch { }
        }

        private void ProcessPendingEffectProposal()
        {
            if (economicRecoveryApplying) return;
            EffectProposal proposal;
            lock (effectGate)
            {
                proposal = pendingEffectProposal;
                if (proposal == null) return;
            }

            EffectReceipt receipt;
            try { receipt = ApplyEffectProposal(proposal); }
            catch (Exception ex)
            {
                receipt = BuildRejectedReceipt(proposal, "Bridge rejected proposal safely: " + BoundEffect(ex.Message, 192));
            }

            lock (effectGate)
            {
                if (!object.ReferenceEquals(pendingEffectProposal, proposal)) return;
                pendingEffectReceipt = receipt;
                pendingEffectProposal = null;
            }
            PublishEffectContext(HighLogic.LoadedSceneIsGame && HighLogic.CurrentGame != null, CurrentScene(), HighLogic.SaveFolder, null);
            try { effectWakeWorker.Set(); } catch { }
        }

        private EffectReceipt ApplyEffectProposal(EffectProposal proposal)
        {
            EffectContext context = Interlocked.CompareExchange(ref latestEffectContext, null, null);
            AcceptedState state = null;
            RecoveryCapsuleModule recovery = RecoveryCapsuleModule.Instance;
            try
            {
                byte[] bytes = recovery == null ? null : recovery.GetAcceptedStateBytes();
                if (bytes != null) state = AcceptedStateCodec.Deserialize(bytes);
            }
            catch { }

            string hold;
            if (context == null || !context.CanWrite || !CanAcceptRegistryEffects) hold = context == null ? "Bridge context is unavailable" : context.UnavailableReason ?? "KSP is not ready for effects";
            else if (!string.Equals(proposal.InstallNamespace, installNamespace, StringComparison.OrdinalIgnoreCase) || !string.Equals(proposal.SaveFolder, HighLogic.SaveFolder, StringComparison.Ordinal)) hold = "Install or save context mismatch";
                else if (!string.Equals(proposal.RunId, runId, StringComparison.Ordinal) || state == null || !string.Equals(proposal.WorldId, state.WorldId, StringComparison.OrdinalIgnoreCase)) hold = "World or execution run fence mismatch";
            else if (proposal.SessionId != Guid.Parse(sessionId) || proposal.LoadEpoch != Guid.Parse(loadEpoch) ||
                     !string.Equals(lastPublishedClockSessionId, proposal.SessionId.ToString("D"), StringComparison.OrdinalIgnoreCase) ||
                     !string.Equals(lastPublishedClockLoadEpoch, proposal.LoadEpoch.ToString("D"), StringComparison.OrdinalIgnoreCase)) hold = "KSP session or load epoch does not match the current published clock context";
            else if (proposal.ProtocolVersion != 1 || proposal.MessageType != "effectProposal" || !IsSupportedEffectKind(proposal.OperationKind)) hold = "Unsupported effect proposal kind or protocol";
            else if (proposal.CommandSequence <= 0 || (proposal.OperationKind == "counterIncrement" && (proposal.CounterDelta <= 0 || proposal.TargetCompactionWatermark != 0)) ||
                     (proposal.OperationKind == "compact" && proposal.CounterDelta != 0)) hold = "Proposal quantity, kind or sequence is invalid";
            else if (proposal.OperationKind == "counterIncrement" && !devRecoveryFixtureEnabled) hold = "Counter effects require the explicit disposable development fixture";
            else if ((proposal.OperationKind == "dispatch" || proposal.OperationKind == "arrival") && !PhysicalCandidateEnabled && !RemotePhysicalCandidateEnabled) hold = "No supported physical inventory provider is available";
            else if (!string.Equals(proposal.OperationId, OperationIdentity.Create(proposal.WorldId, proposal.CommandSequence, proposal.ClientRequestId), StringComparison.Ordinal) ||
                     !string.Equals(proposal.PayloadHash, ExpectedPayloadHash(proposal), StringComparison.Ordinal)) hold = "Operation identity or payload hash mismatch";
            else hold = null;

            if (hold != null) return BuildRejectedReceipt(proposal, hold);

            // Resolve exact retained retries before checking their original expected prefix.
            for (int i = 0; i < state.Receipts.Length; i++)
            {
                AcceptedReceipt retained = state.Receipts[i];
                if (retained.CommandSequence == proposal.CommandSequence || string.Equals(retained.OperationId, proposal.OperationId, StringComparison.Ordinal))
                {
                    if (retained.CommandSequence == proposal.CommandSequence && string.Equals(retained.OperationId, proposal.OperationId, StringComparison.Ordinal) && string.Equals(retained.PayloadHash, proposal.PayloadHash, StringComparison.Ordinal) && retained.OperationKind == proposal.OperationKind && retained.CounterDelta == proposal.CounterDelta && retained.TargetCompactionWatermark == proposal.TargetCompactionWatermark)
                        return BuildReceipt(proposal, "duplicate", retained.AppliedUt, retained.CounterDelta, state, null);
                    return BuildRejectedReceipt(proposal, "Operation ID or sequence conflicts with an accepted receipt");
                }
            }
            if (proposal.CommandSequence <= state.CompactionWatermark)
                return BuildReceipt(proposal, "alreadySettledCompacted", Planetarium.GetUniversalTime(), 0, state, "Command sequence is at or below the compacted watermark");
            // Exact retained receipts remain readable above. No new writer may
            // change a shared provider while a colony mutation has an unknown result.
            if (ColonyRuntime.Current != null && ColonyRuntime.Current.HasUnresolvedExternalEffects)
                return BuildRejectedReceipt(proposal, "A colony effect needs reconciliation before shared funds or inventory can change.");
            if (proposal.ExpectedRevision != state.Revision || !string.Equals(proposal.ExpectedStateHash, AcceptedStateCodec.ComputeHash(state), StringComparison.Ordinal))
                return BuildRejectedReceipt(proposal, "Expected accepted revision or state hash is stale");

            // Historical receipts and departed cargo retain their immutable price.
            // A prepared command that has never applied must use current export terms.
            DeliveryEffectPayload pricedDelivery = proposal.Delivery;
            if (pricedDelivery != null)
            {
                RouteVersionRecord pricedRoute = proposal.OperationKind == "routeUpsert" ? pricedDelivery.RouteVersion : null;
                if (proposal.OperationKind == "ruleUpsert" && pricedDelivery.Rule != null && pricedDelivery.Rule.Enabled)
                    pricedRoute = state.RouteVersions.SingleOrDefault(r => r.RouteId == pricedDelivery.Rule.RouteId && r.Version == pricedDelivery.Rule.RouteVersion);
                if ((pricedRoute != null && pricedRoute.DestinationKind == OreExportPolicy.VirtualDestinationKind && pricedRoute.FundsPerUnit != OreExportPolicy.DefaultFundsPerUnit) ||
                    (proposal.OperationKind == "dispatch" && pricedDelivery.Shipment != null && pricedDelivery.Shipment.DestinationKind == OreExportPolicy.VirtualDestinationKind && pricedDelivery.Shipment.FundsPerUnit != OreExportPolicy.DefaultFundsPerUnit))
                    return BuildRejectedReceipt(proposal, "New Ore exports require current terms of 100 funds per unit; save updated route settings");
            }

            double appliedUt = Planetarium.GetUniversalTime();
            if (double.IsNaN(appliedUt) || double.IsInfinity(appliedUt) || appliedUt < 0) return BuildRejectedReceipt(proposal, "Universal time is unavailable or invalid");
            if (previousUt.HasValue && appliedUt < previousUt.Value)
            {
                StartNewEpoch();
                PublishEffectContext(HighLogic.LoadedSceneIsGame && HighLogic.CurrentGame != null, CurrentScene(), HighLogic.SaveFolder, null);
                return BuildRejectedReceipt(proposal, "Universal time moved backwards; a fresh Host reattachment is required");
            }
            AcceptedState nextState;
            string outcome = "accepted";
            string transitionReason = null;
            long actualCounterDelta = 0;
            if (proposal.OperationKind == "syncDepots")
            {
                DepotRegistryModule registry = DepotRegistryModule.Instance;
                DepotRegistrySnapshot fresh = registry == null ? null : registry.CreateEffectRegistrySnapshot();
                DepotRegistrySnapshot proposed = proposal.Delivery == null ? null : proposal.Delivery.DepotRegistrySnapshot;
                if (fresh == null || proposed == null || !SameRegistry(fresh, proposed)) return BuildRejectedReceipt(proposal, "Depot registry changed after the Host prepared this synchronization");
                StateTransitionResult transition = AcceptedStateCodec.SyncDepots(state, proposal.OperationId, proposal.ClientRequestId, proposal.CommandSequence, proposal.PayloadHash, proposal.ExpectedRevision, proposal.ExpectedStateHash, proposed, appliedUt);
                if (transition.Outcome != "accepted") return BuildReceipt(proposal, transition.Outcome, appliedUt, 0, state, transition.Reason);
                nextState = transition.State;
            }
            else if (proposal.OperationKind == "routeUpsert")
            {
                if (proposal.Delivery == null || proposal.Delivery.Kind != "routeUpsert" || proposal.Delivery.RouteVersion == null) return BuildRejectedReceipt(proposal, "Route payload is missing or mismatched");
                StateTransitionResult transition = AcceptedStateCodec.UpsertRoute(state, proposal.OperationId, proposal.ClientRequestId, proposal.CommandSequence, proposal.PayloadHash, proposal.ExpectedRevision, proposal.ExpectedStateHash, proposal.Delivery.RouteVersion, appliedUt);
                if (transition.Outcome != "accepted") return BuildReceipt(proposal, transition.Outcome, appliedUt, 0, state, transition.Reason);
                nextState = transition.State;
            }
            else if (proposal.OperationKind == "ruleUpsert")
            {
                if (proposal.Delivery == null || proposal.Delivery.Kind != "ruleUpsert" || proposal.Delivery.Rule == null) return BuildRejectedReceipt(proposal, "Rule payload is missing or mismatched");
                StateTransitionResult transition = AcceptedStateCodec.UpsertRule(state, proposal.OperationId, proposal.ClientRequestId, proposal.CommandSequence, proposal.PayloadHash, proposal.ExpectedRevision, proposal.ExpectedStateHash, proposal.Delivery.Rule, appliedUt);
                if (transition.Outcome != "accepted") return BuildReceipt(proposal, transition.Outcome, appliedUt, 0, state, transition.Reason);
                nextState = transition.State;
            }
            else if (proposal.OperationKind == "ruleCancel")
            {
                if (proposal.Delivery == null || proposal.Delivery.Kind != "ruleCancel") return BuildRejectedReceipt(proposal, "Rule cancellation payload is missing or mismatched");
                StateTransitionResult transition = AcceptedStateCodec.CancelRule(state, proposal.OperationId, proposal.ClientRequestId, proposal.CommandSequence, proposal.PayloadHash, proposal.ExpectedRevision, proposal.ExpectedStateHash, proposal.Delivery.RuleId, appliedUt);
                if (transition.Outcome != "accepted") return BuildReceipt(proposal, transition.Outcome, appliedUt, 0, state, transition.Reason);
                nextState = transition.State;
            }
            else if (proposal.OperationKind == "compact")
            {
                try { nextState = AcceptedStateCodec.Compact(state, proposal.OperationId, proposal.ClientRequestId, proposal.CommandSequence, proposal.PayloadHash, proposal.ExpectedRevision, proposal.ExpectedStateHash, proposal.TargetCompactionWatermark, appliedUt); }
                catch (Exception ex) { return BuildRejectedReceipt(proposal, "Compaction rejected: " + BoundEffect(ex.Message, 192)); }
            }
            else if (proposal.OperationKind == "dispatch" || proposal.OperationKind == "arrival")
            {
                return ApplyPhysicalProposal(proposal, state, recovery, appliedUt);
            }
            else if (proposal.OperationKind == "recoverySale")
            {
                return ApplyEconomicRecoveryProposal(proposal, state, recovery, appliedUt);
            }
            else
            {
                StateTransitionResult transition = AcceptedStateCodec.Increment(state, proposal.WorldId, proposal.OperationId, proposal.ClientRequestId, proposal.CommandSequence, proposal.PayloadHash, proposal.CounterDelta, appliedUt);
                outcome = transition.Outcome;
                transitionReason = transition.Reason;
                nextState = transition.State;
                actualCounterDelta = string.Equals(outcome, "accepted", StringComparison.Ordinal) ? proposal.CounterDelta : 0;
                if (!string.Equals(outcome, "accepted", StringComparison.Ordinal))
                    return BuildReceipt(proposal, outcome, appliedUt, 0, state, transitionReason);
            }
            byte[] encoded = AcceptedStateCodec.Serialize(nextState);
            string replaceReason = null;
            if (recovery == null || !recovery.TryReplaceAcceptedState(state.WorldId, encoded, out replaceReason))
                return BuildRejectedReceipt(proposal, replaceReason ?? "Recovery state could not be replaced");
            return BuildReceipt(proposal, "accepted", appliedUt, actualCounterDelta, nextState, null);
        }

        private EffectReceipt ApplyPhysicalProposal(EffectProposal proposal, AcceptedState prior, RecoveryCapsuleModule recovery, double appliedUt)
        {
            DeliveryEffectPayload delivery = proposal.Delivery;
            PhysicalEffectIntent intent = delivery == null ? null : delivery.PhysicalEffect;
            if (delivery == null || intent == null || (proposal.OperationKind == "dispatch" && (delivery.Kind != "dispatch" || delivery.Shipment == null)) ||
                (proposal.OperationKind == "arrival" && (delivery.Kind != "arrival" || String.IsNullOrWhiteSpace(delivery.ShipmentId) || delivery.ScheduleRuleUpdate != null)) ||
                AcceptedStateV2Draft.ValidatePhysicalIntent(intent) != null)
                return BuildRejectedReceipt(proposal, "Physical proposal is incomplete or failed typed intent validation");

            // Retained receipts are reconciled before reaching this new-effect path.
            // Do not let an independent delivery spend a tank claimed by colony work.
            var colony = ColonyRuntime.Current;
            if (colony != null && intent.Deltas.Any(d => colony.IsPhysicalTankReserved(intent.Capability.DepotId, d.MemberPersistentId, d.ResourceName)))
                return BuildRejectedReceipt(proposal, "A selected tank is reserved by a colony stock transfer or service operation.");

            DepotRegistryModule registry = DepotRegistryModule.Instance;
            DepotRegistrySnapshot liveRegistry = registry == null ? null : registry.CreateEffectRegistrySnapshot();
            if (liveRegistry == null || !String.Equals(registry.WorldId, prior.WorldId, StringComparison.OrdinalIgnoreCase) || !SameRegistry(liveRegistry, CreateAcceptedRegistrySnapshot(prior)))
                return BuildRejectedReceipt(proposal, "Loaded registry no longer matches the accepted recovery mirror");
            DepotRecord acceptedEndpoint = prior.Depots.SingleOrDefault(d => d.Active && d.DepotId == intent.Capability.DepotId && d.MembershipRevision == intent.MembershipRevision && d.MembershipHash == intent.Capability.MembershipHash);
            DepotRegistration registered = registry.Registrations.SingleOrDefault(d => d.DepotId == intent.Capability.DepotId && d.MembershipRevision == intent.MembershipRevision);
            if (acceptedEndpoint == null || registered == null || registered.Anchor == 0 || registered.MemberIds.Count != intent.MemberPersistentIds.Length ||
                !registered.MemberIds.OrderBy(x => x).SequenceEqual(intent.MemberPersistentIds.OrderBy(x => x)))
                return BuildRejectedReceipt(proposal, "Physical intent does not match the exact registered endpoint membership");

            InventoryEndpoint endpoint = new InventoryEndpoint { DepotId = registered.DepotId, MembershipRevision = registered.MembershipRevision,
                MemberPersistentIds = registered.MemberIds.OrderBy(x => x).ToArray(), AnchorPersistentId = registered.Anchor,
                MembershipHash = acceptedEndpoint.MembershipHash,
                MemberSetHash = OperationIdentity.ComputeMemberSetHash(registered.Anchor, registered.MemberIds), Scene = CurrentScene() };
            bool remote = String.Equals(intent.Capability.ProviderId, RemoteBrpInventoryGateway.ProviderId, StringComparison.Ordinal);
            IInventoryGateway gateway = remote ? (IInventoryGateway)remotePhysicalGateway :
                String.Equals(intent.Capability.ProviderId, LoadedBrpInventoryGateway.ProviderId, StringComparison.Ordinal) ? (IInventoryGateway)loadedPhysicalGateway : physicalGateway;
            if (!remote) physicalGateway.SetRuntimeScope(prior.WorldId, runId, sessionId, loadEpoch);
            InventoryCapabilityEvidence evidence = gateway.Describe(endpoint);
            InventoryCapability expectedCapability = new InventoryCapability { DepotId = registered.DepotId, MembershipRevision = registered.MembershipRevision,
                MembershipHash = acceptedEndpoint.MembershipHash, AnchorPersistentId = registered.Anchor,
                MemberSetHash = OperationIdentity.ComputeMemberSetHash(registered.Anchor, endpoint.MemberPersistentIds), ProviderId = evidence.ProviderId,
                ProviderVersion = evidence.ProviderVersion, Scene = evidence.Scene, ObservationAvailable = evidence.ObservationAvailable,
                ReadSupported = evidence.ReadSupported, WriteSupported = evidence.WriteSupported, SynchronousRollbackSupported = evidence.SynchronousRollbackSupported,
                PersistenceSyncSupported = evidence.PersistenceSyncSupported, HoldReason = evidence.HoldReason ?? string.Empty };
            if (!SameCapability(intent.Capability, expectedCapability)) return BuildRejectedReceipt(proposal, "Prepared provider capability is stale or does not match this endpoint");
            InventoryResolution resolution = gateway.Resolve(endpoint);
            if (resolution == null) return BuildRejectedReceipt(proposal, "BRP cannot resolve every selected member/resource uniquely");
            List<InventoryDelta> deltas = intent.Deltas.Select(d => new InventoryDelta { MemberPersistentId = d.MemberPersistentId, ResourceName = d.ResourceName, DeltaMicroUnits = d.DeltaMicroUnits }).ToList();
            InventoryPreflightResult preflight = gateway.Preflight(resolution, deltas);
            if (preflight == null || !preflight.Accepted || preflight.Plan == null) return BuildRejectedReceipt(proposal, preflight == null ? "BRP preflight failed" : preflight.Reason);

            // Prepare an EXS3 witness from the exact, coherent preflight plan before
            // any provider field can change. The accepted capsule is also fully
            // validated and bounded before the non-yielding mutation begins.
            PhysicalSuccessWitness witness = new PhysicalSuccessWitness
            {
                ProviderId = intent.Capability.ProviderId,
                ProviderVersion = intent.Capability.ProviderVersion,
                Rows = preflight.Plan.Rows.OrderBy(x => x.MemberPersistentId).ThenBy(x => x.ResourceName, StringComparer.Ordinal)
                    .Select(x => new PhysicalSuccessWitnessRow
                    {
                        MemberPersistentId = x.MemberPersistentId,
                        ResourceName = x.ResourceName,
                        BeforeAmount = x.BeforeAmount,
                        IntendedAfterAmount = x.IntendedAfterAmount,
                        ObservedAfterAmount = x.IntendedAfterAmount
                    }).ToArray()
            };
            string witnessError = AcceptedStateV2Draft.ValidatePhysicalSuccessWitness(intent, witness);
            if (witnessError != null) return BuildRejectedReceipt(proposal, "Physical success witness preflight failed: " + witnessError);

            StateTransitionResult transition;
            if (proposal.OperationKind == "dispatch")
            {
                if (delivery.ScheduleRuleUpdate == null)
                    transition = AcceptedStateCodec.Dispatch(prior, proposal.OperationId, proposal.ClientRequestId, proposal.CommandSequence, proposal.PayloadHash, proposal.ExpectedRevision, proposal.ExpectedStateHash, delivery.Shipment, intent, appliedUt, null, witness);
                else
                    transition = AcceptedStateCodec.Dispatch(prior, proposal.OperationId, proposal.ClientRequestId, proposal.CommandSequence, proposal.PayloadHash, proposal.ExpectedRevision, proposal.ExpectedStateHash, delivery.Shipment, intent, appliedUt, delivery.ScheduleRuleUpdate, witness);
            }
            else
                transition = AcceptedStateCodec.Arrive(prior, proposal.OperationId, proposal.ClientRequestId, proposal.CommandSequence, proposal.PayloadHash, proposal.ExpectedRevision, proposal.ExpectedStateHash, delivery.ShipmentId, delivery.Credits, delivery.RemainingCargo, intent, appliedUt, witness);
            if (transition.Outcome != "accepted") return BuildReceipt(proposal, transition.Outcome, appliedUt, 0, prior, transition.Reason);

            string prepareReason;
            RecoveryCapsuleModule.PreparedAcceptedState acceptedPrepared = recovery.PrepareAcceptedStateReplacement(prior.WorldId, AcceptedStateCodec.Serialize(transition.State), out prepareReason);
            if (acceptedPrepared == null) return BuildRejectedReceipt(proposal, prepareReason ?? "Accepted recovery witness preflight failed");

            // Prebuild a conservative fault witness with unknown observations before any
            // provider field can change. It reserves a bounded terminal state for a
            // failed write plus unconfirmed rollback; confirmed rollback retains prior.
            PhysicalEffectResult uncertain = BuildPhysicalResult(intent, preflight.Plan, null, "uncertain", "unknown", "Provider outcome or rollback could not be confirmed");
            StateTransitionResult faultTransition = AcceptedStateCodec.FaultPhysicalEffect(prior, proposal.OperationId, proposal.ClientRequestId, proposal.CommandSequence,
                proposal.PayloadHash, proposal.ExpectedRevision, proposal.ExpectedStateHash, proposal.OperationKind, registered.DepotId, registered.MembershipRevision,
                acceptedEndpoint.MembershipHash, intent, uncertain, appliedUt);
            string faultPrepareReason = null;
            RecoveryCapsuleModule.PreparedAcceptedState faultPrepared = faultTransition.Outcome == "faulted"
                ? recovery.PrepareAcceptedStateReplacement(prior.WorldId, AcceptedStateCodec.Serialize(faultTransition.State), out faultPrepareReason)
                : null;
            if (faultPrepared == null) return BuildRejectedReceipt(proposal, faultTransition.Reason ?? faultPrepareReason ?? "Fault recovery witness could not be reserved before the write");

            RecoveryCapsuleModule.PreparedAcceptedState beforePrepared = recovery.PrepareAcceptedStateReplacement(prior.WorldId, AcceptedStateCodec.Serialize(prior), out prepareReason);
            if (beforePrepared == null) return BuildRejectedReceipt(proposal, prepareReason ?? "Prior recovery witness could not be reserved before the write");
            Game physicalGame = HighLogic.CurrentGame;
            string physicalEpoch = loadEpoch, physicalRun = runId;
            recovery.CommitPreparedState(faultPrepared);

            InventoryApplyResult applied = gateway.Apply(preflight.Plan);
            if (!Object.ReferenceEquals(HighLogic.CurrentGame, physicalGame) || !Object.ReferenceEquals(RecoveryCapsuleModule.Instance, recovery) ||
                physicalEpoch != loadEpoch || physicalRun != runId || recovery.StateHash != faultPrepared.Hash)
                return BuildRejectedReceipt(proposal, "Physical world changed inside provider commit; its selected-save hold requires reconciliation");
            if (!applied.Succeeded)
            {
                InventoryRollbackResult rollback = gateway.Rollback(preflight.Plan);
                PhysicalEffectResult result = BuildPhysicalResult(intent, preflight.Plan, rollback.Rows, rollback.Confirmed ? "rollbackConfirmed" : "uncertain", rollback.Confirmed ? "confirmed" : "unknown", applied.Reason ?? "Provider write failed", inspectCurrent: true);
                if (rollback.Confirmed) { recovery.CommitPreparedState(beforePrepared); return BuildReceipt(proposal, "rejected", appliedUt, 0, prior, applied.Reason ?? "Provider write failed and exact rollback was confirmed", result); }
                AcceptedState faultState; RecoveryCapsuleModule.PreparedAcceptedState exactFault;
                if (TryPrepareFault(proposal, prior, intent, registered, acceptedEndpoint, result, appliedUt, recovery, out faultState, out exactFault)) { uncertain = result; faultTransition = new StateTransitionResult { State = faultState, Outcome = "faulted" }; faultPrepared = exactFault; }
                recovery.CommitPreparedState(faultPrepared);
                if (!remote) physicalGateway.InvalidateLineage();
                PublishEffectContext(HighLogic.LoadedSceneIsGame && HighLogic.CurrentGame != null, CurrentScene(), HighLogic.SaveFolder, null);
                return BuildReceipt(proposal, "faulted", appliedUt, 0, faultTransition.State, "Provider write failed and rollback was not confirmed", uncertain, faultPrepared);
            }

            InventorySyncResult synchronized = gateway.Synchronize(preflight.Plan);
            if (!synchronized.Succeeded || !MatchesPhysicalWitness(preflight.Plan, witness))
            {
                InventoryRollbackResult rollback = gateway.Rollback(preflight.Plan);
                string failureReason = synchronized.Succeeded ? "Provider readback did not match the preconstructed success witness" : synchronized.Reason;
                PhysicalEffectResult result = BuildPhysicalResult(intent, preflight.Plan, rollback.Rows, rollback.Confirmed ? "rollbackConfirmed" : "uncertain", rollback.Confirmed ? "confirmed" : "unknown", failureReason, inspectCurrent: true);
                if (rollback.Confirmed) { recovery.CommitPreparedState(beforePrepared); return BuildReceipt(proposal, "rejected", appliedUt, 0, prior, failureReason, result); }
                AcceptedState faultState; RecoveryCapsuleModule.PreparedAcceptedState exactFault;
                if (TryPrepareFault(proposal, prior, intent, registered, acceptedEndpoint, result, appliedUt, recovery, out faultState, out exactFault)) { uncertain = result; faultTransition = new StateTransitionResult { State = faultState, Outcome = "faulted" }; faultPrepared = exactFault; }
                recovery.CommitPreparedState(faultPrepared);
                if (!remote) physicalGateway.InvalidateLineage();
                PublishEffectContext(HighLogic.LoadedSceneIsGame && HighLogic.CurrentGame != null, CurrentScene(), HighLogic.SaveFolder, null);
                return BuildReceipt(proposal, "faulted", appliedUt, 0, faultTransition.State, failureReason, uncertain, faultPrepared);
            }

            // No yielding/callbacks/serialization between verified triple-field write and
            // this preconstructed capsule reference swap.
            recovery.CommitPreparedState(acceptedPrepared);
            if (!remote) physicalGateway.CommitLineage(preflight.Plan);
            PublishEffectContext(HighLogic.LoadedSceneIsGame && HighLogic.CurrentGame != null, CurrentScene(), HighLogic.SaveFolder, null);
            PhysicalEffectResult success = BuildPhysicalResult(intent, preflight.Plan, applied.Rows, "applied", "none", string.Empty);
            return BuildReceipt(proposal, "accepted", appliedUt, 0, transition.State, null, success, acceptedPrepared);
        }

        private void MaintainPhysicalGatewayLifecycle()
        {
            physicalGateway.MaintainLifecycle(!loadUnresolved);
        }

        private void InvalidatePhysicalGatewayLineage() { physicalGateway.InvalidateLineage(); }

        private static bool MatchesPhysicalWitness(InventoryMutationPlan plan, PhysicalSuccessWitness witness)
        {
            if (plan == null || witness == null || witness.Rows == null || witness.Rows.Length != plan.Rows.Count) return false;
            for (int i = 0; i < plan.Rows.Count; i++)
            {
                InventoryMutationRow row = plan.Rows[i];
                PhysicalSuccessWitnessRow expected = witness.Rows[i];
                if (expected.MemberPersistentId != row.MemberPersistentId || !String.Equals(expected.ResourceName, row.ResourceName, StringComparison.Ordinal) ||
                    expected.BeforeAmount != row.BeforeAmount || expected.IntendedAfterAmount != row.IntendedAfterAmount || expected.ObservedAfterAmount != row.IntendedAfterAmount)
                    return false;
                try
                {
                    if ((row.Resource == null && row.ProtoResource == null) ||
                        (row.Resource != null ? row.Resource.amount : row.ProtoResource.amount) != expected.ObservedAfterAmount ||
                        (row.ProviderInventory == null && witness.ProviderId != LoadedBrpInventoryGateway.ProviderId) ||
                        (row.ProviderInventory != null && (row.ProviderAmountField == null || row.ProviderOriginalAmountField == null ||
                        Convert.ToDouble(row.ProviderAmountField.GetValue(row.ProviderInventory), CultureInfo.InvariantCulture) != expected.ObservedAfterAmount ||
                        Convert.ToDouble(row.ProviderOriginalAmountField.GetValue(row.ProviderInventory), CultureInfo.InvariantCulture) != expected.ObservedAfterAmount)))
                        return false;
                    if (row.HasProviderSnapshot && row.SnapshotAmountField != null &&
                        Convert.ToDouble(row.SnapshotAmountField.GetValue(row.ProviderSnapshot), CultureInfo.InvariantCulture) !=
                        (row.ProtoResource == null ? row.BeforeSnapshotAmount : row.IntendedAfterAmount))
                        return false;
                }
                catch { return false; }
            }
            return true;
        }

        private static DepotRegistrySnapshot CreateAcceptedRegistrySnapshot(AcceptedState state)
        {
            DepotRecord[] active = state.Depots.Where(d => d.Active).Select(d => new DepotRecord { DepotId = d.DepotId, MembershipRevision = d.MembershipRevision, MembershipHash = d.MembershipHash, Active = true }).OrderBy(d => d.DepotId, StringComparer.Ordinal).ToArray();
            return new DepotRegistrySnapshot { RegistryVersion = state.DepotRegistryVersion, RegistryHash = state.DepotRegistryHash, Depots = active };
        }

        private static bool SameCapability(InventoryCapability a, InventoryCapability b)
        { return a != null && b != null && a.DepotId == b.DepotId && a.MembershipRevision == b.MembershipRevision && a.MembershipHash == b.MembershipHash && a.AnchorPersistentId == b.AnchorPersistentId && a.MemberSetHash == b.MemberSetHash && a.ProviderId == b.ProviderId && a.ProviderVersion == b.ProviderVersion && a.Scene == b.Scene && a.ObservationAvailable == b.ObservationAvailable && a.ReadSupported == b.ReadSupported && a.WriteSupported == b.WriteSupported && a.SynchronousRollbackSupported == b.SynchronousRollbackSupported && a.PersistenceSyncSupported == b.PersistenceSyncSupported; }

        private static bool TryPrepareFault(EffectProposal proposal, AcceptedState prior, PhysicalEffectIntent intent, DepotRegistration registration, DepotRecord endpoint, PhysicalEffectResult result, double appliedUt, RecoveryCapsuleModule recovery, out AcceptedState state, out RecoveryCapsuleModule.PreparedAcceptedState prepared)
        {
            state = null; prepared = null;
            try
            {
                StateTransitionResult transition = AcceptedStateCodec.FaultPhysicalEffect(prior, proposal.OperationId, proposal.ClientRequestId, proposal.CommandSequence, proposal.PayloadHash,
                    proposal.ExpectedRevision, proposal.ExpectedStateHash, proposal.OperationKind, registration.DepotId, registration.MembershipRevision, endpoint.MembershipHash, intent, result, appliedUt);
                if (transition.Outcome != "faulted") return false;
                string reason; prepared = recovery.PrepareAcceptedStateReplacement(prior.WorldId, AcceptedStateCodec.Serialize(transition.State), out reason);
                if (prepared == null) return false;
                state = transition.State; return true;
            }
            catch { return false; }
        }

        private static PhysicalEffectResult BuildPhysicalResult(PhysicalEffectIntent intent, InventoryMutationPlan plan, InventoryAppliedRow[] observedRows, string status, string rollback, string reason, bool inspectCurrent = false)
        {
            Dictionary<string, InventoryAppliedRow> observed = (observedRows ?? new InventoryAppliedRow[0]).ToDictionary(x => x.MemberPersistentId.ToString(CultureInfo.InvariantCulture) + "\0" + x.ResourceName, StringComparer.Ordinal);
            Dictionary<string, InventoryMutationRow> planned = plan.Rows.ToDictionary(x => x.MemberPersistentId.ToString(CultureInfo.InvariantCulture) + "\0" + x.ResourceName, StringComparer.Ordinal);
            HashSet<string> attempted = new HashSet<string>(plan.Rows.Take(plan.AttemptedCount).Select(x => x.MemberPersistentId.ToString(CultureInfo.InvariantCulture) + "\0" + x.ResourceName), StringComparer.Ordinal);
            PhysicalResourceDelta[] rows = intent.Deltas.OrderBy(x => x.MemberPersistentId).ThenBy(x => x.ResourceName, StringComparer.Ordinal).Select(d =>
            {
                string key = d.MemberPersistentId.ToString(CultureInfo.InvariantCulture) + "\0" + d.ResourceName;
                InventoryMutationRow p = planned[key]; InventoryAppliedRow o;
                bool known = observed.TryGetValue(key, out o);
                double observedAmount = known ? o.ObservedAfterAmount : p.BeforeAmount;
                if (inspectCurrent && (p.Resource != null || p.ProtoResource != null))
                {
                    try { observedAmount = p.Resource != null ? p.Resource.amount : p.ProtoResource.amount; known = !Double.IsNaN(observedAmount) && !Double.IsInfinity(observedAmount) && observedAmount >= 0; }
                    catch { known = false; }
                }
                bool rollbackObserved = inspectCurrent && attempted.Contains(key) && known;
                return new PhysicalResourceDelta { MemberPersistentId = d.MemberPersistentId, ResourceName = d.ResourceName, BeforeAmount = p.BeforeAmount,
                    IntendedDeltaMicroUnits = d.DeltaMicroUnits, IntendedAfterAmount = p.IntendedAfterAmount, ObservedAfterKnown = known,
                    ObservedAfterAmount = known ? observedAmount : 0, RollbackObserved = rollbackObserved,
                    RollbackObservedAmount = rollbackObserved ? observedAmount : 0 };
            }).ToArray();
            return new PhysicalEffectResult { Status = status, RollbackStatus = rollback, Reason = BoundEffect(reason, 256), Deltas = rows };
        }

        private static bool IsSupportedEffectKind(string kind)
        { return kind == "counterIncrement" || kind == "compact" || kind == "syncDepots" || kind == "routeUpsert" || kind == "ruleUpsert" || kind == "ruleCancel" || kind == "dispatch" || kind == "arrival" || kind == "recoverySale"; }

        private static string ExpectedPayloadHash(EffectProposal proposal)
        {
            if (proposal.OperationKind == "compact") return OperationIdentity.CompactionPayloadHash(proposal.TargetCompactionWatermark);
            if (proposal.OperationKind == "counterIncrement") return OperationIdentity.CounterIncrementPayloadHash(proposal.CounterDelta);
            if (proposal.Delivery == null) return string.Empty;
            if (proposal.OperationKind == "syncDepots" && proposal.Delivery.DepotRegistrySnapshot != null) return OperationIdentity.DepotRegistryPayloadHash(proposal.Delivery.DepotRegistrySnapshot);
            if (proposal.OperationKind == "routeUpsert" && proposal.Delivery.RouteVersion != null) return OperationIdentity.RouteVersionPayloadHash(proposal.Delivery.RouteVersion);
            if (proposal.OperationKind == "ruleUpsert" && proposal.Delivery.Rule != null) return OperationIdentity.RulePayloadHash(proposal.Delivery.Rule);
            if (proposal.OperationKind == "ruleCancel") return OperationIdentity.RuleCancelPayloadHash(proposal.Delivery.RuleId);
            if (proposal.OperationKind == "recoverySale" && proposal.Delivery.RecoveryIntent != null) return OperationIdentity.RecoveryPayloadHash(proposal.Delivery.RecoveryIntent);
            if (proposal.OperationKind == "dispatch" && proposal.Delivery != null && proposal.Delivery.Shipment != null && proposal.Delivery.PhysicalEffect != null)
            {
                if (proposal.Delivery.ScheduleRuleUpdate == null) return OperationIdentity.DispatchPayloadHash(proposal.Delivery.Shipment, proposal.Delivery.PhysicalEffect);
                return OperationIdentity.DispatchPayloadHash(proposal.Delivery.Shipment, proposal.Delivery.PhysicalEffect, proposal.Delivery.ScheduleRuleUpdate);
            }
            if (proposal.OperationKind == "arrival" && proposal.Delivery != null && proposal.Delivery.PhysicalEffect != null) return OperationIdentity.ArrivalPayloadHash(proposal.Delivery.ShipmentId, proposal.Delivery.Credits, proposal.Delivery.RemainingCargo, proposal.Delivery.PhysicalEffect);
            return string.Empty;
        }

        private static bool SameRegistry(DepotRegistrySnapshot a, DepotRegistrySnapshot b)
        {
            if (a == null || b == null || a.RegistryVersion != b.RegistryVersion || a.RegistryHash != b.RegistryHash || a.Depots == null || b.Depots == null || a.Depots.Length != b.Depots.Length) return false;
            DepotRecord[] left = a.Depots.OrderBy(x => x.DepotId, StringComparer.Ordinal).ToArray();
            DepotRecord[] right = b.Depots.OrderBy(x => x.DepotId, StringComparer.Ordinal).ToArray();
            for (int i = 0; i < left.Length; i++) if (left[i].DepotId != right[i].DepotId || left[i].MembershipRevision != right[i].MembershipRevision || left[i].MembershipHash != right[i].MembershipHash || left[i].Active != right[i].Active) return false;
            return true;
        }

        private EffectReceipt BuildRejectedReceipt(EffectProposal proposal, string reason)
        {
            AcceptedState state = null;
            try
            {
                RecoveryCapsuleModule recovery = RecoveryCapsuleModule.Instance;
                byte[] bytes = recovery == null ? null : recovery.GetAcceptedStateBytes();
                if (bytes != null) state = AcceptedStateCodec.Deserialize(bytes);
            }
            catch { }
            return BuildReceipt(proposal, "rejected", 0, 0, state, BoundEffect(reason, 256));
        }

        private EffectReceipt BuildReceipt(EffectProposal proposal, string outcome, double appliedUt, long actualDelta, AcceptedState state, string reason, PhysicalEffectResult physicalResult = null, RecoveryCapsuleModule.PreparedAcceptedState witness = null, EconomicEffectResult economicResult = null)
        {
            RecoveryCapsule capsule = null;
            string hash = string.Empty;
            long revision = 0, sequenceValue = 0;
            EffectContext current = Interlocked.CompareExchange(ref latestEffectContext, null, null);
            bool sameContext = state != null && current != null && state.WorldId == proposal.WorldId &&
                proposal.SessionId == current.SessionId && proposal.LoadEpoch == current.LoadEpoch && proposal.RunId == current.RunId &&
                proposal.InstallNamespace == current.InstallNamespace && proposal.SaveFolder == current.SaveFolder &&
                string.Equals(current.WorldId, state.WorldId, StringComparison.OrdinalIgnoreCase);
            try
            {
                if (sameContext)
                {
                    hash = witness == null ? AcceptedStateCodec.ComputeHash(state) : witness.Hash;
                    revision = state.Revision;
                    sequenceValue = state.AcceptedSequence;
                    capsule = witness == null ? AcceptedStateCodec.CreateCapsule(state) : witness.Capsule;
                }
            }
            catch { capsule = null; hash = string.Empty; revision = sequenceValue = 0; }
            return new EffectReceipt
            {
                ProtocolVersion = 1, MessageType = "effectReceipt",
                InstallNamespace = proposal.InstallNamespace ?? string.Empty, SaveFolder = proposal.SaveFolder ?? string.Empty,
                SessionId = proposal.SessionId, LoadEpoch = proposal.LoadEpoch,
                WorldId = proposal.WorldId ?? string.Empty, RunId = proposal.RunId ?? string.Empty,
                OperationId = proposal.OperationId ?? string.Empty, ClientRequestId = proposal.ClientRequestId ?? string.Empty,
                CommandSequence = proposal.CommandSequence, PayloadHash = proposal.PayloadHash ?? string.Empty,
                OperationKind = proposal.OperationKind ?? string.Empty, TargetCompactionWatermark = proposal.TargetCompactionWatermark,
                Outcome = outcome, AppliedUt = SafeFinite(appliedUt) ? appliedUt : 0, ActualCounterDelta = actualDelta,
                AcceptedRevision = revision, AcceptedSequence = sequenceValue, StateHash = hash, AcceptedCapsule = capsule, Reason = BoundEffect(reason, 256), PhysicalResult = physicalResult, EconomicResult = economicResult
            };
        }

        private static double SafeCurrentUt()
        {
            try { double v = Planetarium.GetUniversalTime(); return SafeFinite(v) && v >= 0 ? v : 0; }
            catch { return 0; }
        }
        private static bool SafeFinite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }

        private void EffectsPipeWorker()
        {
            try { EffectsPipeWorkerLoop(); }
            finally
            {
                try { if (activeEffectPipe != null) activeEffectPipe.Dispose(); } catch { }
                try { effectWakeWorker.Dispose(); } catch { }
            }
        }

        private void EffectsPipeWorkerLoop()
        {
            string pipeName;
            try { pipeName = ResolveEffectsPipe(Environment.UserName, Environment.GetCommandLineArgs()); }
            catch (Exception ex) { WorkerError("effects namespace lookup", null, ex); return; }
            string lastAttachedKey = null;
            long lastPollAt = 0;
            while (!stopping)
            {
                NamedPipeClientStream pipe = null;
                try
                {
                    pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None);
                    activeEffectPipe = pipe;
                    pipe.Connect(1000);
                    if (!reportedEffectPipeError) WorkerLog("Connected to recovery effects pipe name=" + pipeName);
                    lastAttachedKey = null;
                    while (!stopping && pipe.IsConnected)
                    {
                        EffectContext context = Interlocked.CompareExchange(ref latestEffectContext, null, null);
                        if (context == null) { WaitForEffectsWorker(500); continue; }
                        if (!string.Equals(lastAttachedKey, context.AttachmentKey, StringComparison.Ordinal))
                        {
                            if (!TryBeginEffectExchange(context)) continue;
                            try
                            {
                                WriteEffectFrame(pipe, EffectAttachJson(context));
                                string response = ReadEffectFrame(pipe);
                                string type = ReadMessageType(response);
                                if (type == "effectProposal") { SetPendingProposal(ParseProposal(response)); WaitForProposalReceipt(pipe); lastAttachedKey = null; continue; }
                                if (type != "effectIdle" && type != "effectNeedAttach") throw new InvalidDataException("Unexpected response to effectAttach: " + type);
                                if (type == "effectNeedAttach") ReportNeedAttach("attach", response, context);
                                lastAttachedKey = context.AttachmentKey;
                            }
                            finally { lock (effectGate) effectExchangeInFlight = false; }
                            continue;
                        }

                        if (!context.CanWrite) { WaitForEffectsWorker(1000); continue; }
                        if (lastPollAt != 0)
                        {
                            long elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - lastPollAt;
                            long interval = System.Diagnostics.Stopwatch.Frequency;
                            if (elapsed < interval)
                            {
                                int remaining = (int)Math.Max(1, Math.Ceiling((interval - elapsed) * 1000d / System.Diagnostics.Stopwatch.Frequency));
                                WaitForEffectsWorker(remaining);
                                continue;
                            }
                        }
                        if (!TryBeginEffectExchange(context)) continue;
                        try
                        {
                            WriteEffectFrame(pipe, EffectPollJson(context));
                            lastPollAt = System.Diagnostics.Stopwatch.GetTimestamp();
                            string pollResponse = ReadEffectFrame(pipe);
                            string pollType = ReadMessageType(pollResponse);
                            if (pollType == "effectProposal") { SetPendingProposal(ParseProposal(pollResponse)); WaitForProposalReceipt(pipe); lastAttachedKey = null; continue; }
                            if (pollType == "effectNeedAttach") { ReportNeedAttach("poll", pollResponse, context); lastAttachedKey = null; continue; }
                            if (pollType != "effectIdle") throw new InvalidDataException("Unexpected response to effectPoll: " + pollType);
                        }
                        finally { lock (effectGate) effectExchangeInFlight = false; }
                        WaitForEffectsWorker(1000);
                    }
                }
                catch (Exception ex)
                {
                    long now = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (!reportedEffectPipeError || now - lastEffectPipeErrorAt >= System.Diagnostics.Stopwatch.Frequency * 30L)
                    {
                        WorkerError("effects exchange", pipeName, ex);
                        reportedEffectPipeError = true;
                        lastEffectPipeErrorAt = now;
                    }
                }
                finally
                {
                    activeEffectPipe = null;
                    // A receipt is notification, not effect authority. Disconnecting
                    // the Host must not stop game-owned schedules; the selected capsule
                    // contains the durable result for the Host's next attachment.
                    lock (effectGate) { effectExchangeInFlight = false; pendingEffectReceipt = null; }
                    if (pipe != null) { try { pipe.Dispose(); } catch { } }
                }
                if (!stopping) WaitForEffectsWorker(1000);
            }
        }

        private void WaitForProposalReceipt(NamedPipeClientStream pipe)
        {
            while (!stopping && pipe.IsConnected)
            {
                EffectReceipt receipt;
                lock (effectGate) { receipt = pendingEffectReceipt; }
                if (receipt != null)
                {
                    WriteEffectFrame(pipe, ReceiptJson(receipt));
                    string ack = ReadEffectFrame(pipe);
                    string type = ReadMessageType(ack);
                    if (type != "effectIdle" && type != "effectNeedAttach") throw new InvalidDataException("Unexpected response to effectReceipt: " + type);
                    if (type == "effectNeedAttach") ReportNeedAttach("receipt", ack, Interlocked.CompareExchange(ref latestEffectContext, null, null));
                    lock (effectGate) pendingEffectReceipt = null;
                    return;
                }
                WaitForEffectsWorker(250);
            }
        }

        private void ReportNeedAttach(string phase, string response, EffectContext context)
        {
            string boundedResponse = BoundEffect(response, 384);
            string key = phase + "\n" + boundedResponse;
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            long previous = Interlocked.Read(ref lastNeedAttachLoggedAt);
            string priorKey = Interlocked.CompareExchange(ref lastNeedAttachDiagnostic, null, null);
            if (string.Equals(key, priorKey, StringComparison.Ordinal) && previous != 0 && now - previous < System.Diagnostics.Stopwatch.Frequency * 30L) return;
            Interlocked.Exchange(ref lastNeedAttachDiagnostic, key);
            Interlocked.Exchange(ref lastNeedAttachLoggedAt, now);
            string details = context == null ? "context=missing" :
                "run=" + context.RunId + " epoch=" + context.LoadEpoch.ToString("D") + " session=" + context.SessionId.ToString("D") +
                " world=" + context.WorldId + " save=" + context.SaveFolder + " seq=" + context.Sequence.ToString(CultureInfo.InvariantCulture) +
                " rev=" + context.Revision.ToString(CultureInfo.InvariantCulture) + " stateHash=" + context.StateHash +
                " registryHash=" + (context.RegistrySnapshot == null ? "missing" : context.RegistrySnapshot.RegistryHash) + " canWrite=" + context.CanWrite;
            EffectContext current = Interlocked.CompareExchange(ref latestEffectContext, null, null);
            string currentDetails = current == null ? "latestContext=missing" : "latestRun=" + current.RunId + " latestEpoch=" + current.LoadEpoch.ToString("D") +
                " latestSession=" + current.SessionId.ToString("D") + " latestWorld=" + current.WorldId + " latestSeq=" + current.Sequence.ToString(CultureInfo.InvariantCulture) +
                " latestStateHash=" + current.StateHash + " latestRegistryHash=" + (current.RegistrySnapshot == null ? "missing" : current.RegistrySnapshot.RegistryHash);
            QueueWorkerLog("Host returned effectNeedAttach phase=" + phase + " requestContext{" + details + "} " + currentDetails + " response=" + boundedResponse);
        }

        private void SetPendingProposal(EffectProposal proposal)
        {
            lock (effectGate)
            {
                if (pendingEffectProposal != null || pendingEffectReceipt != null) throw new InvalidDataException("Host offered more than one unacknowledged proposal");
                pendingEffectProposal = proposal;
            }
        }

        private bool TryBeginEffectExchange(EffectContext context)
        {
            lock (effectGate)
            {
                if (effectExchangeInFlight || !Object.ReferenceEquals(context, latestEffectContext)) return false;
                effectExchangeInFlight = true;
                return true;
            }
        }

        private bool WaitForEffectsWorker(int milliseconds)
        {
            try { return effectWakeWorker.WaitOne(milliseconds); }
            catch (Exception ex) { WorkerError("effects worker wait", null, ex); stopping = true; return false; }
        }

        private static string ResolveEffectsPipe(string user, string[] args)
        {
            if (string.IsNullOrWhiteSpace(user)) throw new InvalidOperationException("Environment.UserName is empty");
            string prefix = "ExpanseFoundations.Effects.dev." + user + ".";
            for (int i = 0; i < args.Length; i++)
            {
                const string marker = "-expanseEffectsPipe=";
                if (!args[i].StartsWith(marker, StringComparison.Ordinal)) continue;
                string supplied = args[i].Substring(marker.Length);
                if (!supplied.StartsWith(prefix, StringComparison.Ordinal)) throw new FormatException("Rejected effects pipe override outside the current-user dev namespace");
                string token = supplied.Substring(prefix.Length);
                if (token.Length < 1 || token.Length > 64) throw new FormatException("Rejected effects pipe override token length");
                for (int c = 0; c < token.Length; c++)
                    if (!((token[c] >= 'A' && token[c] <= 'Z') || (token[c] >= 'a' && token[c] <= 'z') || (token[c] >= '0' && token[c] <= '9') || token[c] == '-' || token[c] == '_'))
                        throw new FormatException("Rejected effects pipe override token characters");
                return supplied;
            }
            return "ExpanseFoundations.Effects.v1." + user;
        }

        private static void WriteEffectFrame(Stream stream, string json)
        {
            byte[] body = new UTF8Encoding(false, true).GetBytes(json);
            if (body.Length < 1 || body.Length > 65536) throw new InvalidDataException("Effects frame exceeds 64 KiB");
            uint n = (uint)body.Length;
            byte[] header = BitConverter.GetBytes(n);
            if (!BitConverter.IsLittleEndian) Array.Reverse(header);
            stream.Write(header, 0, header.Length);
            stream.Write(body, 0, body.Length);
            stream.Flush();
        }

        private static string ReadEffectFrame(Stream stream)
        {
            byte[] header = new byte[4]; ReadExact(stream, header);
            if (!BitConverter.IsLittleEndian) Array.Reverse(header);
            uint n = BitConverter.ToUInt32(header, 0);
            if (n < 1 || n > 65536) throw new InvalidDataException("Invalid effects frame length");
            byte[] body = new byte[(int)n]; ReadExact(stream, body);
            return new UTF8Encoding(false, true).GetString(body);
        }

        private static void ReadExact(Stream stream, byte[] buffer)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int read = stream.Read(buffer, offset, buffer.Length - offset);
                if (read <= 0) throw new EndOfStreamException("Truncated effects frame");
                offset += read;
            }
        }

        private static string EffectAttachJson(EffectContext c)
        {
            return "{\"protocolVersion\":1,\"messageType\":\"effectAttach\",\"sessionId\":" + Q(c.SessionId.ToString("D")) + ",\"loadEpoch\":" + Q(c.LoadEpoch.ToString("D")) +
                ",\"bridgeProcessId\":" + I(c.BridgeProcessId) + ",\"bridgeProcessStartUtcTicks\":" + I(c.BridgeProcessStartUtcTicks) + ",\"bridgeExecutablePath\":" + Q(c.BridgeExecutablePath) + ",\"installNamespace\":" + Q(c.InstallNamespace) +
                ",\"saveFolder\":" + Q(c.SaveFolder) + ",\"worldId\":" + Q(c.WorldId) + ",\"runId\":" + Q(c.RunId) +
                ",\"checkpointId\":" + Q(c.CheckpointId) + ",\"revision\":" + I(c.Revision) + ",\"acceptedSequence\":" + I(c.Sequence) +
                ",\"compactionWatermark\":" + I(c.Watermark) + ",\"stateHash\":" + Q(c.StateHash) + ",\"canWrite\":" + (c.CanWrite ? "true" : "false") +
                ",\"unavailableReason\":" + Q(c.UnavailableReason) + ",\"capabilities\":" + EffectCapabilitiesJson(c) + ",\"inventoryEndpoints\":" + InventoryEndpointsJson(c.InventoryEndpoints) + ",\"registrySnapshot\":" + RegistrySnapshotJson(c.RegistrySnapshot) + ",\"capsule\":" + CapsuleJson(c.Capsule) + "}";
        }

        private static string EffectCapabilitiesJson(EffectContext context)
        {
            string economic = context.EconomicRecoveryReady ? ",\"economicRecovery.v1\"" : "";
            if (PhysicalCandidateEnabled || RemotePhysicalCandidateEnabled)
                return "[\"syncDepots\",\"compact\",\"routeUpsert\",\"ruleUpsert\",\"ruleCancel\",\"dispatch\",\"arrival\",\"runtimeLogistics.v1\"" + economic + "]";
            return devRecoveryFixtureEnabled ? "[\"syncDepots\",\"compact\",\"routeUpsert\",\"ruleUpsert\",\"ruleCancel\",\"counterIncrement\",\"runtimeLogistics.v1\"" + economic + "]" : "[\"syncDepots\",\"compact\",\"routeUpsert\",\"ruleUpsert\",\"ruleCancel\",\"runtimeLogistics.v1\"" + economic + "]";
        }

        private static string RegistrySnapshotJson(DepotRegistrySnapshot snapshot)
        {
            if (snapshot == null) return "null";
            StringBuilder b = new StringBuilder("{\"registryVersion\":").Append(Q(snapshot.RegistryVersion)).Append(",\"registryHash\":").Append(Q(snapshot.RegistryHash)).Append(",\"depots\":[");
            for (int i = 0; i < snapshot.Depots.Length; i++)
            {
                if (i != 0) b.Append(',');
                DepotRecord depot = snapshot.Depots[i];
                b.Append("{\"depotId\":").Append(Q(depot.DepotId)).Append(",\"membershipRevision\":").Append(I(depot.MembershipRevision)).Append(",\"membershipHash\":").Append(Q(depot.MembershipHash)).Append(",\"active\":").Append(depot.Active ? "true" : "false").Append('}');
            }
            return b.Append("]}").ToString();
        }

        private InventoryCapability[] BuildInventoryEndpoints(DepotRegistrySnapshot snapshot, string scene)
        {
            if (snapshot == null || snapshot.Depots.Length == 0) return new InventoryCapability[0];
            string key = (sessionId ?? "") + "\n" + (loadEpoch ?? "") + "\n" + (snapshot.RegistryHash ?? "") + "\n" +
                (scene ?? "") + "\n" + (PhysicalCandidateEnabled ? "L" : "-") + (RemotePhysicalCandidateEnabled ? "R" : "-");
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (endpointCapabilityCache != null && String.Equals(endpointCapabilityCacheKey, key, StringComparison.Ordinal) &&
                now >= endpointCapabilityCapturedAt && now - endpointCapabilityCapturedAt < 2f)
                return endpointCapabilityCache;
            long scanStarted = WorldBridgeAddon.RemotePhysicalDiagnosticsEnabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            InventoryCapability[] endpoints = new InventoryCapability[snapshot.Depots.Length];
            BrpInventoryGateway gateway = new BrpInventoryGateway();
            RemoteBrpInventoryGateway remoteGateway = new RemoteBrpInventoryGateway();
            LoadedBrpInventoryGateway loadedGateway = new LoadedBrpInventoryGateway();
            for (int i = 0; i < snapshot.Depots.Length; i++)
            {
                DepotRecord depot = snapshot.Depots[i];
                DepotRegistryModule registry = DepotRegistryModule.Instance;
                DepotRegistration registration = registry == null ? null : registry.Registrations.FirstOrDefault(x => string.Equals(x.DepotId, depot.DepotId, StringComparison.OrdinalIgnoreCase));
                uint[] members = registration == null ? new uint[0] : registration.MemberIds.OrderBy(x => x).ToArray();
                uint anchor = registration == null ? 0 : registration.Anchor;
                InventoryEndpoint inventoryEndpoint = new InventoryEndpoint { DepotId = depot.DepotId, MembershipRevision = (int)depot.MembershipRevision, MemberPersistentIds = members,
                    AnchorPersistentId = anchor, MembershipHash = depot.MembershipHash,
                    MemberSetHash = anchor == 0 || members.Length == 0 ? string.Empty : OperationIdentity.ComputeMemberSetHash(anchor, members), Scene = scene ?? "unknown" };
                InventoryCapabilityEvidence evidence = loadedGateway.Describe(inventoryEndpoint);
                if (!evidence.ObservationAvailable) evidence = remoteGateway.Describe(inventoryEndpoint);
                if (!evidence.ObservationAvailable && PhysicalCandidateEnabled) evidence = gateway.Describe(inventoryEndpoint);
                endpoints[i] = new InventoryCapability { DepotId = depot.DepotId, MembershipRevision = depot.MembershipRevision,
                    MembershipHash = depot.MembershipHash, AnchorPersistentId = anchor, MemberSetHash = anchor == 0 || members.Length == 0 ? string.Empty : OperationIdentity.ComputeMemberSetHash(anchor, members),
                    ProviderId = evidence.ProviderId, ProviderVersion = evidence.ProviderVersion, Scene = evidence.Scene,
                    ObservationAvailable = evidence.ObservationAvailable, ReadSupported = evidence.ReadSupported,
                    WriteSupported = evidence.WriteSupported, SynchronousRollbackSupported = evidence.SynchronousRollbackSupported,
                    PersistenceSyncSupported = evidence.PersistenceSyncSupported, HoldReason = evidence.HoldReason };
            }
            endpointCapabilityCacheKey = key;
            endpointCapabilityCapturedAt = now;
            endpointCapabilityCache = endpoints;
            if (scanStarted != 0)
            {
                double elapsedMs = (System.Diagnostics.Stopwatch.GetTimestamp() - scanStarted) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                UnityEngine.Debug.Log("[ExpanseRemoteBrp] capability scan ms=" + elapsedMs.ToString("F3", CultureInfo.InvariantCulture) +
                    " depots=" + endpoints.Length + " warpIndex=" + TimeWarp.CurrentRateIndex);
            }
            return endpoints;
        }

        private static string InventoryCapabilityJson(InventoryCapability value)
        {
            return "{\"depotId\":" + Q(value.DepotId) + ",\"membershipRevision\":" + I(value.MembershipRevision) + ",\"providerId\":" + Q(value.ProviderId) +
                ",\"membershipHash\":" + Q(value.MembershipHash) + ",\"anchorPersistentId\":" + I(value.AnchorPersistentId) + ",\"memberSetHash\":" + Q(value.MemberSetHash) +
                ",\"providerVersion\":" + Q(value.ProviderVersion) + ",\"scene\":" + Q(value.Scene) + ",\"observationAvailable\":" + (value.ObservationAvailable ? "true" : "false") +
                ",\"readSupported\":" + (value.ReadSupported ? "true" : "false") + ",\"writeSupported\":" + (value.WriteSupported ? "true" : "false") +
                ",\"synchronousRollbackSupported\":" + (value.SynchronousRollbackSupported ? "true" : "false") + ",\"persistenceSyncSupported\":" + (value.PersistenceSyncSupported ? "true" : "false") +
                ",\"holdReason\":" + Q(value.HoldReason) + "}";
        }

        private static string InventoryEndpointsJson(InventoryCapability[] endpoints)
        {
            if (endpoints == null || endpoints.Length == 0) return "[]";
            StringBuilder b = new StringBuilder("[");
            for (int i = 0; i < endpoints.Length; i++) { if (i != 0) b.Append(','); b.Append(InventoryCapabilityJson(endpoints[i])); }
            return b.Append(']').ToString();
        }

        private static string InventoryEndpointsFingerprint(InventoryCapability[] endpoints)
        {
            if (endpoints == null || endpoints.Length == 0) return "0";
            if (endpoints.Length > AcceptedStateCodec.MaxDepots) return "invalid-count";
            StringBuilder b = new StringBuilder();
            InventoryCapability[] ordered = endpoints.OrderBy(x => x.DepotId, StringComparer.Ordinal).ToArray();
            b.Append(ordered.Length.ToString(CultureInfo.InvariantCulture)).Append(';');
            foreach (InventoryCapability endpoint in ordered)
            {
                AppendEndpointField(b, endpoint.DepotId);
                AppendEndpointField(b, endpoint.MembershipRevision.ToString(CultureInfo.InvariantCulture));
                AppendEndpointField(b, endpoint.MembershipHash);
                AppendEndpointField(b, endpoint.AnchorPersistentId.ToString(CultureInfo.InvariantCulture));
                AppendEndpointField(b, endpoint.MemberSetHash);
                AppendEndpointField(b, endpoint.ProviderId);
                AppendEndpointField(b, endpoint.ProviderVersion);
                AppendEndpointField(b, endpoint.Scene);
                AppendEndpointField(b, endpoint.ObservationAvailable ? "1" : "0");
                AppendEndpointField(b, endpoint.ReadSupported ? "1" : "0");
                AppendEndpointField(b, endpoint.WriteSupported ? "1" : "0");
                AppendEndpointField(b, endpoint.SynchronousRollbackSupported ? "1" : "0");
                AppendEndpointField(b, endpoint.PersistenceSyncSupported ? "1" : "0");
                AppendEndpointField(b, endpoint.HoldReason);
            }
            return b.ToString();
        }

        private static void AppendEndpointField(StringBuilder builder, string value)
        {
            value = value ?? string.Empty;
            builder.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append(';');
        }

        private static string EffectPollJson(EffectContext c)
        {
            string prefix = "{\"protocolVersion\":1,\"messageType\":\"effectPoll\",\"sessionId\":" + Q(c.SessionId.ToString("D")) + ",\"loadEpoch\":" + Q(c.LoadEpoch.ToString("D")) + ",\"installNamespace\":" + Q(c.InstallNamespace) +
                ",\"saveFolder\":" + Q(c.SaveFolder) + ",\"worldId\":" + Q(c.WorldId) + ",\"runId\":" + Q(c.RunId) +
                ",\"checkpointId\":" + Q(c.CheckpointId) + ",\"revision\":" + I(c.Revision) + ",\"acceptedSequence\":" + I(c.Sequence) +
                ",\"compactionWatermark\":" + I(c.Watermark) + ",\"stateHash\":" + Q(c.StateHash) + ",\"inventoryObservations\":";
            string holds = ",\"runtimeShipmentHolds\":[" + String.Join(",", (c.RuntimeShipmentHolds ?? new TransientShipmentHold[0]).Select(x => "{\"shipmentId\":" + Q(x.ShipmentId) + ",\"reason\":" + Q(BoundEffect(x.Reason, 256)) + "}")) + "]}";
            string json = prefix + InventoryObservationsJson(c.InventoryObservations) + holds;
            if (Encoding.UTF8.GetByteCount(json) <= 60000) return json;
            StockObservation[] compact = (c.InventoryObservations ?? new StockObservation[0]).Select(x => new StockObservation { DepotId = x.DepotId, SessionId = x.SessionId, LoadEpoch = x.LoadEpoch, WorldId = x.WorldId, RegistryHash = x.RegistryHash, MembershipRevision = x.MembershipRevision, MembershipHash = x.MembershipHash, ObservationRevision = x.ObservationRevision, ObservedUt = x.ObservedUt, AgeSeconds = x.AgeSeconds, Available = false, MicroUnitProjectionSafe = false, UnavailableReason = "Stock observation exceeded the effects frame bound", Resources = new StockAmount[0], MemberStocks = new MemberStockObservation[0] }).ToArray();
            return prefix + InventoryObservationsJson(compact) + holds;
        }

        private static string InventoryObservationsJson(StockObservation[] observations)
        {
            if (observations == null || observations.Length == 0) return "[]";
            StringBuilder b = new StringBuilder("[");
            for (int i = 0; i < observations.Length; i++)
            {
                if (i != 0) b.Append(',');
                StockObservation x = observations[i];
                b.Append("{\"depotId\":").Append(Q(x.DepotId)).Append(",\"sessionId\":").Append(Q(x.SessionId.ToString("D"))).Append(",\"loadEpoch\":").Append(Q(x.LoadEpoch.ToString("D"))).Append(",\"worldId\":").Append(Q(x.WorldId)).Append(",\"registryHash\":").Append(Q(x.RegistryHash))
                    .Append(",\"membershipRevision\":").Append(I(x.MembershipRevision)).Append(",\"membershipHash\":").Append(Q(x.MembershipHash)).Append(",\"observationRevision\":").Append(I(x.ObservationRevision)).Append(",\"observedUt\":").Append(F(x.ObservedUt)).Append(",\"ageSeconds\":").Append(F(x.AgeSeconds))
                    .Append(",\"available\":").Append(x.Available ? "true" : "false").Append(",\"microUnitProjectionSafe\":").Append(x.MicroUnitProjectionSafe ? "true" : "false").Append(",\"unavailableReason\":").Append(Q(x.UnavailableReason)).Append(",\"resources\":");
                AppendStockRows(b, x.Resources);
                b.Append(",\"memberStocks\":[");
                for (int m = 0; m < x.MemberStocks.Length; m++) { if (m != 0) b.Append(','); b.Append("{\"memberPersistentId\":").Append(I(x.MemberStocks[m].MemberPersistentId)).Append(",\"resources\":"); AppendStockRows(b, x.MemberStocks[m].Resources); b.Append('}'); }
                b.Append("]}");
            }
            return b.Append(']').ToString();
        }

        private static void AppendStockRows(StringBuilder b, StockAmount[] rows)
        {
            b.Append('[');
            for (int i = 0; rows != null && i < rows.Length; i++) { if (i != 0) b.Append(','); b.Append("{\"resourceName\":").Append(Q(rows[i].ResourceName)).Append(",\"amountMicroUnits\":").Append(I(rows[i].AmountMicroUnits)).Append(",\"capacityMicroUnits\":").Append(I(rows[i].CapacityMicroUnits)).Append(",\"debitAllowed\":").Append(rows[i].DebitAllowed.HasValue ? (rows[i].DebitAllowed.Value ? "true" : "false") : "null").Append('}'); }
            b.Append(']');
        }

        private static string ReceiptJson(EffectReceipt r)
        {
            return "{\"protocolVersion\":1,\"messageType\":\"effectReceipt\",\"sessionId\":" + Q(r.SessionId.ToString("D")) + ",\"loadEpoch\":" + Q(r.LoadEpoch.ToString("D")) + ",\"installNamespace\":" + Q(r.InstallNamespace) +
                ",\"saveFolder\":" + Q(r.SaveFolder) + ",\"worldId\":" + Q(r.WorldId) + ",\"runId\":" + Q(r.RunId) +
                ",\"operationId\":" + Q(r.OperationId) + ",\"clientRequestId\":" + Q(r.ClientRequestId) + ",\"commandSequence\":" + I(r.CommandSequence) +
                ",\"payloadHash\":" + Q(r.PayloadHash) + ",\"operationKind\":" + Q(r.OperationKind) + ",\"targetCompactionWatermark\":" + I(r.TargetCompactionWatermark) + ",\"outcome\":" + Q(r.Outcome) + ",\"appliedUt\":" + F(r.AppliedUt) +
                ",\"actualCounterDelta\":" + I(r.ActualCounterDelta) + ",\"acceptedRevision\":" + I(r.AcceptedRevision) +
                ",\"acceptedSequence\":" + I(r.AcceptedSequence) + ",\"stateHash\":" + Q(r.StateHash) +
                ",\"acceptedCapsule\":" + CapsuleJson(r.AcceptedCapsule) + ",\"physicalResult\":" + PhysicalResultJson(r.PhysicalResult) + ",\"economicResult\":" + EconomicResultJson(r.EconomicResult) + ",\"reason\":" + Q(r.Reason) + "}";
        }

        private static string PhysicalResultJson(PhysicalEffectResult result)
        {
            if (result == null) return "null";
            StringBuilder b = new StringBuilder("{\"status\":").Append(Q(result.Status)).Append(",\"rollbackStatus\":").Append(Q(result.RollbackStatus)).Append(",\"reason\":").Append(Q(result.Reason)).Append(",\"deltas\":[");
            for (int i = 0; i < result.Deltas.Length; i++)
            {
                if (i != 0) b.Append(',');
                PhysicalResourceDelta d = result.Deltas[i];
                b.Append("{\"memberPersistentId\":").Append(I(d.MemberPersistentId)).Append(",\"resourceName\":").Append(Q(d.ResourceName))
                    .Append(",\"beforeAmount\":").Append(F(d.BeforeAmount)).Append(",\"intendedDeltaMicroUnits\":").Append(I(d.IntendedDeltaMicroUnits))
                    .Append(",\"intendedAfterAmount\":").Append(F(d.IntendedAfterAmount)).Append(",\"observedAfterKnown\":").Append(d.ObservedAfterKnown ? "true" : "false")
                    .Append(",\"observedAfterAmount\":").Append(F(d.ObservedAfterAmount)).Append(",\"rollbackObserved\":").Append(d.RollbackObserved ? "true" : "false")
                    .Append(",\"rollbackObservedAmount\":").Append(F(d.RollbackObservedAmount)).Append('}');
            }
            return b.Append("]}").ToString();
        }

        private static string CapsuleJson(RecoveryCapsule capsule)
        {
            if (capsule == null) return "null";
            return "{\"schemaVersion\":" + I(capsule.SchemaVersion) + ",\"worldId\":" + Q(capsule.WorldId) + ",\"stateBytesBase64\":" + Q(capsule.StateBytesBase64) + ",\"stateSha256\":" + Q(capsule.StateSha256) + "}";
        }

        private static string ReadMessageType(string json)
        {
            Dictionary<string, object> root = MinimalJson.ParseObject(json);
            object value;
            if (!root.TryGetValue("messageType", out value) || !(value is string)) throw new InvalidDataException("Effects response has no messageType");
            return (string)value;
        }

        private static EffectProposal ParseProposal(string json)
        {
            Dictionary<string, object> o = MinimalJson.ParseObject(json);
            EffectProposal proposal = new EffectProposal
            {
                ProtocolVersion = ReadInt(o, "protocolVersion"), MessageType = ReadString(o, "messageType"),
                SessionId = Guid.Parse(ReadString(o, "sessionId")), LoadEpoch = Guid.Parse(ReadString(o, "loadEpoch")),
                InstallNamespace = ReadString(o, "installNamespace"), SaveFolder = ReadString(o, "saveFolder"), WorldId = ReadString(o, "worldId"), RunId = ReadString(o, "runId"),
                OperationId = ReadString(o, "operationId"), ClientRequestId = ReadString(o, "clientRequestId"), CommandSequence = ReadLong(o, "commandSequence"),
                ExpectedRevision = ReadLong(o, "expectedRevision"), ExpectedStateHash = ReadString(o, "expectedStateHash"), PayloadHash = ReadString(o, "payloadHash"),
                OperationKind = ReadString(o, "operationKind"), CounterDelta = ReadLong(o, "counterDelta")
                , TargetCompactionWatermark = ReadLong(o, "targetCompactionWatermark")
            };
            object delivery;
            if (o.TryGetValue("delivery", out delivery) && delivery is Dictionary<string, object>) proposal.Delivery = ParseDelivery((Dictionary<string, object>)delivery);
            object physical;
            if (o.TryGetValue("physicalEffect", out physical) && physical is Dictionary<string, object>) proposal.PhysicalEffect = ParsePhysicalIntent((Dictionary<string, object>)physical);
            return proposal;
        }

        private static DeliveryEffectPayload ParseDelivery(Dictionary<string, object> o)
        {
            DeliveryEffectPayload result = new DeliveryEffectPayload { Kind = ReadString(o, "kind"), ShipmentId = ReadString(o, "shipmentId"), RuleId = ReadString(o, "ruleId") };
            object value;
            if (o.TryGetValue("depotRegistrySnapshot", out value) && value is Dictionary<string, object>) result.DepotRegistrySnapshot = ParseRegistrySnapshot((Dictionary<string, object>)value);
            if (o.TryGetValue("routeVersion", out value) && value is Dictionary<string, object>) result.RouteVersion = ParseRoute((Dictionary<string, object>)value);
            if (o.TryGetValue("rule", out value) && value is Dictionary<string, object>) result.Rule = ParseRule((Dictionary<string, object>)value);
            if (o.TryGetValue("shipment", out value) && value is Dictionary<string, object>) result.Shipment = ParseShipment((Dictionary<string, object>)value);
            if (o.TryGetValue("scheduleRuleUpdate", out value) && value is Dictionary<string, object>) result.ScheduleRuleUpdate = ParseRule((Dictionary<string, object>)value);
            if (o.TryGetValue("credits", out value) && value is List<object>) result.Credits = ParseResources((List<object>)value);
            if (o.TryGetValue("remainingCargo", out value) && value is List<object>) result.RemainingCargo = ParseResources((List<object>)value);
            if (o.TryGetValue("physicalEffect", out value) && value is Dictionary<string, object>) result.PhysicalEffect = ParsePhysicalIntent((Dictionary<string, object>)value);
            if (o.TryGetValue("recoveryIntent", out value) && value is Dictionary<string, object>)
            {
                Dictionary<string, object> intent = (Dictionary<string, object>)value;
                result.RecoveryIntent = new EconomicRecoveryIntent { ShipmentId = ReadString(intent, "shipmentId"), FundsDelta = ReadLong(intent, "fundsDelta") };
            }
            return result;
        }

        private static DepotRegistrySnapshot ParseRegistrySnapshot(Dictionary<string, object> o)
        {
            object rows; List<object> values = o.TryGetValue("depots", out rows) ? rows as List<object> : null;
            DepotRecord[] depots = values == null ? new DepotRecord[0] : values.Select(v => { Dictionary<string, object> d = RequireObject(v, "depot"); return new DepotRecord { DepotId = ReadString(d, "depotId"), MembershipRevision = ReadLong(d, "membershipRevision"), MembershipHash = ReadString(d, "membershipHash"), Active = ReadBool(d, "active") }; }).ToArray();
            if (depots.Length > AcceptedStateV2Limits.MaxDepotRecords) throw new InvalidDataException("Registry snapshot exceeds depot bounds.");
            return new DepotRegistrySnapshot { RegistryVersion = ReadString(o, "registryVersion"), RegistryHash = ReadString(o, "registryHash"), Depots = depots };
        }

        private static RouteVersionRecord ParseRoute(Dictionary<string, object> o)
        {
            object values; List<object> resources = o.TryGetValue("resources", out values) ? values as List<object> : null;
            return new RouteVersionRecord { RouteId = ReadString(o, "routeId"), Revision = ReadLong(o, "revision"), Version = ReadLong(o, "version"),
                SourceDepotId = ReadString(o, "sourceDepotId"), SourceMembershipRevision = ReadLong(o, "sourceMembershipRevision"), SourceMembershipHash = ReadString(o, "sourceMembershipHash"),
                DestinationDepotId = ReadString(o, "destinationDepotId"), DestinationMembershipRevision = ReadLong(o, "destinationMembershipRevision"), DestinationMembershipHash = ReadString(o, "destinationMembershipHash"),
                TravelDurationSeconds = ReadDouble(o, "travelDurationSeconds"), Provenance = ReadString(o, "provenance"), LegacyOpaque = ReadBool(o, "legacyOpaque"), Resources = ParseResources(resources),
                DestinationKind = o.ContainsKey("destinationKind") ? ReadString(o, "destinationKind") : "physicalDepot", FundsPerUnit = o.ContainsKey("fundsPerUnit") ? ReadLong(o, "fundsPerUnit") : 0 };
        }

        private static DeliveryRuleRecord ParseRule(Dictionary<string, object> o)
        {
            return new DeliveryRuleRecord { RuleId = ReadString(o, "ruleId"), Revision = ReadLong(o, "revision"), Kind = ReadString(o, "kind"), RouteId = ReadString(o, "routeId"), RouteVersion = ReadLong(o, "routeVersion"), Enabled = ReadBool(o, "enabled"), NextDueUt = ReadDouble(o, "nextDueUt"), IntervalSeconds = ReadDouble(o, "intervalSeconds"), WaitingRequest = ReadBool(o, "waitingRequest"), WaitingScheduledUt = ReadDouble(o, "waitingScheduledUt"), WaitingCoalescedSlots = ReadLong(o, "waitingCoalescedSlots"), ResourceName = ReadString(o, "resourceName"), LowTriggerMicroUnits = ReadLong(o, "lowTriggerMicroUnits"), TargetMicroUnits = ReadLong(o, "targetMicroUnits"), BatchSizeMicroUnits = ReadLong(o, "batchSizeMicroUnits"), LegacyOpaque = ReadBool(o, "legacyOpaque") };
        }

        private static ActiveShipmentRecord ParseShipment(Dictionary<string, object> o)
        {
            object values; List<object> resources = o.TryGetValue("remainingResources", out values) ? values as List<object> : null;
            return new ActiveShipmentRecord { ShipmentId = ReadString(o, "shipmentId"), Revision = ReadLong(o, "revision"), RouteId = ReadString(o, "routeId"), RouteVersion = ReadLong(o, "routeVersion"), SourceDepotId = ReadString(o, "sourceDepotId"), DestinationDepotId = ReadString(o, "destinationDepotId"), DepartureUt = ReadDouble(o, "departureUt"), DueUt = ReadDouble(o, "dueUt"), RemainingResources = ParseResources(resources), HeldReason = ReadString(o, "heldReason"), LegacyOpaque = ReadBool(o, "legacyOpaque"), DestinationKind = o.ContainsKey("destinationKind") ? ReadString(o, "destinationKind") : "physicalDepot", FundsPerUnit = o.ContainsKey("fundsPerUnit") ? ReadLong(o, "fundsPerUnit") : 0 };
        }

        private static ResourceAmount[] ParseResources(List<object> values)
        {
            if (values == null) return new ResourceAmount[0];
            if (values.Count > AcceptedStateV2Limits.MaxManifestResources) throw new InvalidDataException("Resource manifest exceeds bounds.");
            return values.Select(v => { Dictionary<string, object> row = RequireObject(v, "resource amount"); return new ResourceAmount { ResourceName = ReadString(row, "resourceName"), AmountMicroUnits = ReadLong(row, "amountMicroUnits") }; }).ToArray();
        }

        private static PhysicalEffectIntent ParsePhysicalIntent(Dictionary<string, object> o)
        {
            object value; Dictionary<string, object> capability = o.TryGetValue("capability", out value) ? value as Dictionary<string, object> : null;
            PhysicalEffectIntent result = new PhysicalEffectIntent { EffectKind = ReadString(o, "effectKind"), MembershipRevision = ReadLong(o, "membershipRevision") };
            result.Capability = capability == null ? null : new InventoryCapability { DepotId = ReadString(capability, "depotId"), MembershipRevision = ReadLong(capability, "membershipRevision"), MembershipHash = ReadString(capability, "membershipHash"), AnchorPersistentId = ReadUInt(capability, "anchorPersistentId"), MemberSetHash = ReadString(capability, "memberSetHash"), ProviderId = ReadString(capability, "providerId"), ProviderVersion = ReadString(capability, "providerVersion"), Scene = ReadString(capability, "scene"), ObservationAvailable = ReadBool(capability, "observationAvailable"), ReadSupported = ReadBool(capability, "readSupported"), WriteSupported = ReadBool(capability, "writeSupported"), SynchronousRollbackSupported = ReadBool(capability, "synchronousRollbackSupported"), PersistenceSyncSupported = ReadBool(capability, "persistenceSyncSupported"), HoldReason = ReadString(capability, "holdReason") };
            object ids; List<object> idRows = o.TryGetValue("memberPersistentIds", out ids) ? ids as List<object> : null;
            result.MemberPersistentIds = idRows == null ? new uint[0] : idRows.Select(ReadUIntValue).ToArray();
            object rows; List<object> deltaRows = o.TryGetValue("deltas", out rows) ? rows as List<object> : null;
            result.Deltas = deltaRows == null ? new PhysicalEffectResourceDelta[0] : deltaRows.Select(v => { Dictionary<string, object> row = RequireObject(v, "physical delta"); return new PhysicalEffectResourceDelta { MemberPersistentId = ReadUInt(row, "memberPersistentId"), ResourceName = ReadString(row, "resourceName"), DeltaMicroUnits = ReadLong(row, "deltaMicroUnits") }; }).ToArray();
            return result;
        }

        private static Dictionary<string, object> RequireObject(object value, string label)
        { Dictionary<string, object> result = value as Dictionary<string, object>; return result ?? throw new InvalidDataException("Missing or invalid " + label + " object."); }
        private static uint ReadUInt(Dictionary<string, object> o, string key) { ulong n; object v; if (!o.TryGetValue(key, out v) || !UInt64.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) || n > UInt32.MaxValue) throw new InvalidDataException("Invalid unsigned field " + key); return (uint)n; }
        private static uint ReadUIntValue(object value) { ulong n; if (!UInt64.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) || n > UInt32.MaxValue) throw new InvalidDataException("Invalid unsigned array value"); return (uint)n; }
        private static double ReadDouble(Dictionary<string, object> o, string key) { object v; double n; if (!o.TryGetValue(key, out v) || !Double.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out n) || Double.IsNaN(n) || Double.IsInfinity(n)) throw new InvalidDataException("Invalid numeric field " + key); return n; }
        private static bool ReadBool(Dictionary<string, object> o, string key) { object v; if (!o.TryGetValue(key, out v)) return false; string text = Convert.ToString(v, CultureInfo.InvariantCulture); if (text == "true") return true; if (text == "false" || text == "") return false; throw new InvalidDataException("Invalid boolean field " + key); }

        private static string ReadString(Dictionary<string, object> o, string key) { object v; return o.TryGetValue(key, out v) && v is string ? (string)v : string.Empty; }
        private static long ReadLong(Dictionary<string, object> o, string key) { object v; long n; if (!o.TryGetValue(key, out v) || !long.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) throw new InvalidDataException("Invalid integer field " + key); return n; }
        private static int ReadInt(Dictionary<string, object> o, string key) { long n = ReadLong(o, key); if (n < int.MinValue || n > int.MaxValue) throw new InvalidDataException("Invalid integer field " + key); return (int)n; }
        private static string I(long n) { return n.ToString(CultureInfo.InvariantCulture); }
        private static string F(double n) { if (!SafeFinite(n)) throw new InvalidDataException("Non-finite numeric JSON value"); return n.ToString("R", CultureInfo.InvariantCulture); }
        private static string BoundEffect(string value, int length) { if (string.IsNullOrEmpty(value)) return "Effect unavailable"; value = value.Replace('\r', ' ').Replace('\n', ' '); return value.Length <= length ? value : value.Substring(0, length); }

        private static class MinimalJson
        {
            private sealed class Parser
            {
                private readonly string text;
                private int index;
                private int nodes;
                private int depth;
                public Parser(string value) { text = value; }
                public Dictionary<string, object> Object() { return Object(true); }
                private Dictionary<string, object> Object(bool root)
                {
                    Skip(); Expect('{'); Skip();
                    Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
                    if (Take('}')) { if (root) EnsureEnd(); return result; }
                    while (true)
                    {
                        Skip(); string key = String(); Skip(); Expect(':'); object value = Value();
                        if (result.ContainsKey(key)) throw new InvalidDataException("Duplicate JSON property");
                        result.Add(key, value); if (result.Count > 128) throw new InvalidDataException("Too many JSON fields");
                        Skip(); if (Take('}')) break; Expect(',');
                    }
                    if (root) EnsureEnd();
                    return result;
                }
                private void EnsureEnd() { Skip(); if (index != text.Length) throw new InvalidDataException("Trailing JSON data"); }
                private object Value()
                {
                    if (++nodes > 512) throw new InvalidDataException("JSON value count exceeds bound");
                    Skip(); if (index >= text.Length) throw new InvalidDataException("Truncated JSON value");
                    char c = text[index];
                    if (c == '"') return String();
                    if (c == '{') { EnterDepth(); try { return Object(false); } finally { depth--; } }
                    if (c == '[') { EnterDepth(); try { return Array(); } finally { depth--; } }
                    if (Match("true")) return "true";
                    if (Match("false")) return "false";
                    if (Match("null")) return string.Empty;
                    int start = index;
                    if (c == '-') index++;
                    while (index < text.Length && (char.IsDigit(text[index]) || text[index] == '.' || text[index] == 'e' || text[index] == 'E' || text[index] == '+' || text[index] == '-')) index++;
                    if (index == start) throw new InvalidDataException("Invalid JSON value");
                    string number = text.Substring(start, index - start);
                    double parsed; if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) || double.IsNaN(parsed) || double.IsInfinity(parsed)) throw new InvalidDataException("Invalid JSON number");
                    return number;
                }
                private List<object> Array() { Expect('['); Skip(); List<object> result = new List<object>(); if (Take(']')) return result; while (true) { result.Add(Value()); if (result.Count > 256) throw new InvalidDataException("JSON array exceeds bound"); Skip(); if (Take(']')) return result; Expect(','); } }
                private void EnterDepth() { if (++depth > 16) throw new InvalidDataException("JSON nesting exceeds bound"); }
                private string String()
                {
                    Expect('"'); StringBuilder b = new StringBuilder();
                    while (index < text.Length)
                    {
                        char c = text[index++];
                        if (c == '"') return b.ToString();
                        if (c < 0x20) throw new InvalidDataException("Control character in JSON string");
                        if (c != '\\') { b.Append(c); continue; }
                        if (index >= text.Length) throw new InvalidDataException("Truncated JSON escape");
                        char e = text[index++];
                        switch (e)
                        {
                            case '"': b.Append('"'); break; case '\\': b.Append('\\'); break; case '/': b.Append('/'); break;
                            case 'b': b.Append('\b'); break; case 'f': b.Append('\f'); break; case 'n': b.Append('\n'); break; case 'r': b.Append('\r'); break; case 't': b.Append('\t'); break;
                            case 'u':
                                if (index + 4 > text.Length) throw new InvalidDataException("Truncated unicode escape");
                                int code; if (!int.TryParse(text.Substring(index, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code)) throw new InvalidDataException("Invalid unicode escape");
                                b.Append((char)code); index += 4; break;
                            default: throw new InvalidDataException("Invalid JSON escape");
                        }
                    }
                    throw new InvalidDataException("Unterminated JSON string");
                }
                private bool Match(string value) { if (index + value.Length > text.Length || string.CompareOrdinal(text, index, value, 0, value.Length) != 0) return false; index += value.Length; return true; }
                private void Skip() { while (index < text.Length && (text[index] == ' ' || text[index] == '\t' || text[index] == '\r' || text[index] == '\n')) index++; }
                private bool Take(char value) { if (index < text.Length && text[index] == value) { index++; return true; } return false; }
                private void Expect(char value) { Skip(); if (!Take(value)) throw new InvalidDataException("Invalid JSON structure"); }
            }
            public static Dictionary<string, object> ParseObject(string json)
            {
                if (string.IsNullOrEmpty(json) || Encoding.UTF8.GetByteCount(json) > 65536) throw new InvalidDataException("JSON frame size outside bound");
                return new Parser(json).Object();
            }
        }
    }
}
