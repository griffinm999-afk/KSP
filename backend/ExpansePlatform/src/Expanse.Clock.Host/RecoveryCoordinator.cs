using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Expanse.Clock.Core;
using Expanse.Domain;
using Microsoft.Data.Sqlite;

namespace Expanse.Clock.Host;

internal sealed class RecoveryCoordinator : IDisposable
{
    readonly object gate = new();
    readonly SqliteConnection db;
    readonly IBridgeProcessVerifier processVerifier;
    readonly bool enableDevCounter;
    readonly Func<long> monotonicTimestampProvider;
    readonly string databaseFile;
    ActiveContext? active;
    AcceptedState? accepted;
    string? acceptedHash;
    string? physicalPreflightHoldReason;
    readonly List<TransientShipmentHold> transientArrivalHolds = new();
    bool historyUnavailable;

    sealed record ObservedStock(StockObservation Value, long FirstAt, double FirstAge, long LatestAt, double LatestAge);
    sealed record ActiveContext(string InstallNamespace, string SaveFolder, string WorldId, string RunId, Guid SessionId, Guid LoadEpoch, int ProcessId, long ProcessStartUtcTicks, string ExecutablePath, long ClockContextGeneration, string[] Capabilities, InventoryCapability[] InventoryEndpoints, DepotRegistrySnapshot? RegistrySnapshot, ObservedStock[] ObservedStocks);
    sealed record Prepared(EffectProposal Proposal, string PayloadJson);

    internal const int ScheduledRetryBackoffSeconds = 15;

    public RecoveryCoordinator(string dataDirectory, IBridgeProcessVerifier? processVerifier = null, bool enableDevCounter = false, Func<long>? monotonicTimestampProvider = null)
    {
        this.processVerifier = processVerifier ?? new OperatingSystemBridgeProcessVerifier();
        this.enableDevCounter = enableDevCounter;
        this.monotonicTimestampProvider = monotonicTimestampProvider ?? Stopwatch.GetTimestamp;
        SQLitePCL.Batteries_V2.Init();
        Directory.CreateDirectory(dataDirectory);
        databaseFile = Path.GetFullPath(Path.Combine(dataDirectory, "recovery.sqlite3"));
        db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databaseFile, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared, Pooling = false }.ToString());
        db.Open();
        using (var pragma = db.CreateCommand()) { pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;"; pragma.ExecuteNonQuery(); }
        using var transaction = db.BeginTransaction();
        using (var version = db.CreateCommand()) { version.Transaction = transaction; version.CommandText = "PRAGMA user_version;"; if (Convert.ToInt32(version.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 1) throw new InvalidDataException("Recovery database schema is newer than this Host."); }
        using var command = db.CreateCommand(); command.Transaction = transaction; command.CommandText = @"
CREATE TABLE IF NOT EXISTS HostMeta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS ActiveState(id INTEGER PRIMARY KEY CHECK(id=1), install TEXT NOT NULL, save TEXT NOT NULL, world TEXT NOT NULL, run TEXT NOT NULL, state BLOB NOT NULL, hash TEXT NOT NULL, ownerSession TEXT NOT NULL, ownerEpoch TEXT NOT NULL, ownerPid INTEGER NOT NULL, ownerStartTicks INTEGER NOT NULL, ownerPath TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS Prepared(operationId TEXT PRIMARY KEY, requestId TEXT NOT NULL UNIQUE, world TEXT NOT NULL, run TEXT NOT NULL, sequence INTEGER NOT NULL, revision INTEGER NOT NULL, expectedHash TEXT NOT NULL, payloadHash TEXT NOT NULL, kind TEXT NOT NULL, delta INTEGER NOT NULL, targetWatermark INTEGER NOT NULL, status TEXT NOT NULL, reason TEXT NULL, deliveryJson TEXT NULL);
CREATE TABLE IF NOT EXISTS Settled(operationId TEXT PRIMARY KEY, requestId TEXT NOT NULL, world TEXT NOT NULL, run TEXT NOT NULL DEFAULT '', sequence INTEGER NOT NULL, payloadHash TEXT NOT NULL, kind TEXT NOT NULL DEFAULT 'counterIncrement', deliveryJson TEXT NULL, outcome TEXT NOT NULL, stateHash TEXT NOT NULL, acceptedState BLOB NULL, appliedUt REAL NULL);
PRAGMA user_version=1;"; command.ExecuteNonQuery();
        // Upgrade prior v1 databases without disturbing their prepared counter rows.
        // Early v1 Prepared tables predate operation kinds and compaction watermarks.
        using (var columns = db.CreateCommand()) { columns.Transaction = transaction; columns.CommandText = "PRAGMA table_info(Prepared);"; using var reader = columns.ExecuteReader(); var kind = false; var watermark = false; while (reader.Read()) { if (reader.GetString(1) == "kind") kind = true; if (reader.GetString(1) == "targetWatermark") watermark = true; } reader.Close(); if (!kind) { using var alter = db.CreateCommand(); alter.Transaction = transaction; alter.CommandText = "ALTER TABLE Prepared ADD COLUMN kind TEXT NOT NULL DEFAULT 'counterIncrement';"; alter.ExecuteNonQuery(); } if (!watermark) { using var alter = db.CreateCommand(); alter.Transaction = transaction; alter.CommandText = "ALTER TABLE Prepared ADD COLUMN targetWatermark INTEGER NOT NULL DEFAULT 0;"; alter.ExecuteNonQuery(); } }
        using (var columns = db.CreateCommand()) { columns.Transaction = transaction; columns.CommandText = "PRAGMA table_info(Prepared);"; using var reader = columns.ExecuteReader(); var found = false; while (reader.Read()) if (reader.GetString(1) == "deliveryJson") found = true; reader.Close(); if (!found) { using var alter = db.CreateCommand(); alter.Transaction = transaction; alter.CommandText = "ALTER TABLE Prepared ADD COLUMN deliveryJson TEXT NULL;"; alter.ExecuteNonQuery(); } }
        using (var columns = db.CreateCommand()) { columns.Transaction = transaction; columns.CommandText = "PRAGMA table_info(Settled);"; using var reader = columns.ExecuteReader(); var found = false; while (reader.Read()) if (reader.GetString(1) == "run") found = true; reader.Close(); if (!found) { using var alter = db.CreateCommand(); alter.Transaction = transaction; alter.CommandText = "ALTER TABLE Settled ADD COLUMN run TEXT NOT NULL DEFAULT '';"; alter.ExecuteNonQuery(); } }
        using (var columns = db.CreateCommand()) { columns.Transaction = transaction; columns.CommandText = "PRAGMA table_info(Settled);"; using var reader = columns.ExecuteReader(); var kind = false; var delivery = false; while (reader.Read()) { if (reader.GetString(1) == "kind") kind = true; if (reader.GetString(1) == "deliveryJson") delivery = true; } reader.Close(); if (!kind) { using var alter = db.CreateCommand(); alter.Transaction = transaction; alter.CommandText = "ALTER TABLE Settled ADD COLUMN kind TEXT NOT NULL DEFAULT 'counterIncrement';"; alter.ExecuteNonQuery(); } if (!delivery) { using var alter = db.CreateCommand(); alter.Transaction = transaction; alter.CommandText = "ALTER TABLE Settled ADD COLUMN deliveryJson TEXT NULL;"; alter.ExecuteNonQuery(); } }
        transaction.Commit();
        using (var history = db.CreateCommand()) { history.CommandText = "SELECT value FROM HostMeta WHERE key='historyUnavailable';"; historyUnavailable = Convert.ToString(history.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == "1"; }
    }

    public EffectEnvelope Attach(EffectAttach request, ClockState clock)
    {
        lock (gate)
        {
            if (request.ProtocolVersion != 1 || request.MessageType != "effectAttach" || request.SessionId == Guid.Empty || request.LoadEpoch == Guid.Empty || request.BridgeProcessId <= 0 || request.BridgeProcessStartUtcTicks <= 0 || string.IsNullOrWhiteSpace(request.BridgeExecutablePath) || string.IsNullOrWhiteSpace(request.InstallNamespace) || string.IsNullOrWhiteSpace(request.SaveFolder) || string.IsNullOrWhiteSpace(request.WorldId) || string.IsNullOrWhiteSpace(request.RunId) || request.Capsule is null)
                return Need("Attach identity or capsule is incomplete.");
            if (request.Capabilities is null || request.Capabilities.Length > 16 || request.Capabilities.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 64)) return Need("Capability list is malformed or exceeds bounds.");
            if (request.InventoryEndpoints is null || request.InventoryEndpoints.Length > AcceptedStateCodec.MaxDepots || request.InventoryEndpoints.Any(x => x is null || string.IsNullOrWhiteSpace(x.DepotId) || x.DepotId.Length > 128 || x.MembershipRevision < 0) || request.InventoryEndpoints.Select(x => x.DepotId).Distinct(StringComparer.Ordinal).Count() != request.InventoryEndpoints.Length) return Need("Inventory endpoint identity list is malformed or exceeds bounds.");
            if (request.RegistrySnapshot is not null)
            {
                try
                {
                    if (request.RegistrySnapshot.RegistryHash != OperationIdentity.ComputeDepotRegistryHash(request.RegistrySnapshot.Depots)) return Need("Attach registry fingerprint does not match its exact active depot list.");
                    if (request.RegistrySnapshot.Depots.Length != request.InventoryEndpoints.Length || request.RegistrySnapshot.Depots.Any(d => !request.InventoryEndpoints.Any(e => e.DepotId == d.DepotId && e.MembershipRevision == d.MembershipRevision))) return Need("Attach registry snapshot and inventory endpoint list disagree.");
                }
                catch (ArgumentException ex) { return Need("Attach registry snapshot is invalid: " + Bound(ex.Message)); }
            }
            if (!clock.TryGetFreshGameContext(request.SessionId, request.LoadEpoch, request.InstallNamespace, request.SaveFolder, request.WorldId, request.RunId, out var clockGeneration)) return Need("No matching fresh active clock publisher context is available yet.");
            var processStatus = processVerifier.Verify(request.BridgeProcessId, request.BridgeProcessStartUtcTicks, request.BridgeExecutablePath, request.InstallNamespace, out var processReason);
            if (processStatus != ProcessIdentityState.SameLiveProcess) return Need("Bridge process identity not verified: " + processReason);
            AcceptedState incoming;
            try { incoming = AcceptedStateCodec.ReadCapsule(request.Capsule); }
            catch (Exception ex) when (ex is InvalidDataException or FormatException or ArgumentException) { return Need("Capsule validation failed: " + Bound(ex.Message)); }
            var hash = AcceptedStateCodec.ComputeHash(incoming);
            if (incoming.WorldId != request.WorldId || incoming.CheckpointId != request.CheckpointId || incoming.Revision != request.Revision || incoming.AcceptedSequence != request.AcceptedSequence || incoming.CompactionWatermark != request.CompactionWatermark || hash != request.StateHash)
                return Need("Attach prefix does not match its complete capsule.");
            if (!request.CanWrite || !string.IsNullOrWhiteSpace(request.UnavailableReason)) return Need(Bound(request.UnavailableReason ?? "Bridge does not advertise writable context."));

            var nextContext = new ActiveContext(request.InstallNamespace, request.SaveFolder, request.WorldId, request.RunId, request.SessionId, request.LoadEpoch, request.BridgeProcessId, request.BridgeProcessStartUtcTicks, Path.GetFullPath(request.BridgeExecutablePath), clockGeneration, request.Capabilities, request.InventoryEndpoints, request.RegistrySnapshot, Array.Empty<ObservedStock>());
            using var transaction = db.BeginTransaction();
            var pending = LoadPrepared(transaction);
            var priorOwner = active ?? LoadStoredContext(transaction);
            var priorState = accepted ?? LoadStoredState(transaction);
            if (priorState is null && (incoming.AcceptedSequence > 0 || incoming.Receipts.Length > 0)) { historyUnavailable = true; SetMeta(transaction, "historyUnavailable", "1"); }
            if (priorOwner is not null && (priorOwner.SessionId != nextContext.SessionId || priorOwner.ProcessId != nextContext.ProcessId || priorOwner.ProcessStartUtcTicks != nextContext.ProcessStartUtcTicks))
            {
                var oldOwner = processVerifier.Verify(priorOwner.ProcessId, priorOwner.ProcessStartUtcTicks, priorOwner.ExecutablePath, priorOwner.InstallNamespace, out var oldReason);
                if (oldOwner != ProcessIdentityState.Gone) return Need("Ambiguous writable game owner; prior process is " + (oldOwner == ProcessIdentityState.SameLiveProcess ? "still alive" : "unverifiable") + ": " + oldReason);
            }
            if (pending is not null)
            {
                var represented = Array.Find(incoming.Receipts, r => r.OperationId == pending.Proposal.OperationId && r.CommandSequence == pending.Proposal.CommandSequence && r.PayloadHash == pending.Proposal.PayloadHash && r.OperationKind == pending.Proposal.OperationKind);
                if (represented is not null)
                {
                    if (priorState is null) return Need("Host has no prior accepted state to verify the unacknowledged effect.");
                    PhysicalEffectResult? physicalEvidence = null;
                    if (pending.Proposal.OperationKind is "dispatch" or "arrival")
                    {
                        if (represented.Outcome != "accepted" || represented.PhysicalWitness is null) return Need("Physical operation capsule lacks its preconstructed exact success witness; keep this endpoint held.");
                        physicalEvidence = PhysicalResultFromWitness(pending.Proposal.Delivery?.PhysicalEffect, represented.PhysicalWitness);
                        if (physicalEvidence is null || !PhysicalResultMatches(pending.Proposal.Delivery?.PhysicalEffect, physicalEvidence)) return Need("Persisted physical witness does not match the exact prepared intent; keep this endpoint held.");
                    }
                    if (!TryVerifyProposal(pending.Proposal, priorState, incoming, represented.AppliedUt, physicalEvidence, out var verifyReason,
                        request.Capabilities.Contains(LogisticsRuntimePlanner.OwnershipCapability, StringComparer.Ordinal))) return Need(verifyReason);
                    InsertSettled(transaction, pending.Proposal, represented.Outcome, hash, AcceptedStateCodec.Serialize(incoming), represented.AppliedUt);
                    DeletePrepared(transaction, pending.Proposal.OperationId);
                }
                else
                {
                    if (priorState is null || AcceptedStateCodec.ComputeHash(priorState) != pending.Proposal.ExpectedStateHash || priorState.Revision != pending.Proposal.ExpectedRevision || priorState.AcceptedSequence + 1 != pending.Proposal.CommandSequence || priorOwner is null || priorOwner.WorldId != nextContext.WorldId || priorOwner.RunId != nextContext.RunId || priorOwner.InstallNamespace != nextContext.InstallNamespace || priorOwner.SaveFolder != nextContext.SaveFolder || hash != pending.Proposal.ExpectedStateHash)
                    {
                        // Game schedules can advance while a configuration command
                        // awaits its next poll. Only pure configuration is revalidated
                        // on that newer same-load prefix; physical effects never rebase.
                        if (priorOwner is not null && priorState is not null && priorOwner.WorldId == nextContext.WorldId && priorOwner.RunId == nextContext.RunId &&
                            priorOwner.SessionId == nextContext.SessionId && priorOwner.LoadEpoch == nextContext.LoadEpoch &&
                            request.Capabilities.Contains(LogisticsRuntimePlanner.OwnershipCapability, StringComparer.Ordinal) &&
                            incoming.CompactionWatermark < pending.Proposal.CommandSequence && TryRebaseConfiguration(pending.Proposal, priorState, incoming, clock.CurrentSample?.UtSeconds ?? 0, out var rebased))
                        {
                            DeletePrepared(transaction, pending.Proposal.OperationId);
                            InsertPrepared(transaction, rebased);
                        }
                        else MarkHeld(transaction, pending.Proposal.OperationId, incoming.CompactionWatermark >= pending.Proposal.CommandSequence
                                ? "Selected-save compaction removed this prepared operation's receipt; its outcome is unknown and replay is disabled."
                                : "Attached capsule is not the exact prepared prefix and contains no matching accepted receipt.");
                    }
                }
            }
            // ClockState can transition independently while capsule/database checks run. Never let
            // an attach validated against an earlier generation commit over a newer sampled run.
            if (!clock.TryGetFreshGameContext(request.SessionId, request.LoadEpoch, request.InstallNamespace, request.SaveFolder, request.WorldId, request.RunId, out var finalClockGeneration) || finalClockGeneration != clockGeneration)
                return Need("Clock world/run context changed while validating attach; retry with the current capsule.");
            StoreActive(transaction, nextContext, incoming, hash); transaction.Commit();
            var sameLoad = active is not null && active.WorldId == nextContext.WorldId && active.RunId == nextContext.RunId && active.SessionId == nextContext.SessionId && active.LoadEpoch == nextContext.LoadEpoch;
            active = nextContext; accepted = incoming; acceptedHash = hash;
            if (!sameLoad) { physicalPreflightHoldReason = null; transientArrivalHolds.Clear(); }
            return new EffectIdle { MessageType = "effectIdle", Reason = historyUnavailable ? "Attached from the selected-save capsule; earlier Host history is unavailable." : "Attached to validated accepted state." };
        }
    }

    public SubmitCommandResult Submit(SubmitCommand request)
    {
        lock (gate)
        {
            if (request.ProtocolVersion != 1 || request.MessageType != "submitCommand" || string.IsNullOrWhiteSpace(request.ClientRequestId) || request.ClientRequestId.Length > 128)
                return Reject(request.ClientRequestId, "Invalid request identity or protocol.");
            var isCounter = request.CommandKind == "counterIncrement" && request.CounterDelta > 0;
            var isRoute = request.CommandKind == "routeUpsert" && request.Delivery?.RouteVersion is not null;
            var isRule = request.CommandKind == "ruleUpsert" && request.Delivery?.Rule is not null;
            var isRuleCancel = request.CommandKind == "ruleCancel" && request.Delivery?.Kind == "ruleCancel" && !string.IsNullOrWhiteSpace(request.Delivery.RuleId) && request.Delivery.RuleId.Length <= 128;
            var isSendOnce = request.CommandKind == "sendOnce" && request.Delivery?.Kind == "sendOnce";
            if (!isCounter && !isRoute && !isRule && !isRuleCancel && !isSendOnce) return Reject(request.ClientRequestId, "Command kind or typed payload is unsupported.");
            if (isCounter && !enableDevCounter) return Reject(request.ClientRequestId, "The stage 3a counter command is disabled in packaged Host mode.");
            if (active is null || accepted is null || request.WorldId != active.WorldId || request.RunId != active.RunId) return Reject(request.ClientRequestId, "Command context is not the active attached run.");
            if (accepted.WritesBlocked) return Reject(request.ClientRequestId, "Accepted state contains an unresolved physical fault; all new writes are blocked.");
            if (isRoute)
            {
                var route = request.Delivery!.RouteVersion!;
                if (IsRecoveryRoute(route) && route.FundsPerUnit != OreExportPolicy.DefaultFundsPerUnit)
                    return Reject(request.ClientRequestId, "New Ore export routes must use 100 funds per unit. Departed cargo retains its saved terms.");
                if (accepted.SchemaVersion != 2 || active.RegistrySnapshot is null || accepted.DepotRegistryVersion != active.RegistrySnapshot.RegistryVersion || accepted.DepotRegistryHash != active.RegistrySnapshot.RegistryHash)
                    return Reject(request.ClientRequestId, "The fenced depot registry snapshot must be accepted before route configuration.");
                foreach (var endpointId in IsRecoveryRoute(route) ? new[] { route.SourceDepotId } : new[] { route.SourceDepotId, route.DestinationDepotId })
                {
                    var saved = accepted.Depots.SingleOrDefault(x => x.Active && x.DepotId == endpointId);
                    var observed = active.RegistrySnapshot?.Depots.SingleOrDefault(x => x.DepotId == endpointId);
                    var routeMembershipHash = endpointId == route.SourceDepotId ? route.SourceMembershipHash : route.DestinationMembershipHash;
                    if (saved is null || observed is null || saved.MembershipRevision != observed.MembershipRevision || saved.MembershipHash != routeMembershipHash || observed.MembershipHash != routeMembershipHash || !active.InventoryEndpoints.Any(x => x.DepotId == endpointId && x.MembershipRevision == observed.MembershipRevision))
                        return Reject(request.ClientRequestId, "Route endpoints lack matching fenced registry depot identities and membership revisions.");
                }
            }
            if (isRule)
            {
                var rule = request.Delivery!.Rule!;
                // Pausing an unchanged existing order grants no cargo effect and
                // remains available even when its historical endpoint is stale.
                if (!IsDisableOnly(accepted, rule))
                {
                if (accepted.SchemaVersion != 2 || active.RegistrySnapshot is null || accepted.DepotRegistryVersion != active.RegistrySnapshot.RegistryVersion || accepted.DepotRegistryHash != active.RegistrySnapshot.RegistryHash)
                    return Reject(request.ClientRequestId, "The fenced depot registry snapshot must be accepted before rule configuration.");
                var route = accepted.RouteVersions.SingleOrDefault(x => !x.LegacyOpaque && x.RouteId == rule.RouteId && x.Version == rule.RouteVersion);
                if (route is null || !RouteEndpointsCurrent(route, accepted))
                    return Reject(request.ClientRequestId, "Rule route endpoints are removed or changed in the active depot registry.");
                if (IsRecoveryRoute(route) && route.FundsPerUnit != OreExportPolicy.DefaultFundsPerUnit)
                    return Reject(request.ClientRequestId, "Create a new export route at 100 funds per unit before configuring an automatic order.");
                if (!rule.LegacyOpaque && rule.Kind == "keepStock" && (route.Resources.Length != 1 || route.Resources[0].ResourceName != rule.ResourceName || route.Resources[0].AmountMicroUnits != rule.BatchSizeMicroUnits))
                    return Reject(request.ClientRequestId, "A keep-stock route must carry exactly its configured resource at the rule's batch-size cap.");
                }
            }
            var existing = FindByRequest(request.ClientRequestId);
            if (existing is not null)
            {
                if (!isSendOnce && existing.Proposal.PayloadHash != (isCounter ? OperationIdentity.CounterIncrementPayloadHash(request.CounterDelta) : isRoute ? OperationIdentity.RouteVersionPayloadHash(request.Delivery!.RouteVersion!) : isRule ? OperationIdentity.RulePayloadHash(request.Delivery!.Rule!) : OperationIdentity.RuleCancelPayloadHash(request.Delivery!.RuleId))) return Reject(request.ClientRequestId, "Client request ID was reused with different payload.");
                if (isRule && existing.Proposal.Delivery?.Rule?.Revision != request.Delivery!.Rule!.Revision) return Reject(request.ClientRequestId, "Client request ID was reused with a different rule revision.");
                if (isSendOnce && (existing.Proposal.OperationKind != "dispatch" || existing.Proposal.Delivery?.Shipment?.RouteId != request.Delivery!.RouteId || existing.Proposal.Delivery.Shipment.RouteVersion != request.Delivery.RouteVersionNumber)) return Reject(request.ClientRequestId, "Client request ID was reused with different route selection.");
                var status = existing.Proposal.MessageType == "settled" ? "accepted" : existing.Proposal.MessageType == "held" ? "rejected" : "pending";
                return new SubmitCommandResult { ProtocolVersion = 1, MessageType = "submitCommandResult", ClientRequestId = request.ClientRequestId, Status = status, ConfirmedTerminal = existing.Proposal.MessageType == "held", OperationId = existing.Proposal.OperationId, Reason = status == "rejected" ? "Prior attempt was held." : null };
            }
            if (isRule)
            {
                var rule = request.Delivery!.Rule!;
                var existingRule = accepted.DeliveryRules.SingleOrDefault(x => x.RuleId == rule.RuleId);
                if (rule.Revision != (existingRule is null ? 1 : existingRule.Revision + 1)) return Reject(request.ClientRequestId, "Rule revision is stale.");
            }
            if (LoadPrepared(null) is not null) return Reject(request.ClientRequestId, "Another command is already prepared for this world.");
            if (accepted.Receipts.Length >= AcceptedStateCodec.MaxReceipts)
            {
                PrepareCompaction();
                return new SubmitCommandResult { ProtocolVersion = 1, MessageType = "submitCommandResult", ClientRequestId = request.ClientRequestId, Status = "rejected", Reason = "A verified accepted-state compaction was prepared; retry the same request ID after it settles." };
            }

            DeliveryEffectPayload? effect = null;
            string payloadHash;
            try
            {
                if (isCounter) payloadHash = OperationIdentity.CounterIncrementPayloadHash(request.CounterDelta);
                else if (isRoute) payloadHash = OperationIdentity.RouteVersionPayloadHash(request.Delivery!.RouteVersion!);
                else if (isRule) payloadHash = OperationIdentity.RulePayloadHash(request.Delivery!.Rule!);
                else if (isRuleCancel)
                {
                    if (accepted.SchemaVersion != 2 || !accepted.DeliveryRules.Any(x => x.RuleId == request.Delivery!.RuleId && !x.LegacyOpaque)) return Reject(request.ClientRequestId, "The named automatic order does not exist.");
                    payloadHash = OperationIdentity.RuleCancelPayloadHash(request.Delivery!.RuleId);
                }
                else
                {
                    if (accepted.SchemaVersion != 2 || active.RegistrySnapshot is null || accepted.DepotRegistryHash != active.RegistrySnapshot.RegistryHash) return Reject(request.ClientRequestId, "The fenced depot registry snapshot must be accepted before dispatch.");
                    var route = accepted.RouteVersions.SingleOrDefault(x => !x.LegacyOpaque && x.RouteId == request.Delivery!.RouteId && x.Version == request.Delivery.RouteVersionNumber);
                    if (route is null || !RouteEndpointsCurrent(route, accepted)) return Reject(request.ClientRequestId, "Selected immutable route version has a removed or changed endpoint.");
                    if (IsRecoveryRoute(route) && route.FundsPerUnit != OreExportPolicy.DefaultFundsPerUnit) return Reject(request.ClientRequestId, "Historical export terms apply only to departed cargo; create a new 100-funds route to dispatch.");
                    if (IsRecoveryRoute(route) && !active.Capabilities.Contains("economicRecovery.v1", StringComparer.Ordinal)) return Reject(request.ClientRequestId, "Modeled recovery funds capability is unavailable; export cargo will not be debited.");
                    if (LoadPrepared(null) is not null) return Reject(request.ClientRequestId, "Another command is already prepared for this world.");
                    var sequenceForDispatch = accepted.AcceptedSequence + 1;
                    var operationForDispatch = OperationIdentity.Create(accepted.WorldId, sequenceForDispatch, request.ClientRequestId);
                    var shipmentId = "shipment:" + operationForDispatch;
                    var source = GetFreshObservation(route.SourceDepotId, request.WorldId);
                    var capability = FindWritableCapability(route.SourceDepotId, route.SourceMembershipRevision, route.SourceMembershipHash);
                    if (source is null || capability is null) return Reject(request.ClientRequestId, "Fresh source stock or the complete rollback/persistence write capability is unavailable.");
                    var manifest = ToManifest(route);
                    var planned = LogisticsPlanner.PlanDispatch(manifest, source, shipmentId, 0);
                    if (planned.Outcome != "dispatch") return Reject(request.ClientRequestId, planned.Reason);
                    var intent = BuildIntent("dispatchDebit", capability, source, planned.Debits, debit: true, out var intentHold);
                    if (intent is null) return Reject(request.ClientRequestId, intentHold);
                    var shipment = new ActiveShipmentRecord { ShipmentId = shipmentId, RouteId = route.RouteId, RouteVersion = route.Version, SourceDepotId = route.SourceDepotId, DestinationDepotId = route.DestinationDepotId, DestinationKind = route.DestinationKind, FundsPerUnit = route.FundsPerUnit, DepartureUt = 0, DueUt = 0, RemainingResources = planned.Shipment!.RemainingResources };
                    effect = new DeliveryEffectPayload { Kind = "dispatch", Shipment = shipment, PhysicalEffect = intent };
                    payloadHash = OperationIdentity.DispatchPayloadHash(shipment, intent);
                }
            }
            catch (ArgumentException ex) { return Reject(request.ClientRequestId, ex.Message); }

            var sequence = accepted.AcceptedSequence + 1;
            var operationId = OperationIdentity.Create(accepted.WorldId, sequence, request.ClientRequestId);
            var proposal = new EffectProposal { ProtocolVersion = 1, MessageType = "effectProposal", SessionId = active.SessionId, LoadEpoch = active.LoadEpoch, InstallNamespace = active.InstallNamespace, SaveFolder = active.SaveFolder,
                WorldId = active.WorldId, RunId = active.RunId, OperationId = operationId, ClientRequestId = request.ClientRequestId,
                CommandSequence = sequence, ExpectedRevision = accepted.Revision, ExpectedStateHash = acceptedHash!, PayloadHash = payloadHash,
                OperationKind = isSendOnce ? "dispatch" : request.CommandKind, CounterDelta = request.CounterDelta,
                Delivery = effect ?? (isRoute ? new DeliveryEffectPayload { Kind = "routeUpsert", RouteVersion = request.Delivery!.RouteVersion } : isRule ? new DeliveryEffectPayload { Kind = "ruleUpsert", Rule = request.Delivery!.Rule } : isRuleCancel ? new DeliveryEffectPayload { Kind = "ruleCancel", RuleId = request.Delivery!.RuleId } : null) };
            if ((proposal.OperationKind is "dispatch" or "arrival") && !PreflightPhysicalProposal(proposal, 0, out var preflightReason))
            {
                var compactPrepared = IsWitnessBoundFailure(preflightReason) && PrepareCompactionForPhysicalWitness();
                return Reject(request.ClientRequestId, compactPrepared ? "A verified accepted-state compaction was prepared because the physical success witness would exceed capsule bounds; retry after it settles." : "Physical operation held before preparation: " + preflightReason);
            }
            using var transaction = db.BeginTransaction(); InsertPrepared(transaction, proposal); transaction.Commit();
            return new SubmitCommandResult { ProtocolVersion = 1, MessageType = "submitCommandResult", ClientRequestId = request.ClientRequestId, Status = "pending", OperationId = operationId };
        }
    }

    public SubmitCommandResult GetCommandStatus(GetCommandStatus request)
    {
        lock (gate)
        {
            if (request.ProtocolVersion != 1 || request.MessageType != "getCommandStatus" || String.IsNullOrWhiteSpace(request.ClientRequestId) || request.ClientRequestId.Length > 128 || String.IsNullOrWhiteSpace(request.WorldId) || request.WorldId.Length > 128 || String.IsNullOrWhiteSpace(request.RunId) || request.RunId.Length > 128) return new SubmitCommandResult { ProtocolVersion = 1, MessageType = "commandStatus", ClientRequestId = request.ClientRequestId ?? "", Status = "rejected", Reason = "Invalid status query identity/context." };
            using (var prepared = db.CreateCommand())
            {
                prepared.CommandText = "SELECT operationId,status,reason,world,run FROM Prepared WHERE requestId=$r"; prepared.Parameters.AddWithValue("$r", request.ClientRequestId);
                using var reader = prepared.ExecuteReader();
                if (reader.Read())
                {
                    var status = reader.GetString(1);
                    var sameRun = reader.GetString(3) == request.WorldId && reader.GetString(4) == request.RunId;
                    var attached = active?.WorldId == request.WorldId && active?.RunId == request.RunId;
                    return new SubmitCommandResult { ProtocolVersion = 1, MessageType = "commandStatus", ClientRequestId = request.ClientRequestId,
                        OperationId = reader.GetString(0), Status = !sameRun ? "historical" : !attached ? "unknown" : status == "prepared" ? "pending" : "rejected",
                        ConfirmedTerminal = sameRun && attached && status == "held",
                        Reason = !sameRun ? "This request belongs to a different world/run than the attached save."
                            : !attached ? "Matching game context is not attached; the operation outcome remains unresolved."
                            : reader.IsDBNull(2) ? null : reader.GetString(2) };
                }
            }
            using (var settled = db.CreateCommand())
            {
                settled.CommandText = "SELECT operationId,outcome,stateHash,acceptedState,appliedUt,world,run FROM Settled WHERE requestId=$r"; settled.Parameters.AddWithValue("$r", request.ClientRequestId);
                using var reader = settled.ExecuteReader();
                if (reader.Read())
                {
                    if (reader.GetString(5) != request.WorldId || reader.GetString(6) != request.RunId) return new SubmitCommandResult { ProtocolVersion = 1, MessageType = "commandStatus", ClientRequestId = request.ClientRequestId, OperationId = reader.GetString(0), Status = "historical", Reason = "This request belongs to a different world/run than the attached save." };
                    if (active?.WorldId != request.WorldId || active?.RunId != request.RunId) return new SubmitCommandResult { ProtocolVersion = 1, MessageType = "commandStatus", ClientRequestId = request.ClientRequestId, OperationId = reader.GetString(0), Status = "unknown", Reason = "Matching game context is not attached; settled state cannot be projected yet." };
                    var stateBytes = reader.IsDBNull(3) ? null : (byte[])reader[3];
                    if (stateBytes is null) return new SubmitCommandResult { ProtocolVersion = 1, MessageType = "commandStatus", ClientRequestId = request.ClientRequestId, OperationId = reader.GetString(0), Status = "unknown", Reason = "Settled outcome is known but full accepted state was not retained." };
                    var state = AcceptedStateCodec.Deserialize(stateBytes); var hash = AcceptedStateCodec.ComputeHash(state);
                    if (hash != reader.GetString(2) || state.WorldId != request.WorldId) return new SubmitCommandResult { ProtocolVersion = 1, MessageType = "commandStatus", ClientRequestId = request.ClientRequestId, OperationId = reader.GetString(0), Status = "unknown", Reason = "Settled state hash or world identity failed verification." };
                    return new SubmitCommandResult { ProtocolVersion = 1, MessageType = "commandStatus", ClientRequestId = request.ClientRequestId, OperationId = reader.GetString(0), Status = reader.GetString(1) == "faulted" ? "faulted" : "accepted", AcceptedOutcome = reader.GetString(1), AcceptedRevision = state.Revision, AcceptedSequence = state.AcceptedSequence, StateHash = hash, AcceptedCapsule = AcceptedStateCodec.CreateCapsule(state) };
                }
            }
            return new SubmitCommandResult { ProtocolVersion = 1, MessageType = "commandStatus", ClientRequestId = request.ClientRequestId, Status = "unknown", Reason = historyUnavailable ? "Host history is unavailable; status must be recovered from the selected-save capsule." : "No command with this request ID is known." };
        }
    }

    public AcceptedStateQueryResult GetAcceptedState(GetAcceptedState request)
    {
        lock (gate)
        {
            if (request.ProtocolVersion != 1 || request.MessageType != "getAcceptedState" || string.IsNullOrWhiteSpace(request.WorldId) || request.WorldId.Length > 128 || string.IsNullOrWhiteSpace(request.RunId) || request.RunId.Length > 128)
                return new AcceptedStateQueryResult { ProtocolVersion = 1, MessageType = "acceptedState", Status = "unavailable", Reason = "Invalid world/run query." };
            if (active is null || accepted is null || active.WorldId != request.WorldId || active.RunId != request.RunId)
                return new AcceptedStateQueryResult { ProtocolVersion = 1, MessageType = "acceptedState", Status = "unavailable", WorldId = request.WorldId, RunId = request.RunId, Reason = "No matching writable game context is attached." };
            var hash = AcceptedStateCodec.ComputeHash(accepted);
            if (hash != acceptedHash) return new AcceptedStateQueryResult { ProtocolVersion = 1, MessageType = "acceptedState", Status = "unavailable", WorldId = request.WorldId, RunId = request.RunId, Reason = "Attached accepted state failed its current hash check." };
            var capsule = AcceptedStateCodec.CreateCapsule(accepted);
            if (System.Text.Encoding.UTF8.GetByteCount(capsule.StateBytesBase64) > AcceptedStateCodec.MaxEncodedCapsuleBytes) return new AcceptedStateQueryResult { ProtocolVersion = 1, MessageType = "acceptedState", Status = "unavailable", WorldId = request.WorldId, RunId = request.RunId, Reason = "Accepted state exceeds the read projection bound." };
            return new AcceptedStateQueryResult { ProtocolVersion = 1, MessageType = "acceptedState", Status = "available", WorldId = active.WorldId, RunId = active.RunId, Revision = accepted.Revision, AcceptedSequence = accepted.AcceptedSequence, StateHash = hash, AcceptedCapsule = capsule, TransientShipmentHolds = transientArrivalHolds.ToArray() };
        }
    }

    public DeliveryReadinessResult GetDeliveryReadiness(GetDeliveryReadiness request)
    {
        lock (gate)
        {
            DeliveryReadinessResult Held(string reason) => new() { ProtocolVersion = 1, MessageType = "deliveryReadiness",
                Status = "held", WorldId = request.WorldId ?? "", RunId = request.RunId ?? "", RouteId = request.RouteId ?? "",
                RouteVersion = request.RouteVersion, Reason = Bound(reason) };
            if (request.ProtocolVersion != 1 || request.MessageType != "getDeliveryReadiness" ||
                String.IsNullOrWhiteSpace(request.WorldId) || request.WorldId.Length > 128 ||
                String.IsNullOrWhiteSpace(request.RunId) || request.RunId.Length > 128 ||
                String.IsNullOrWhiteSpace(request.RouteId) || request.RouteId.Length > 128 || request.RouteVersion <= 0)
                return Held("Selected route or world/run identity is invalid.");
            if (active is null || accepted is null || active.WorldId != request.WorldId || active.RunId != request.RunId ||
                accepted.WorldId != request.WorldId || AcceptedStateCodec.ComputeHash(accepted) != acceptedHash)
                return Held("No matching writable game and accepted state are attached.");
            if (accepted.WritesBlocked) return Held("An unresolved physical fault blocks deliveries.");
            if (!active.Capabilities.Contains("dispatch", StringComparer.Ordinal) || !active.Capabilities.Contains("arrival", StringComparer.Ordinal))
                return Held("The connected Bridge does not advertise dispatch and arrival effects.");
            if (active.RegistrySnapshot is null || active.RegistrySnapshot.RegistryHash != accepted.DepotRegistryHash)
                return Held("Selected depot registry has not matched the accepted save state.");
            RouteVersionRecord? route = accepted.RouteVersions.SingleOrDefault(x => !x.LegacyOpaque &&
                x.RouteId == request.RouteId && x.Version == request.RouteVersion);
            if (route is null) return Held("Selected saved route is unavailable in the accepted game state.");
            if (IsRecoveryRoute(route) && route.FundsPerUnit != OreExportPolicy.DefaultFundsPerUnit) return Held("Historical price retained for departed cargo. New dispatch requires a 100-funds export route.");
            if (!RouteEndpointsCurrent(route, accepted)) return Held("Selected route depot membership changed; save a new route version.");
            InventoryCapability? source = active.InventoryEndpoints.SingleOrDefault(x => x.DepotId == route.SourceDepotId &&
                x.MembershipRevision == route.SourceMembershipRevision && x.MembershipHash == route.SourceMembershipHash);
            InventoryCapability? destination = active.InventoryEndpoints.SingleOrDefault(x => x.DepotId == route.DestinationDepotId &&
                x.MembershipRevision == route.DestinationMembershipRevision && x.MembershipHash == route.DestinationMembershipHash);
            if (source is null) return Held("Source depot has no matching physical inventory endpoint.");
            if (FindWritableCapability(source.DepotId, source.MembershipRevision, source.MembershipHash) is null)
                return Held(String.IsNullOrWhiteSpace(source.HoldReason) ? "Source physical inventory cannot debit and roll back selected tanks." : source.HoldReason);
            if (IsRecoveryRoute(route))
            {
                if (!active.Capabilities.Contains("economicRecovery.v1", StringComparer.Ordinal)) return Held("The Bridge does not advertise modeled recovery funds settlement.");
                var exportStock = GetFreshObservation(source.DepotId, request.WorldId);
                if (exportStock is null || BuildIntent("dispatchDebit", source, exportStock, route.Resources, true, out _) is null)
                    return Held("The complete configured Ore batch is unavailable at the source.");
                if (LoadPrepared(null) is not null) return Held("Another accepted operation is finishing.");
                return new DeliveryReadinessResult { ProtocolVersion = 1, MessageType = "deliveryReadiness", Status = "ready", WorldId = request.WorldId, RunId = request.RunId, RouteId = route.RouteId, RouteVersion = route.Version, SourceProviderId = source.ProviderId, DestinationProviderId = "KSP.Funding", AcceptedSequence = accepted.AcceptedSequence };
            }
            if (destination is null) return Held("Destination depot has no matching physical inventory endpoint.");
            if (FindWritableCapability(destination.DepotId, destination.MembershipRevision, destination.MembershipHash) is null)
                return Held(String.IsNullOrWhiteSpace(destination.HoldReason) ? "Destination physical inventory cannot credit and roll back selected tanks." : destination.HoldReason);
            StockObservation? sourceStock = GetFreshObservation(source.DepotId, request.WorldId);
            StockObservation? destinationStock = GetFreshObservation(destination.DepotId, request.WorldId);
            if (sourceStock is null || sourceStock.MembershipRevision != source.MembershipRevision || sourceStock.MembershipHash != source.MembershipHash)
                return Held("Fresh authoritative source tank stock is unavailable.");
            if (destinationStock is null || destinationStock.MembershipRevision != destination.MembershipRevision || destinationStock.MembershipHash != destination.MembershipHash)
                return Held("Fresh authoritative destination tank capacity is unavailable.");
            if (BuildIntent("dispatchDebit", source, sourceStock, route.Resources, true, out string allocationHold) is null)
                return Held(allocationHold);
            if (BuildIntent("arrivalCredit", destination, destinationStock, route.Resources, false, out string capacityHold) is null)
                return Held(capacityHold);
            if (LoadPrepared(null) is not null) return Held("Another accepted operation is finishing; retry when it settles.");
            return new DeliveryReadinessResult { ProtocolVersion = 1, MessageType = "deliveryReadiness", Status = "ready",
                WorldId = request.WorldId, RunId = request.RunId, RouteId = route.RouteId, RouteVersion = route.Version,
                Reason = "", SourceProviderId = source.ProviderId, DestinationProviderId = destination.ProviderId,
                AcceptedSequence = accepted.AcceptedSequence };
        }
    }

    void PrepareCompaction()
    {
        var target = Math.Max(accepted!.CompactionWatermark, accepted.AcceptedSequence - 16);
        var sequence = accepted.AcceptedSequence + 1; var requestId = "compact:" + accepted.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + target.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var payloadHash = OperationIdentity.CompactionPayloadHash(target); var operationId = OperationIdentity.Create(accepted.WorldId, sequence, requestId);
        var proposal = new EffectProposal { ProtocolVersion = 1, MessageType = "effectProposal", SessionId = active!.SessionId, LoadEpoch = active.LoadEpoch, InstallNamespace = active.InstallNamespace, SaveFolder = active.SaveFolder, WorldId = active.WorldId, RunId = active.RunId, OperationId = operationId, ClientRequestId = requestId, CommandSequence = sequence, ExpectedRevision = accepted.Revision, ExpectedStateHash = acceptedHash!, PayloadHash = payloadHash, OperationKind = "compact", CounterDelta = 0, TargetCompactionWatermark = target };
        using var transaction = db.BeginTransaction(); InsertPrepared(transaction, proposal); transaction.Commit();
    }

    void PrepareRegistrySync()
    {
        var snapshot = active?.RegistrySnapshot ?? throw new InvalidOperationException("No bridge-authored registry snapshot is attached.");
        var sequence = accepted!.AcceptedSequence + 1;
        // Include the selected branch prefix and run in the internal identity. A held operation from
        // another run remains archival and cannot block a safe metadata sync rebuilt from this snapshot.
        var identityText = string.Join("|", accepted.WorldId, active!.RunId, accepted.CheckpointId, accepted.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture), accepted.AcceptedSequence.ToString(System.Globalization.CultureInfo.InvariantCulture), acceptedHash, snapshot.RegistryVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), snapshot.RegistryHash);
        var identityHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identityText))).ToLowerInvariant();
        var requestId = "syncDepots:" + identityHash;
        var payloadHash = OperationIdentity.DepotRegistryPayloadHash(snapshot);
        var operationId = OperationIdentity.Create(accepted.WorldId, sequence, requestId);
        var proposal = new EffectProposal { ProtocolVersion = 1, MessageType = "effectProposal", SessionId = active.SessionId, LoadEpoch = active.LoadEpoch, InstallNamespace = active.InstallNamespace, SaveFolder = active.SaveFolder, WorldId = active.WorldId, RunId = active.RunId, OperationId = operationId, ClientRequestId = requestId, CommandSequence = sequence, ExpectedRevision = accepted.Revision, ExpectedStateHash = acceptedHash!, PayloadHash = payloadHash, OperationKind = "syncDepots", CounterDelta = 0, Delivery = new DeliveryEffectPayload { Kind = "syncDepots", DepotRegistrySnapshot = snapshot } };
        using var transaction = db.BeginTransaction(); InsertPrepared(transaction, proposal); transaction.Commit();
    }

    static RouteManifest ToManifest(RouteVersionRecord route) => new() { RouteId = route.RouteId, Version = route.Version, SourceDepotId = route.SourceDepotId, DestinationDepotId = route.DestinationDepotId, TravelDurationSeconds = route.TravelDurationSeconds, Provenance = route.Provenance, Resources = route.Resources.Select(x => new ResourceAmount { ResourceName = x.ResourceName, AmountMicroUnits = x.AmountMicroUnits }).ToArray() };
    static bool IsRecoveryRoute(RouteVersionRecord route) => route.DestinationKind == OreExportPolicy.VirtualDestinationKind;
    static bool IsDisableOnly(AcceptedState state, DeliveryRuleRecord rule)
    {
        var prior = state.DeliveryRules.SingleOrDefault(x => x.RuleId == rule.RuleId && !x.LegacyOpaque);
        if (prior is null || rule.Enabled || rule.Revision != prior.Revision + 1) return false;
        var disabled = CloneScheduleRule(prior); disabled.Enabled = false; disabled.Revision = rule.Revision;
        try { return OperationIdentity.RulePayloadHash(disabled) == OperationIdentity.RulePayloadHash(rule); }
        catch (ArgumentException) { return false; }
    }
    static bool RouteEndpointsCurrent(RouteVersionRecord route, AcceptedState state)
        => state.Depots.Any(d => d.Active && d.DepotId == route.SourceDepotId && d.MembershipRevision == route.SourceMembershipRevision && d.MembershipHash == route.SourceMembershipHash)
        && RouteDestinationCurrent(route, state);
    static bool RouteDestinationCurrent(RouteVersionRecord route, AcceptedState state)
        => IsRecoveryRoute(route) || state.Depots.Any(d => d.Active && d.DepotId == route.DestinationDepotId && d.MembershipRevision == route.DestinationMembershipRevision && d.MembershipHash == route.DestinationMembershipHash);

    static bool TryRebaseConfiguration(EffectProposal pending, AcceptedState prior, AcceptedState selected, double ut, out EffectProposal rebased)
    {
        rebased = null!;
        if (selected.WritesBlocked || selected.WorldId != prior.WorldId || selected.AcceptedSequence <= prior.AcceptedSequence || selected.AcceptedSequence == long.MaxValue ||
            pending.ExpectedRevision != prior.Revision || pending.ExpectedStateHash != AcceptedStateCodec.ComputeHash(prior) || pending.CommandSequence != prior.AcceptedSequence + 1 ||
            pending.OperationKind is not ("routeUpsert" or "ruleUpsert" or "ruleCancel")) return false;
        var delivery = pending.Delivery;
        if (delivery is null) return false;
        var rule = delivery.Rule;
        if (pending.OperationKind == "ruleUpsert")
        {
            if (rule is null) return false;
            var old = prior.DeliveryRules.SingleOrDefault(x => x.RuleId == rule.RuleId);
            var current = selected.DeliveryRules.SingleOrDefault(x => x.RuleId == rule.RuleId);
            if ((old is null) != (current is null)) return false;
            if (old is not null && current is not null)
            {
                if (current.Revision == long.MaxValue) return false;
                // Permit only revisions/slot progress caused by dispatches. A
                // changed user policy or route must keep the stale edit held.
                var scheduleNeutral = CloneScheduleRule(current);
                scheduleNeutral.Revision = old.Revision; scheduleNeutral.NextDueUt = old.NextDueUt;
                scheduleNeutral.WaitingRequest = old.WaitingRequest; scheduleNeutral.WaitingScheduledUt = old.WaitingScheduledUt;
                scheduleNeutral.WaitingCoalescedSlots = old.WaitingCoalescedSlots;
                if (OperationIdentity.RulePayloadHash(scheduleNeutral) != OperationIdentity.RulePayloadHash(old)) return false;
                rule = CloneScheduleRule(rule); rule.Revision = current.Revision + 1;
                delivery = new DeliveryEffectPayload { Kind = "ruleUpsert", Rule = rule };
            }
        }
        var sequence = selected.AcceptedSequence + 1;
        var candidate = new EffectProposal { MessageType = "effectProposal", SessionId = pending.SessionId, LoadEpoch = pending.LoadEpoch,
            InstallNamespace = pending.InstallNamespace, SaveFolder = pending.SaveFolder, WorldId = pending.WorldId, RunId = pending.RunId,
            ClientRequestId = pending.ClientRequestId, CommandSequence = sequence, ExpectedRevision = selected.Revision,
            ExpectedStateHash = AcceptedStateCodec.ComputeHash(selected), OperationKind = pending.OperationKind, Delivery = delivery,
            PayloadHash = pending.PayloadHash, OperationId = OperationIdentity.Create(selected.WorldId, sequence, pending.ClientRequestId) };
        StateTransitionResult validation;
        if (candidate.OperationKind == "routeUpsert" && delivery.RouteVersion is not null)
            validation = AcceptedStateCodec.UpsertRoute(selected,candidate.OperationId,candidate.ClientRequestId,sequence,candidate.PayloadHash,candidate.ExpectedRevision,candidate.ExpectedStateHash,delivery.RouteVersion,ut);
        else if (candidate.OperationKind == "ruleUpsert" && rule is not null)
            validation = AcceptedStateCodec.UpsertRule(selected,candidate.OperationId,candidate.ClientRequestId,sequence,candidate.PayloadHash,candidate.ExpectedRevision,candidate.ExpectedStateHash,rule,ut);
        else if (candidate.OperationKind == "ruleCancel")
            validation = AcceptedStateCodec.CancelRule(selected,candidate.OperationId,candidate.ClientRequestId,sequence,candidate.PayloadHash,candidate.ExpectedRevision,candidate.ExpectedStateHash,delivery.RuleId,ut);
        else return false;
        if (validation.Outcome != "accepted") return false;
        rebased = candidate; return true;
    }

    StockObservation? GetFreshObservation(string depotId, string worldId)
    {
        if (active is null) return null;
        var held = active.ObservedStocks.SingleOrDefault(x => x.Value.DepotId == depotId && x.Value.WorldId == worldId && x.Value.SessionId == active.SessionId && x.Value.LoadEpoch == active.LoadEpoch);
        if (held is null) return null;
        var now = Environment.TickCount64;
        var ageA = held.FirstAge + Math.Max(0, now - held.FirstAt) / 1000d;
        var ageB = held.LatestAge + Math.Max(0, now - held.LatestAt) / 1000d;
        var age = Math.Max(ageA, ageB);
        if (age > 6 || !held.Value.Available || !held.Value.MicroUnitProjectionSafe) return null;
        return new StockObservation { DepotId = held.Value.DepotId, SessionId = held.Value.SessionId, LoadEpoch = held.Value.LoadEpoch, WorldId = held.Value.WorldId, RegistryHash = held.Value.RegistryHash, MembershipRevision = held.Value.MembershipRevision, MembershipHash = held.Value.MembershipHash, ObservationRevision = held.Value.ObservationRevision, ObservedUt = held.Value.ObservedUt, AgeSeconds = age, MemberStocks = held.Value.MemberStocks, Available = held.Value.Available, MicroUnitProjectionSafe = held.Value.MicroUnitProjectionSafe, Resources = held.Value.Resources, UnavailableReason = held.Value.UnavailableReason };
    }

    InventoryCapability? FindWritableCapability(string depotId, long membershipRevision, string membershipHash)
    {
        var capability = active?.InventoryEndpoints.SingleOrDefault(x => x.DepotId == depotId && x.MembershipRevision == membershipRevision && x.MembershipHash == membershipHash);
        return capability is not null && capability.ObservationAvailable && capability.ReadSupported && capability.WriteSupported && capability.SynchronousRollbackSupported && capability.PersistenceSyncSupported ? capability : null;
    }

    static PhysicalEffectIntent? BuildIntent(string kind, InventoryCapability capability, StockObservation observation, ResourceAmount[] resources, bool debit) =>
        BuildIntent(kind, capability, observation, resources, debit, out _);

    internal static PhysicalEffectIntent? BuildIntent(string kind, InventoryCapability capability, StockObservation observation, ResourceAmount[] resources, bool debit, out string hold)
    {
        hold = "Fresh per-member stock cannot safely allocate the complete resource manifest.";
        if (capability is null || observation?.MemberStocks is null || resources is null || resources.Length == 0 ||
            observation.MemberStocks.Length == 0 || observation.MemberStocks.Length > 64 ||
            observation.MemberStocks.Any(x => x == null || x.MemberPersistentId == 0 || x.Resources == null ||
                x.Resources.Any(s => s is null || string.IsNullOrWhiteSpace(s.ResourceName) || s.AmountMicroUnits < 0 || s.CapacityMicroUnits < s.AmountMicroUnits) ||
                x.Resources.Select(s => s.ResourceName).Distinct(StringComparer.Ordinal).Count() != x.Resources.Length) ||
            observation.MemberStocks.Select(x => x.MemberPersistentId).Distinct().Count() != observation.MemberStocks.Length ||
            resources.Any(x => x is null || string.IsNullOrWhiteSpace(x.ResourceName) || x.AmountMicroUnits <= 0) ||
            resources.Select(x => x.ResourceName).Distinct(StringComparer.Ordinal).Count() != resources.Length) return null;
        var memberIds = observation.MemberStocks.Select(x => x.MemberPersistentId).OrderBy(x => x).ToArray();
        if (capability.AnchorPersistentId == 0 || capability.MemberSetHash != OperationIdentity.ComputeMemberSetHash(capability.AnchorPersistentId, memberIds)) return null;
        var deltas = new List<PhysicalEffectResourceDelta>();
        foreach (var resource in resources.OrderBy(x => x.ResourceName, StringComparer.Ordinal))
        {
            long remaining = resource.AmountMicroUnits;
            foreach (var member in observation.MemberStocks.OrderBy(x => x.MemberPersistentId))
            {
                var stock = member.Resources.SingleOrDefault(x => x.ResourceName == resource.ResourceName);
                // A depot may contain specialized tanks. Members without this
                // resource remain enrolled but contribute no eligible capacity.
                if (stock is null) continue;
                // Missing metadata from an older Bridge/observer version is unknown and
                // therefore contributes no debit stock. Credits still use free capacity.
                if (debit && stock.DebitAllowed != true) continue;
                var available = debit ? stock.AmountMicroUnits : stock.CapacityMicroUnits - stock.AmountMicroUnits;
                var amount = Math.Min(remaining, available);
                if (amount > 0) { deltas.Add(new PhysicalEffectResourceDelta { MemberPersistentId = member.MemberPersistentId, ResourceName = resource.ResourceName, DeltaMicroUnits = debit ? -amount : amount }); remaining -= amount; }
                if (remaining == 0) break;
            }
            if (remaining != 0) return null;
        }
        if (deltas.Count > AcceptedStateV2Limits.MaxFaultResourceDeltas)
        {
            hold = "This transfer needs " + deltas.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                " tank/resource changes, above the " + AcceptedStateV2Limits.MaxFaultResourceDeltas.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                "-row physical-effect safety limit. Reduce selected tank spread or manifest size.";
            return null;
        }
        var intent = new PhysicalEffectIntent { EffectKind = kind, Capability = capability, MemberPersistentIds = observation.MemberStocks.Select(x => x.MemberPersistentId).OrderBy(x => x).ToArray(), MembershipRevision = capability.MembershipRevision, Deltas = deltas.ToArray() };
        var structuralHold = AcceptedStateV2Draft.ValidatePhysicalIntent(intent);
        if (structuralHold is not null) { hold = structuralHold; return null; }
        hold = string.Empty;
        return intent;
    }

    bool PreflightPhysicalProposal(EffectProposal proposal, double appliedUt, out string reason)
    {
        reason = "Physical operation is incomplete.";
        var intent = proposal.Delivery?.PhysicalEffect;
        if (accepted is null || intent is null || intent.Capability is null) return false;
        var sorted = intent.Deltas.OrderBy(x => x.MemberPersistentId).ThenBy(x => x.ResourceName, StringComparer.Ordinal).ToArray();
        var rows = new PhysicalSuccessWitnessRow[sorted.Length];
        for (var i = 0; i < sorted.Length; i++)
        {
            var delta = sorted[i].DeltaMicroUnits / 1_000_000d;
            var before = delta < 0 ? -delta : 0;
            var after = before + delta;
            rows[i] = new PhysicalSuccessWitnessRow { MemberPersistentId = sorted[i].MemberPersistentId, ResourceName = sorted[i].ResourceName, BeforeAmount = before, IntendedAfterAmount = after, ObservedAfterAmount = after };
        }
        var witness = new PhysicalSuccessWitness { ProviderId = intent.Capability.ProviderId, ProviderVersion = intent.Capability.ProviderVersion, Rows = rows };
        StateTransitionResult transition;
        if (proposal.OperationKind == "dispatch" && proposal.Delivery?.Shipment is not null)
            transition = AcceptedStateCodec.Dispatch(accepted, proposal.OperationId, proposal.ClientRequestId, proposal.CommandSequence, proposal.PayloadHash, proposal.ExpectedRevision, proposal.ExpectedStateHash, proposal.Delivery.Shipment, intent, appliedUt, proposal.Delivery.ScheduleRuleUpdate, witness);
        else if (proposal.OperationKind == "arrival" && proposal.Delivery is not null)
            transition = AcceptedStateCodec.Arrive(accepted, proposal.OperationId, proposal.ClientRequestId, proposal.CommandSequence, proposal.PayloadHash, proposal.ExpectedRevision, proposal.ExpectedStateHash, proposal.Delivery.ShipmentId, proposal.Delivery.Credits, proposal.Delivery.RemainingCargo, intent, appliedUt, witness);
        else return false;
        if (transition.Outcome == "accepted") { reason = ""; return true; }
        reason = transition.Reason ?? "Physical success witness exceeds accepted-state bounds.";
        return false;
    }

    bool PrepareCompactionForPhysicalWitness()
    {
        if (accepted is null) return false;
        var target = Math.Max(accepted.CompactionWatermark, accepted.AcceptedSequence - 16);
        if (target <= accepted.CompactionWatermark) return false;
        PrepareCompaction();
        return true;
    }

    static bool IsWitnessBoundFailure(string reason) =>
        reason.Contains("36 KiB decoded limit", StringComparison.OrdinalIgnoreCase) ||
        reason.Contains("receipt capacity", StringComparison.OrdinalIgnoreCase) ||
        reason.Contains("collection bounds", StringComparison.OrdinalIgnoreCase);

    Prepared? PrepareDueArrival(double targetUt)
    {
        // This is poll-scoped status: do not keep showing an old transient hold
        // after the endpoint becomes usable or there is no longer due cargo.
        physicalPreflightHoldReason = null;
        transientArrivalHolds.Clear();
        if (active is null || accepted is null || !double.IsFinite(targetUt) || accepted.WritesBlocked) return null;
        foreach (var shipment in accepted.ActiveShipments.Where(x => !x.LegacyOpaque && x.DueUt <= targetUt).OrderBy(x => x.DueUt).ThenBy(x => x.ShipmentId, StringComparer.Ordinal))
        {
            var route = accepted.RouteVersions.SingleOrDefault(x => !x.LegacyOpaque && x.RouteId == shipment.RouteId && x.Version == shipment.RouteVersion);
            if (route is null || !RouteDestinationCurrent(route, accepted))
            {
                const string reason = "Due cargo is held because its registered destination membership changed; repair the destination registration before delivery can resume.";
                physicalPreflightHoldReason ??= reason;
                SetTransientArrivalHold(shipment.ShipmentId, reason);
                continue;
            }
            if (IsRecoveryRoute(route))
            {
                if (!active.Capabilities.Contains("economicRecovery.v1", StringComparer.Ordinal))
                {
                    const string recoveryHold = "Modeled Kerbin recovery awaits a Bridge with authoritative funds settlement capability.";
                    SetTransientArrivalHold(shipment.ShipmentId, recoveryHold);
                    physicalPreflightHoldReason ??= recoveryHold;
                    continue;
                }
                // A load/revert selects its own funds and cargo capsule. Settlement
                // history from a newer run cannot consume payable cargo restored in
                // that save; same-run retries still keep one immutable identity.
                var recoveryIdentity = string.Join("\0", accepted.WorldId, active.RunId, shipment.ShipmentId, shipment.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture));
                var saleRequest = "recovery:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(recoveryIdentity))).ToLowerInvariant();
                // A held uncertain attempt is never replaced by a fresh operation identity.
                if (FindByRequest(saleRequest) is not null) { SetTransientArrivalHold(shipment.ShipmentId, "Prior recovery attempt is held; inspect its outcome before retrying."); continue; }
                var saleSequence = accepted.AcceptedSequence + 1;
                long recoveryFunds;
                try { recoveryFunds = OreExportPolicy.RecoveryFunds(shipment.RemainingResources, shipment.FundsPerUnit); }
                catch (ArgumentException ex) { SetTransientArrivalHold(shipment.ShipmentId, ex.Message); continue; }
                catch (OverflowException) { SetTransientArrivalHold(shipment.ShipmentId, "Immutable Ore recovery compensation exceeds supported bounds."); continue; }
                var saleIntent = new EconomicRecoveryIntent { ShipmentId = shipment.ShipmentId, FundsDelta = recoveryFunds };
                var saleProposal = new EffectProposal { ProtocolVersion = 1, MessageType = "effectProposal", SessionId = active.SessionId, LoadEpoch = active.LoadEpoch, InstallNamespace = active.InstallNamespace, SaveFolder = active.SaveFolder, WorldId = active.WorldId, RunId = active.RunId,
                    OperationId = OperationIdentity.Create(accepted.WorldId, saleSequence, saleRequest), ClientRequestId = saleRequest, CommandSequence = saleSequence, ExpectedRevision = accepted.Revision, ExpectedStateHash = acceptedHash!, PayloadHash = OperationIdentity.RecoveryPayloadHash(saleIntent), OperationKind = "recoverySale", Delivery = new DeliveryEffectPayload { Kind = "recoverySale", RecoveryIntent = saleIntent } };
                // Check domain eligibility and success capsule bounds with a representable placeholder.
                // The actual funds before/after witness is captured only on the game thread.
                var preflight = AcceptedStateCodec.SettleRecovery(accepted, saleProposal.OperationId, saleRequest, saleSequence, saleProposal.PayloadHash, accepted.Revision, acceptedHash!, saleIntent, targetUt,
                    new FundsSuccessWitness { BeforeFunds = 0, IntendedDeltaFunds = recoveryFunds, IntendedAfterFunds = recoveryFunds, ObservedAfterFunds = recoveryFunds });
                if (preflight.Outcome != "accepted")
                {
                    var reason = preflight.Reason ?? "Modeled recovery preflight failed.";
                    SetTransientArrivalHold(shipment.ShipmentId, reason);
                    if (IsWitnessBoundFailure(reason) && PrepareCompactionForPhysicalWitness()) return LoadPrepared(null);
                    continue;
                }
                using var saleTransaction = db.BeginTransaction(); InsertPrepared(saleTransaction, saleProposal); saleTransaction.Commit();
                return new Prepared(saleProposal, saleProposal.PayloadHash);
            }
            var destination = GetFreshObservation(shipment.DestinationDepotId, accepted.WorldId);
            var advertised = active.InventoryEndpoints.SingleOrDefault(x => x.DepotId == shipment.DestinationDepotId && x.MembershipRevision == route.DestinationMembershipRevision && x.MembershipHash == route.DestinationMembershipHash);
            var capability = FindWritableCapability(shipment.DestinationDepotId, route.DestinationMembershipRevision, route.DestinationMembershipHash);
            if (destination is null || capability is null)
            {
                var reason = capability is null
                    ? String.IsNullOrWhiteSpace(advertised?.HoldReason) ? "Due cargo is waiting for a writable destination inventory endpoint." : advertised.HoldReason
                    : "Due cargo is waiting for a fresh destination inventory observation.";
                physicalPreflightHoldReason ??= reason;
                SetTransientArrivalHold(shipment.ShipmentId, reason);
                continue;
            }
            var decision = LogisticsPlanner.PlanArrival(new CargoManifest { ShipmentId = shipment.ShipmentId, RouteId = shipment.RouteId, RouteVersion = shipment.RouteVersion, SourceDepotId = shipment.SourceDepotId, DestinationDepotId = shipment.DestinationDepotId, DepartureUt = shipment.DepartureUt, DueUt = shipment.DueUt, RemainingResources = shipment.RemainingResources }, destination, targetUt);
            if (decision.Outcome is not ("arrived" or "partial") || decision.Credits.Length == 0)
            {
                physicalPreflightHoldReason ??= decision.Reason;
                SetTransientArrivalHold(shipment.ShipmentId, decision.Reason);
                continue;
            }
            var intent = BuildIntent("arrivalCredit", capability, destination, decision.Credits, debit: false, out var allocationHold);
            if (intent is null)
            {
                physicalPreflightHoldReason ??= allocationHold;
                SetTransientArrivalHold(shipment.ShipmentId, allocationHold);
                continue;
            }
            var requestId = "arrival:" + shipment.ShipmentId + ":" + shipment.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var sequence = accepted.AcceptedSequence + 1; var operationId = OperationIdentity.Create(accepted.WorldId, sequence, requestId);
            var payload = new DeliveryEffectPayload { Kind = "arrival", ShipmentId = shipment.ShipmentId, Credits = decision.Credits, RemainingCargo = decision.RemainingCargo, PhysicalEffect = intent };
            string payloadHash;
            try { payloadHash = OperationIdentity.ArrivalPayloadHash(payload.ShipmentId, payload.Credits, payload.RemainingCargo, intent); }
            catch (ArgumentException ex)
            {
                physicalPreflightHoldReason ??= ex.Message;
                SetTransientArrivalHold(shipment.ShipmentId, ex.Message);
                continue;
            }
            var proposal = new EffectProposal { ProtocolVersion = 1, MessageType = "effectProposal", SessionId = active.SessionId, LoadEpoch = active.LoadEpoch, InstallNamespace = active.InstallNamespace, SaveFolder = active.SaveFolder, WorldId = active.WorldId, RunId = active.RunId, OperationId = operationId, ClientRequestId = requestId, CommandSequence = sequence, ExpectedRevision = accepted.Revision, ExpectedStateHash = acceptedHash!, PayloadHash = payloadHash, OperationKind = "arrival", Delivery = payload };
            if (!PreflightPhysicalProposal(proposal, targetUt, out var holdReason))
            {
                physicalPreflightHoldReason = holdReason;
                SetTransientArrivalHold(shipment.ShipmentId, holdReason);
                if (IsWitnessBoundFailure(holdReason) && PrepareCompactionForPhysicalWitness()) return LoadPrepared(null);
                continue;
            }
            using var transaction = db.BeginTransaction(); InsertPrepared(transaction, proposal); transaction.Commit();
            return new Prepared(proposal, payloadHash);
        }
        return null;
    }

    void SetTransientArrivalHold(string shipmentId, string? reason)
    {
        if (transientArrivalHolds.Count >= AcceptedStateV2Limits.MaxActiveShipments || transientArrivalHolds.Any(x => x.ShipmentId == shipmentId)) return;
        transientArrivalHolds.Add(new TransientShipmentHold { ShipmentId = shipmentId, Reason = Bound(reason ?? "Arrival is temporarily held.") });
    }

    Prepared? PrepareDueRuleDispatch(double targetUt)
    {
        if (active is null || accepted is null || accepted.SchemaVersion != 2 || accepted.WritesBlocked || !double.IsFinite(targetUt) || active.RegistrySnapshot is null || accepted.DepotRegistryHash != active.RegistrySnapshot.RegistryHash) return null;
        foreach (var rule in accepted.DeliveryRules.Where(x => !x.LegacyOpaque && x.Enabled).OrderBy(x => x.RuleId, StringComparer.Ordinal))
        {
            var route = accepted.RouteVersions.SingleOrDefault(x => !x.LegacyOpaque && x.RouteId == rule.RouteId && x.Version == rule.RouteVersion);
            if (route is null || !RouteEndpointsCurrent(route, accepted)) continue;
            if (IsRecoveryRoute(route) && route.FundsPerUnit != OreExportPolicy.DefaultFundsPerUnit) continue;
            if (IsRecoveryRoute(route) && !active.Capabilities.Contains("economicRecovery.v1", StringComparer.Ordinal)) continue;
            var source = GetFreshObservation(route.SourceDepotId, accepted.WorldId);
            var sourceCapability = FindWritableCapability(route.SourceDepotId, route.SourceMembershipRevision, route.SourceMembershipHash);
            if (source is null || sourceCapability is null) continue;

            var resources = route.Resources.Select(x => new ResourceAmount { ResourceName = x.ResourceName, AmountMicroUnits = x.AmountMicroUnits }).ToArray();
            DeliveryRuleRecord ruleUpdate;
            double scheduledUt;
            long coalescedSlots;
            if (rule.Kind == "repeat")
            {
                if (targetUt < rule.NextDueUt && !rule.WaitingRequest) continue;
                var routeManifest = ToManifest(route);
                var candidate = LogisticsPlanner.PlanDispatch(routeManifest, source, "schedule-candidate", 0);
                var capabilityAvailable = candidate.Outcome == "dispatch" && BuildIntent("dispatchDebit", sourceCapability, source, candidate.Debits, debit: true) is not null;
                var schedule = new RepeatSchedule { NextDueUt = rule.NextDueUt, WaitingRequest = rule.WaitingRequest, WaitingScheduledUt = rule.WaitingScheduledUt, WaitingCoalescedSlots = rule.WaitingCoalescedSlots };
                var decision = LogisticsPlanner.PlanRepeat(schedule, rule.IntervalSeconds, targetUt, capabilityAvailable, capabilityAvailable ? null : "Route inventory or source write capability is unavailable.");
                if (decision.Outcome != "dispatch") continue;
                scheduledUt = decision.ScheduledUt;
                coalescedSlots = decision.CoalescedSlots;
                ruleUpdate = CloneScheduleRule(rule);
                ruleUpdate.Revision = rule.Revision + 1;
                ruleUpdate.NextDueUt = decision.Next.NextDueUt;
                ruleUpdate.WaitingRequest = false;
                ruleUpdate.WaitingScheduledUt = scheduledUt;
                ruleUpdate.WaitingCoalescedSlots = coalescedSlots;
            }
            else if (rule.Kind == "exportStock")
            {
                if (!IsRecoveryRoute(route) || !active.Capabilities.Contains("economicRecovery.v1", StringComparer.Ordinal)) continue;
                if (rule.ResourceName != "Ore" || route.Resources.Length != 1 || rule.BatchSizeMicroUnits != route.Resources[0].AmountMicroUnits) continue;
                if (LogisticsPlanner.PlanExportStock(rule.TargetMicroUnits, source, rule.BatchSizeMicroUnits).Outcome != "dispatch") continue;
                scheduledUt = targetUt; coalescedSlots = 0;
                ruleUpdate = CloneScheduleRule(rule); ruleUpdate.Revision = rule.Revision + 1;
            }
            else if (rule.Kind == "keepStock")
            {
                if (route.Resources.Length != 1 || route.Resources[0].ResourceName != rule.ResourceName || route.Resources[0].AmountMicroUnits != rule.BatchSizeMicroUnits) continue;
                var destination = GetFreshObservation(route.DestinationDepotId, accepted.WorldId);
                if (destination is null) continue;
                var inbound = accepted.ActiveShipments.Where(x => !x.LegacyOpaque && x.DestinationDepotId == route.DestinationDepotId).Select(x => new CargoManifest { ShipmentId = x.ShipmentId, RouteId = x.RouteId, RouteVersion = x.RouteVersion, SourceDepotId = x.SourceDepotId, DestinationDepotId = x.DestinationDepotId, DepartureUt = x.DepartureUt, DueUt = x.DueUt, RemainingResources = x.RemainingResources.Select(r => new ResourceAmount { ResourceName = r.ResourceName, AmountMicroUnits = r.AmountMicroUnits }).ToArray() }).ToArray();
                var decision = LogisticsPlanner.PlanKeepStock(rule.ResourceName, rule.LowTriggerMicroUnits, rule.TargetMicroUnits, rule.BatchSizeMicroUnits, destination, inbound);
                if (decision.Outcome != "dispatch" || decision.RequestedDispatchMicroUnits <= 0) continue;
                resources = new[] { new ResourceAmount { ResourceName = rule.ResourceName, AmountMicroUnits = decision.RequestedDispatchMicroUnits } };
                var routeManifest = ToManifest(route); routeManifest.Resources = resources;
                if (LogisticsPlanner.PlanDispatch(routeManifest, source, "schedule-candidate", 0).Outcome != "dispatch") continue;
                scheduledUt = targetUt;
                coalescedSlots = 0;
                ruleUpdate = CloneScheduleRule(rule);
                ruleUpdate.Revision = rule.Revision + 1;
            }
            else continue;

            var dispatchManifest = ToManifest(route); dispatchManifest.Resources = resources;
            var scheduleKeyUt = rule.Kind == "repeat" ? scheduledUt : 0d;
            var retryBucket = monotonicTimestampProvider() / ((long)ScheduledRetryBackoffSeconds * Stopwatch.Frequency);
            var requestId = CreateScheduleRequestId(accepted, active.RunId, rule, scheduleKeyUt, retryBucket);
            if (FindByRequest(requestId) is not null) continue;
            var sequence = accepted.AcceptedSequence + 1;
            var operationId = OperationIdentity.Create(accepted.WorldId, sequence, requestId);
            var shipmentId = "shipment:" + operationId;
            var planned = LogisticsPlanner.PlanDispatch(dispatchManifest, source, shipmentId, 0);
            if (planned.Outcome != "dispatch") continue;
            var intent = BuildIntent("dispatchDebit", sourceCapability, source, planned.Debits, debit: true);
            if (intent is null) continue;
            var shipment = new ActiveShipmentRecord { ShipmentId = shipmentId, RouteId = route.RouteId, RouteVersion = route.Version, SourceDepotId = route.SourceDepotId, DestinationDepotId = route.DestinationDepotId, DestinationKind = route.DestinationKind, FundsPerUnit = route.FundsPerUnit, DepartureUt = 0, DueUt = 0, RemainingResources = planned.Shipment!.RemainingResources };
            var payloadHash = OperationIdentity.DispatchPayloadHash(shipment, intent, ruleUpdate);
            var delivery = new DeliveryEffectPayload { Kind = "dispatch", Shipment = shipment, PhysicalEffect = intent, ScheduleRuleUpdate = ruleUpdate };
            var proposal = new EffectProposal { ProtocolVersion = 1, MessageType = "effectProposal", SessionId = active.SessionId, LoadEpoch = active.LoadEpoch, InstallNamespace = active.InstallNamespace, SaveFolder = active.SaveFolder, WorldId = active.WorldId, RunId = active.RunId, OperationId = operationId, ClientRequestId = requestId, CommandSequence = sequence, ExpectedRevision = accepted.Revision, ExpectedStateHash = acceptedHash!, PayloadHash = payloadHash, OperationKind = "dispatch", Delivery = delivery };
            if (!PreflightPhysicalProposal(proposal, targetUt, out var holdReason))
            {
                physicalPreflightHoldReason = holdReason;
                if (IsWitnessBoundFailure(holdReason) && PrepareCompactionForPhysicalWitness()) return LoadPrepared(null);
                continue;
            }
            using var transaction = db.BeginTransaction(); InsertPrepared(transaction, proposal); transaction.Commit();
            return new Prepared(proposal, payloadHash);
        }
        return null;
    }

    static DeliveryRuleRecord CloneScheduleRule(DeliveryRuleRecord rule) => new() { RuleId = rule.RuleId, Revision = rule.Revision, Kind = rule.Kind, RouteId = rule.RouteId, RouteVersion = rule.RouteVersion, Enabled = rule.Enabled, NextDueUt = rule.NextDueUt, IntervalSeconds = rule.IntervalSeconds, WaitingRequest = rule.WaitingRequest, WaitingScheduledUt = rule.WaitingScheduledUt, WaitingCoalescedSlots = rule.WaitingCoalescedSlots, ResourceName = rule.ResourceName, LowTriggerMicroUnits = rule.LowTriggerMicroUnits, TargetMicroUnits = rule.TargetMicroUnits, BatchSizeMicroUnits = rule.BatchSizeMicroUnits, LegacyOpaque = rule.LegacyOpaque };

    static string CreateScheduleRequestId(AcceptedState state, string runId, DeliveryRuleRecord rule, double scheduledUt, long retryBucket)
    {
        var text = string.Join("\0", state.WorldId, runId, rule.RuleId, state.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture), rule.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture), scheduledUt.ToString("R", System.Globalization.CultureInfo.InvariantCulture), retryBucket.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return "schedule:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant()[..40];
    }

    static bool PhysicalResultMatches(PhysicalEffectIntent? intent, PhysicalEffectResult result)
    {
        if (intent is null || result.Status != "applied" || !PhysicalResultMatchesIntent(intent, result)) return false;
        var intended = intent.Deltas.OrderBy(x => x.MemberPersistentId).ThenBy(x => x.ResourceName, StringComparer.Ordinal).ToArray();
        var actual = result.Deltas.OrderBy(x => x.MemberPersistentId).ThenBy(x => x.ResourceName, StringComparer.Ordinal).ToArray();
        for (var i = 0; i < intended.Length; i++)
        {
            var a = actual[i]; var d = intended[i];
            if (!a.ObservedAfterKnown) return false;
            var expectedAfter = a.BeforeAmount + (d.DeltaMicroUnits / 1_000_000d);
            if (a.IntendedAfterAmount != expectedAfter || a.ObservedAfterAmount != expectedAfter) return false;
        }
        return true;
    }

    static PhysicalSuccessWitness? PhysicalWitnessFromResult(PhysicalEffectIntent? intent, PhysicalEffectResult? result)
    {
        if (intent?.Capability is null || result?.Status != "applied" || !PhysicalResultMatches(intent, result)) return null;
        return new PhysicalSuccessWitness
        {
            ProviderId = intent.Capability.ProviderId,
            ProviderVersion = intent.Capability.ProviderVersion,
            Rows = result.Deltas.OrderBy(x => x.MemberPersistentId).ThenBy(x => x.ResourceName, StringComparer.Ordinal).Select(x => new PhysicalSuccessWitnessRow
            {
                MemberPersistentId = x.MemberPersistentId, ResourceName = x.ResourceName, BeforeAmount = x.BeforeAmount,
                IntendedAfterAmount = x.IntendedAfterAmount, ObservedAfterAmount = x.ObservedAfterAmount
            }).ToArray()
        };
    }

    static PhysicalEffectResult? PhysicalResultFromWitness(PhysicalEffectIntent? intent, PhysicalSuccessWitness witness)
    {
        if (intent?.Capability is null || AcceptedStateV2Draft.ValidatePhysicalSuccessWitness(intent, witness) is not null) return null;
        var deltas = intent.Deltas.OrderBy(x => x.MemberPersistentId).ThenBy(x => x.ResourceName, StringComparer.Ordinal).ToArray();
        return new PhysicalEffectResult
        {
            Status = "applied", RollbackStatus = "none",
            Deltas = witness.Rows.Select((row, i) => new PhysicalResourceDelta
            {
                MemberPersistentId = row.MemberPersistentId, ResourceName = row.ResourceName, BeforeAmount = row.BeforeAmount,
                IntendedDeltaMicroUnits = deltas[i].DeltaMicroUnits, IntendedAfterAmount = row.IntendedAfterAmount,
                ObservedAfterKnown = true, ObservedAfterAmount = row.ObservedAfterAmount
            }).ToArray()
        };
    }

    static bool PhysicalResultMatchesIntent(PhysicalEffectIntent? intent, PhysicalEffectResult result)
    {
        if (intent is null || result.Deltas.Length != intent.Deltas.Length) return false;
        var intended = intent.Deltas.OrderBy(x => x.MemberPersistentId).ThenBy(x => x.ResourceName, StringComparer.Ordinal).ToArray();
        var actual = result.Deltas.OrderBy(x => x.MemberPersistentId).ThenBy(x => x.ResourceName, StringComparer.Ordinal).ToArray();
        for (var i = 0; i < intended.Length; i++) if (actual[i].MemberPersistentId != intended[i].MemberPersistentId || actual[i].ResourceName != intended[i].ResourceName || actual[i].IntendedDeltaMicroUnits != intended[i].DeltaMicroUnits) return false;
        return true;
    }
    void InsertPrepared(SqliteTransaction transaction, EffectProposal p)
    {
        using var insert = db.CreateCommand(); insert.Transaction = transaction; insert.CommandText = "INSERT INTO Prepared(operationId,requestId,world,run,sequence,revision,expectedHash,payloadHash,kind,delta,targetWatermark,status,deliveryJson) VALUES($op,$req,$world,$run,$seq,$rev,$hash,$payload,$kind,$delta,$target,'prepared',$delivery)";
        insert.Parameters.AddWithValue("$op", p.OperationId); insert.Parameters.AddWithValue("$req", p.ClientRequestId); insert.Parameters.AddWithValue("$world", p.WorldId); insert.Parameters.AddWithValue("$run", p.RunId); insert.Parameters.AddWithValue("$seq", p.CommandSequence); insert.Parameters.AddWithValue("$rev", p.ExpectedRevision); insert.Parameters.AddWithValue("$hash", p.ExpectedStateHash); insert.Parameters.AddWithValue("$payload", p.PayloadHash); insert.Parameters.AddWithValue("$kind", p.OperationKind); insert.Parameters.AddWithValue("$delta", p.CounterDelta); insert.Parameters.AddWithValue("$target", p.TargetCompactionWatermark); insert.Parameters.AddWithValue("$delivery", (object?) (p.Delivery is null ? null : System.Text.Json.JsonSerializer.Serialize(p.Delivery, ClockProtocol.JsonOptions)) ?? DBNull.Value); insert.ExecuteNonQuery();
    }

    public EffectEnvelope Poll(EffectPoll request, ClockState clock)
    {
        lock (gate)
        {
            if (request.ProtocolVersion != 1 || request.MessageType != "effectPoll" || active is null || accepted is null) return Need("No validated writable attachment.");
            physicalPreflightHoldReason = null;
            if (!clock.TryGetFreshGameContext(request.SessionId, request.LoadEpoch, request.InstallNamespace, request.SaveFolder, request.WorldId, request.RunId, out var clockGeneration) || clockGeneration != active.ClockContextGeneration || request.SessionId != active.SessionId || request.LoadEpoch != active.LoadEpoch || request.InstallNamespace != active.InstallNamespace || request.SaveFolder != active.SaveFolder || request.WorldId != active.WorldId || request.RunId != active.RunId || request.CheckpointId != accepted.CheckpointId || request.Revision != accepted.Revision || request.AcceptedSequence != accepted.AcceptedSequence || request.CompactionWatermark != accepted.CompactionWatermark || request.StateHash != acceptedHash)
                return Need("Bridge accepted prefix or context differs; send a fresh capsule.");
            if (request.InventoryObservations is null || request.InventoryObservations.Length > AcceptedStateCodec.MaxDepots || request.InventoryObservations.Any(x => x is null) || request.InventoryObservations.Select(x => x.DepotId).Distinct(StringComparer.Ordinal).Count() != request.InventoryObservations.Length) return Need("Stock observation list is malformed or exceeds bounds.");
            var observedAt = Environment.TickCount64; var updated = new List<ObservedStock>();
            foreach (var observation in request.InventoryObservations)
            {
                if (observation.SessionId != active.SessionId || observation.LoadEpoch != active.LoadEpoch || observation.WorldId != active.WorldId || observation.RegistryHash != accepted.DepotRegistryHash || observation.AgeSeconds < 0 || observation.AgeSeconds > 1e9 || Double.IsNaN(observation.AgeSeconds) || Double.IsInfinity(observation.AgeSeconds) || observation.ObservationRevision < 0) continue;
                var depot = accepted.Depots.SingleOrDefault(x => x.Active && x.DepotId == observation.DepotId);
                if (depot is null || depot.MembershipRevision != observation.MembershipRevision || depot.MembershipHash != observation.MembershipHash) continue;
                var prior = active.ObservedStocks.SingleOrDefault(x => x.Value.DepotId == observation.DepotId && x.Value.RegistryHash == observation.RegistryHash && x.Value.MembershipRevision == observation.MembershipRevision && x.Value.MembershipHash == observation.MembershipHash && x.Value.ObservationRevision == observation.ObservationRevision && x.Value.SessionId == observation.SessionId && x.Value.LoadEpoch == observation.LoadEpoch && x.Value.WorldId == observation.WorldId);
                updated.Add(prior is null ? new ObservedStock(observation, observedAt, observation.AgeSeconds, observedAt, observation.AgeSeconds) : new ObservedStock(observation, prior.FirstAt, prior.FirstAge, observedAt, Math.Max(prior.LatestAge, observation.AgeSeconds)));
            }
            active = active with { ObservedStocks = updated.ToArray() };
            var pending = LoadPrepared(null);
            var gameOwnsSchedules = active.Capabilities.Contains(LogisticsRuntimePlanner.OwnershipCapability, StringComparer.Ordinal);
            if (gameOwnsSchedules)
            {
                if (request.RuntimeShipmentHolds is null || request.RuntimeShipmentHolds.Length > AcceptedStateV2Limits.MaxActiveShipments || request.RuntimeShipmentHolds.Any(x => x is null || String.IsNullOrWhiteSpace(x.ShipmentId) || x.ShipmentId.Length > 128 || x.Reason is null || x.Reason.Length > 256) || request.RuntimeShipmentHolds.Select(x => x.ShipmentId).Distinct(StringComparer.Ordinal).Count() != request.RuntimeShipmentHolds.Length)
                    return Need("Runtime shipment hold projection is malformed or exceeds bounds.");
                transientArrivalHolds.Clear();
                transientArrivalHolds.AddRange(request.RuntimeShipmentHolds.Where(x => accepted.ActiveShipments.Any(s => s.ShipmentId == x.ShipmentId)));
            }
            if (!gameOwnsSchedules && pending is null && active.RegistrySnapshot is not null && (accepted.SchemaVersion != 2 || accepted.DepotRegistryVersion != active.RegistrySnapshot.RegistryVersion || accepted.DepotRegistryHash != active.RegistrySnapshot.RegistryHash))
            {
                PrepareRegistrySync(); pending = LoadPrepared(null);
            }
            if (!gameOwnsSchedules && pending is null)
            {
                var sample = clock.CurrentSample;
                if (sample is not null && sample.InstallNamespace == active.InstallNamespace && sample.SaveFolder == active.SaveFolder && sample.SessionId == active.SessionId && sample.LoadEpoch == active.LoadEpoch && sample.ActiveWorld && sample.UtSeconds is double currentUt && !accepted.WritesBlocked && accepted.SchemaVersion == 2 && accepted.DepotRegistryHash == active.RegistrySnapshot?.RegistryHash)
                {
                    pending = PrepareDueArrival(currentUt);
                    if (pending is null) pending = PrepareDueRuleDispatch(currentUt);
                }
            }
            if (!clock.TryGetFreshGameContext(request.SessionId, request.LoadEpoch, request.InstallNamespace, request.SaveFolder, request.WorldId, request.RunId, out var finalClockGeneration) || finalClockGeneration != clockGeneration)
                return Need("Clock world/run context changed while preparing poll; retry with the current capsule.");
            if (pending is null) return new EffectIdle { MessageType = "effectIdle", Reason = physicalPreflightHoldReason is null ? "No prepared command or currently due, safely observable arrival." : "Physical operation held before preparation: " + physicalPreflightHoldReason };
            // Poll already proved the exact prior prefix. A changed game prefix
            // must attach/reconcile its historical success witness before this
            // point; only unapplied old-price commands are held here.
            if (HasObsoleteExportTerms(pending.Proposal, accepted))
            {
                const string obsolete = "Unapplied historical export command held: new exports require 100 funds per unit.";
                using var transaction = db.BeginTransaction(); MarkHeld(transaction, pending.Proposal.OperationId, obsolete); transaction.Commit();
                return new EffectIdle { MessageType = "effectIdle", Reason = obsolete };
            }
            pending.Proposal.SessionId = active.SessionId; pending.Proposal.LoadEpoch = active.LoadEpoch;
            return pending.Proposal;
        }
    }

    static bool HasObsoleteExportTerms(EffectProposal proposal, AcceptedState state)
    {
        var payload = proposal.Delivery;
        if (proposal.OperationKind == "routeUpsert" && payload?.RouteVersion is RouteVersionRecord candidate)
            return IsRecoveryRoute(candidate) && candidate.FundsPerUnit != OreExportPolicy.DefaultFundsPerUnit;
        if (proposal.OperationKind == "dispatch" && payload?.Shipment is ActiveShipmentRecord shipment)
            return shipment.DestinationKind == OreExportPolicy.VirtualDestinationKind && shipment.FundsPerUnit != OreExportPolicy.DefaultFundsPerUnit;
        if (proposal.OperationKind == "ruleUpsert" && payload?.Rule is DeliveryRuleRecord rule && rule.Enabled)
        {
            var route = state.RouteVersions.SingleOrDefault(r => r.RouteId == rule.RouteId && r.Version == rule.RouteVersion);
            return route is not null && IsRecoveryRoute(route) && route.FundsPerUnit != OreExportPolicy.DefaultFundsPerUnit;
        }
        return false;
    }

    public EffectEnvelope Receipt(EffectReceipt receipt, ClockState clock)
    {
        lock (gate)
        {
            var pending = LoadPrepared(null);
            if (pending is null || active is null || receipt.ProtocolVersion != 1 || receipt.MessageType != "effectReceipt") return Need("No matching prepared operation.");
            if (receipt.SessionId != active.SessionId || receipt.LoadEpoch != active.LoadEpoch || receipt.WorldId != active.WorldId || receipt.RunId != active.RunId || !clock.TryGetFreshGameContext(receipt.SessionId, receipt.LoadEpoch, receipt.InstallNamespace, receipt.SaveFolder, receipt.WorldId, receipt.RunId, out var clockGeneration) || clockGeneration != active.ClockContextGeneration) return Need("Receipt does not belong to the current live game owner/epoch/world/run.");
            var p = pending.Proposal;
            if (receipt.OperationId != p.OperationId || receipt.ClientRequestId != p.ClientRequestId || receipt.WorldId != p.WorldId || receipt.RunId != p.RunId || receipt.CommandSequence != p.CommandSequence || receipt.PayloadHash != p.PayloadHash || receipt.OperationKind != p.OperationKind || receipt.TargetCompactionWatermark != p.TargetCompactionWatermark || receipt.InstallNamespace != p.InstallNamespace || receipt.SaveFolder != p.SaveFolder)
                return Need("Receipt identity does not match the prepared operation.");
            if (receipt.Outcome is "rejected" or "held")
            {
                using var transaction = db.BeginTransaction(); MarkHeld(transaction, p.OperationId, Bound(receipt.Reason ?? receipt.Outcome)); transaction.Commit();
                return new EffectIdle { MessageType = "effectIdle", Reason = "Command was " + receipt.Outcome + "." };
            }
            if (receipt.Outcome is not ("accepted" or "duplicate" or "alreadySettledCompacted" or "faulted") || receipt.AcceptedCapsule is null) return Need("Only a complete accepted-state capsule can verify a settled receipt.");
            if (p.OperationKind is "dispatch" or "arrival")
            {
                try { AcceptedStateV2Draft.ValidatePhysicalResult(receipt.PhysicalResult!); }
                catch (Exception ex) when (ex is InvalidDataException or NullReferenceException) { return Need("Physical effect result is missing or invalid: " + Bound(ex.Message)); }
                if (receipt.Outcome == "faulted")
                {
                    if (receipt.PhysicalResult!.Status != "uncertain" || !PhysicalResultMatchesIntent(p.Delivery?.PhysicalEffect, receipt.PhysicalResult)) return Need("Fault receipt lacks bounded evidence for the prepared physical intent.");
                }
                else if (receipt.PhysicalResult!.Status != "applied" || !PhysicalResultMatches(p.Delivery?.PhysicalEffect, receipt.PhysicalResult)) return Need("Only a fully verified applied physical result can accept cargo mutation.");
            }
            AcceptedState candidate;
            try { candidate = AcceptedStateCodec.ReadCapsule(receipt.AcceptedCapsule); }
            catch (Exception ex) when (ex is InvalidDataException or FormatException or ArgumentException) { return Need("Receipt capsule validation failed: " + Bound(ex.Message)); }
            var computedHash = AcceptedStateCodec.ComputeHash(candidate);
            if (candidate.WorldId != active.WorldId || computedHash != receipt.StateHash || computedHash != receipt.AcceptedCapsule.StateSha256 || candidate.Revision != receipt.AcceptedRevision || candidate.AcceptedSequence != receipt.AcceptedSequence)
                return Need("Receipt capsule does not match declared accepted prefix/hash.");
            var matching = Array.Find(candidate.Receipts, r => r.OperationId == p.OperationId && r.CommandSequence == p.CommandSequence && r.PayloadHash == p.PayloadHash && r.OperationKind == p.OperationKind);
            if (p.OperationKind == "recoverySale" && receipt.Outcome != "alreadySettledCompacted")
            {
                var result = receipt.EconomicResult;
                if (result is null || result.IntendedDeltaFunds != p.Delivery?.RecoveryIntent?.FundsDelta) return Need("Recovery result is missing or does not match the prepared payout.");
                if (receipt.Outcome == "faulted")
                {
                    var fault = candidate.EconomicFaults.SingleOrDefault(x => x.OperationId == p.OperationId && x.CommandSequence == p.CommandSequence);
                    if (result.Status != "uncertain" || fault is null || !SameEconomicResult(result, fault.Result)) return Need("Recovery fault result does not match the terminal accepted evidence.");
                }
                else
                {
                    var witness = matching?.FundsWitness;
                    if (result.Status != "applied" || !result.ObservedAfterKnown || witness is null || result.BeforeFunds != witness.BeforeFunds || result.IntendedDeltaFunds != witness.IntendedDeltaFunds || result.IntendedAfterFunds != witness.IntendedAfterFunds || result.ObservedAfterFunds != witness.ObservedAfterFunds)
                        return Need("Recovery funds result does not match the accepted exact funds witness.");
                }
            }
            if (receipt.Outcome == "alreadySettledCompacted")
            {
                if (candidate.CompactionWatermark < p.CommandSequence || candidate.AcceptedSequence < p.CommandSequence) return Need("Compacted receipt does not cover the prepared sequence.");
            }
            else
            {
                if (matching is null || matching.Outcome != receipt.Outcome || matching.CounterDelta != p.CounterDelta || matching.TargetCompactionWatermark != p.TargetCompactionWatermark || matching.AppliedUt != receipt.AppliedUt || receipt.ActualCounterDelta != p.CounterDelta || !TryVerifyProposal(p, accepted!, candidate, receipt.AppliedUt, receipt.PhysicalResult, out _))
                    return Need("Receipt full-state verification failed; endpoint is fenced.");
            }
            if (!clock.TryGetFreshGameContext(receipt.SessionId, receipt.LoadEpoch, receipt.InstallNamespace, receipt.SaveFolder, receipt.WorldId, receipt.RunId, out var finalClockGeneration) || finalClockGeneration != clockGeneration)
                return Need("Clock world/run context changed while verifying receipt; do not settle this response.");
            using (var transaction = db.BeginTransaction())
            {
                InsertSettled(transaction, p, receipt.Outcome, computedHash, AcceptedStateCodec.Serialize(candidate), receipt.AppliedUt);
                DeletePrepared(transaction, p.OperationId); StoreActive(transaction, active, candidate, computedHash); transaction.Commit();
            }
            accepted = candidate; acceptedHash = computedHash;
            return new EffectIdle { MessageType = "effectIdle", Reason = "Receipt and complete resulting state verified." };
        }
    }

    public void BackupDatabase(string destinationFile)
    {
        if (string.IsNullOrWhiteSpace(destinationFile)) throw new ArgumentException("A backup destination is required.", nameof(destinationFile));
        var fullPath = Path.GetFullPath(destinationFile);
        if (string.Equals(fullPath, databaseFile, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Backup destination must be distinct from the live database.", nameof(destinationFile));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        lock (gate)
        {
            using var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = fullPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
            destination.Open();
            db.BackupDatabase(destination);
        }
    }

    static bool SameEconomicResult(EconomicEffectResult a, EconomicEffectResult b) => a.Status == b.Status && a.BeforeFunds == b.BeforeFunds && a.IntendedDeltaFunds == b.IntendedDeltaFunds && a.IntendedAfterFunds == b.IntendedAfterFunds && a.ObservedAfterKnown == b.ObservedAfterKnown && a.ObservedAfterFunds == b.ObservedAfterFunds && a.Reason == b.Reason;

    static bool TryVerifyProposal(EffectProposal proposal, AcceptedState prior, AcceptedState actual, double appliedUt, PhysicalEffectResult? physicalResult, out string reason, bool allowLaterSelectedGamePrefix = false)
    {
        reason = "";
        if (AcceptedStateCodec.ComputeHash(prior) != proposal.ExpectedStateHash || prior.Revision != proposal.ExpectedRevision || prior.AcceptedSequence + 1 != proposal.CommandSequence || prior.WorldId != proposal.WorldId)
        { reason = "Prepared prefix does not match prior accepted state."; return false; }
        AcceptedState expected;
        try
        {
            if (proposal.OperationKind == "counterIncrement")
            {
                var transition = AcceptedStateCodec.Increment(prior, proposal.WorldId, proposal.OperationId, proposal.ClientRequestId, proposal.CommandSequence, proposal.PayloadHash, proposal.CounterDelta, appliedUt);
                if (transition.Outcome != "accepted") { reason = transition.Reason ?? "Counter transition rejected."; return false; }
                expected = transition.State;
            }
            else if (proposal.OperationKind == "compact")
            {
                expected = AcceptedStateCodec.Compact(prior, proposal.OperationId, proposal.ClientRequestId, proposal.CommandSequence, proposal.PayloadHash, proposal.ExpectedRevision, proposal.ExpectedStateHash, proposal.TargetCompactionWatermark, appliedUt);
            }
            else if (proposal.OperationKind == "routeUpsert" && proposal.Delivery?.RouteVersion is not null)
            {
                var transition = AcceptedStateCodec.UpsertRoute(prior, proposal.OperationId, proposal.ClientRequestId, proposal.CommandSequence, proposal.PayloadHash, proposal.ExpectedRevision, proposal.ExpectedStateHash, proposal.Delivery.RouteVersion, appliedUt);
                if (transition.Outcome != "accepted") { reason = transition.Reason ?? "Route transition rejected."; return false; }
                expected = transition.State;
            }
            else if (proposal.OperationKind == "ruleUpsert" && proposal.Delivery?.Rule is not null)
            {
                var transition = AcceptedStateCodec.UpsertRule(prior, proposal.OperationId, proposal.ClientRequestId, proposal.CommandSequence, proposal.PayloadHash, proposal.ExpectedRevision, proposal.ExpectedStateHash, proposal.Delivery.Rule, appliedUt);
                if (transition.Outcome != "accepted") { reason = transition.Reason ?? "Rule transition rejected."; return false; }
                expected = transition.State;
            }
            else if (proposal.OperationKind == "ruleCancel" && proposal.Delivery?.Kind == "ruleCancel")
            {
                var transition = AcceptedStateCodec.CancelRule(prior, proposal.OperationId, proposal.ClientRequestId, proposal.CommandSequence, proposal.PayloadHash, proposal.ExpectedRevision, proposal.ExpectedStateHash, proposal.Delivery.RuleId, appliedUt);
                if (transition.Outcome != "accepted") { reason = transition.Reason ?? "Rule cancellation rejected."; return false; }
                expected = transition.State;
            }
            else if (proposal.OperationKind == "syncDepots" && proposal.Delivery?.DepotRegistrySnapshot is not null)
            {
                var transition = AcceptedStateCodec.SyncDepots(prior, proposal.OperationId, proposal.ClientRequestId, proposal.CommandSequence, proposal.PayloadHash, proposal.ExpectedRevision, proposal.ExpectedStateHash, proposal.Delivery.DepotRegistrySnapshot, appliedUt);
                if (transition.Outcome != "accepted") { reason = transition.Reason ?? "Depot registry synchronization rejected."; return false; }
                expected = transition.State;
            }
            else if (proposal.OperationKind == "dispatch" && proposal.Delivery?.Shipment is not null && proposal.Delivery.PhysicalEffect is not null)
            {
                var witness = physicalResult?.Status == "applied" ? PhysicalWitnessFromResult(proposal.Delivery.PhysicalEffect, physicalResult) : null;
                if (physicalResult?.Status == "applied" && witness is null) { reason = "Physical readback cannot produce a valid persisted success witness."; return false; }
                var transition = physicalResult?.Status == "uncertain"
                    ? AcceptedStateCodec.FaultPhysicalEffect(prior, proposal.OperationId, proposal.ClientRequestId, proposal.CommandSequence, proposal.PayloadHash, proposal.ExpectedRevision, proposal.ExpectedStateHash, "dispatch", proposal.Delivery.PhysicalEffect.Capability.DepotId, proposal.Delivery.PhysicalEffect.Capability.MembershipRevision, proposal.Delivery.PhysicalEffect.Capability.MembershipHash, proposal.Delivery.PhysicalEffect, physicalResult, appliedUt)
                    : AcceptedStateCodec.Dispatch(prior, proposal.OperationId, proposal.ClientRequestId, proposal.CommandSequence, proposal.PayloadHash, proposal.ExpectedRevision, proposal.ExpectedStateHash, proposal.Delivery.Shipment, proposal.Delivery.PhysicalEffect, appliedUt, proposal.Delivery.ScheduleRuleUpdate, witness);
                if (transition.Outcome != (physicalResult?.Status == "uncertain" ? "faulted" : "accepted")) { reason = transition.Reason ?? "Dispatch transition rejected."; return false; }
                expected = transition.State;
            }
            else if (proposal.OperationKind == "recoverySale" && proposal.Delivery?.RecoveryIntent is not null)
            {
                var receipt = actual.Receipts.SingleOrDefault(r => r.OperationId == proposal.OperationId && r.CommandSequence == proposal.CommandSequence && r.PayloadHash == proposal.PayloadHash);
                if (receipt is null) { reason = "Recovery receipt is missing."; return false; }
                StateTransitionResult transition;
                if (receipt.Outcome == "faulted")
                {
                    var fault = actual.EconomicFaults.SingleOrDefault(x => x.OperationId == proposal.OperationId && x.CommandSequence == proposal.CommandSequence && x.ShipmentId == proposal.Delivery.RecoveryIntent.ShipmentId);
                    if (fault is null) { reason = "Economic fault evidence is missing."; return false; }
                    transition = AcceptedStateCodec.FaultRecovery(prior, proposal.OperationId, proposal.ClientRequestId, proposal.CommandSequence, proposal.PayloadHash, proposal.ExpectedRevision, proposal.ExpectedStateHash, proposal.Delivery.RecoveryIntent, fault.Result, appliedUt);
                }
                else
                {
                    if (receipt.FundsWitness is null) { reason = "Authoritative funds success witness is missing."; return false; }
                    transition = AcceptedStateCodec.SettleRecovery(prior, proposal.OperationId, proposal.ClientRequestId, proposal.CommandSequence, proposal.PayloadHash, proposal.ExpectedRevision, proposal.ExpectedStateHash, proposal.Delivery.RecoveryIntent, appliedUt, receipt.FundsWitness);
                }
                if (transition.Outcome != receipt.Outcome) { reason = transition.Reason ?? "Recovery settlement rejected."; return false; }
                expected = transition.State;
            }
            else if (proposal.OperationKind == "arrival" && proposal.Delivery is not null && proposal.Delivery.PhysicalEffect is not null)
            {
                var witness = physicalResult?.Status == "applied" ? PhysicalWitnessFromResult(proposal.Delivery.PhysicalEffect, physicalResult) : null;
                if (physicalResult?.Status == "applied" && witness is null) { reason = "Physical readback cannot produce a valid persisted success witness."; return false; }
                var transition = physicalResult?.Status == "uncertain"
                    ? AcceptedStateCodec.FaultPhysicalEffect(prior, proposal.OperationId, proposal.ClientRequestId, proposal.CommandSequence, proposal.PayloadHash, proposal.ExpectedRevision, proposal.ExpectedStateHash, "arrival", proposal.Delivery.PhysicalEffect.Capability.DepotId, proposal.Delivery.PhysicalEffect.Capability.MembershipRevision, proposal.Delivery.PhysicalEffect.Capability.MembershipHash, proposal.Delivery.PhysicalEffect, physicalResult, appliedUt)
                    : AcceptedStateCodec.Arrive(prior, proposal.OperationId, proposal.ClientRequestId, proposal.CommandSequence, proposal.PayloadHash, proposal.ExpectedRevision, proposal.ExpectedStateHash, proposal.Delivery.ShipmentId, proposal.Delivery.Credits, proposal.Delivery.RemainingCargo, proposal.Delivery.PhysicalEffect, appliedUt, witness);
                if (transition.Outcome != (physicalResult?.Status == "uncertain" ? "faulted" : "accepted")) { reason = transition.Reason ?? "Arrival transition rejected."; return false; }
                expected = transition.State;
            }
            else { reason = "Unknown operation kind."; return false; }
        }
        catch (InvalidDataException ex) { reason = ex.Message; return false; }
        if (AcceptedStateCodec.ComputeHash(expected) != AcceptedStateCodec.ComputeHash(actual))
        {
            // Only selected-save reattachment from a game-owned scheduler may
            // contain later legitimate effects after a lost Host acknowledgement.
            // Verify this prepared effect's complete terminal receipt independently;
            // the incoming validated capsule remains the state authority. Ordinary
            // external receipt acceptance still requires full deterministic equality.
            var computedReceipt = expected.Receipts.SingleOrDefault(x => x.OperationId == proposal.OperationId && x.CommandSequence == proposal.CommandSequence);
            var selectedReceipt = actual.Receipts.SingleOrDefault(x => x.OperationId == proposal.OperationId && x.CommandSequence == proposal.CommandSequence);
            if (!allowLaterSelectedGamePrefix || actual.WorldId != expected.WorldId || actual.AcceptedSequence <= expected.AcceptedSequence || actual.Revision <= expected.Revision ||
                computedReceipt is null || selectedReceipt is null ||
                System.Text.Json.JsonSerializer.Serialize(computedReceipt, ClockProtocol.JsonOptions) != System.Text.Json.JsonSerializer.Serialize(selectedReceipt, ClockProtocol.JsonOptions))
            { reason = "Full accepted-state projection differs from deterministic result."; return false; }
        }
        return true;
    }

    static SubmitCommandResult Reject(string requestId, string reason) => new() { ProtocolVersion = 1, MessageType = "submitCommandResult", ClientRequestId = requestId ?? "", Status = "rejected", Reason = Bound(reason) };
    static EffectNeedAttach Need(string reason) => new() { ProtocolVersion = 1, MessageType = "effectNeedAttach", Reason = Bound(reason) };
    static string Bound(string x) => x.Length <= 256 ? x : x.Substring(0, 256);

    Prepared? FindByRequest(string id)
    {
        using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT operationId,requestId,world,run,sequence,revision,expectedHash,payloadHash,kind,delta,targetWatermark,status,deliveryJson FROM Prepared WHERE requestId=$r UNION ALL SELECT operationId,requestId,world,run,sequence,0,'',payloadHash,kind,0,0,'settled',deliveryJson FROM Settled WHERE requestId=$r LIMIT 1"; cmd.Parameters.AddWithValue("$r", id);
        using var reader = cmd.ExecuteReader(); return reader.Read() ? ReadPrepared(reader) : null;
    }
    Prepared? LoadPrepared(SqliteTransaction? tx)
    {
        using var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "SELECT p.operationId,p.requestId,p.world,p.run,p.sequence,p.revision,p.expectedHash,p.payloadHash,p.kind,p.delta,p.targetWatermark,p.status,p.deliveryJson FROM Prepared p WHERE p.status='prepared' ORDER BY sequence LIMIT 1";
        using var reader = cmd.ExecuteReader(); return reader.Read() ? ReadPrepared(reader) : null;
    }
    Prepared ReadPrepared(SqliteDataReader r)
    {
        var op = r.GetString(0); var requestId = r.GetString(1); var world = r.GetString(2); var run = r.GetString(3); var seq = r.GetInt64(4); var rev = r.GetInt64(5); var hash = r.GetString(6); var payload = r.GetString(7); var kind = r.GetString(8); var delta = r.GetInt64(9); var target = r.GetInt64(10); var status = r.GetString(11);
        var ctx = active;
        DeliveryEffectPayload? delivery = null;
        if (!r.IsDBNull(12)) delivery = System.Text.Json.JsonSerializer.Deserialize<DeliveryEffectPayload>(r.GetString(12), ClockProtocol.JsonOptions);
        var proposal = new EffectProposal { ProtocolVersion = 1, MessageType = status == "settled" ? "settled" : status == "held" ? "held" : "effectProposal", SessionId = ctx?.SessionId ?? Guid.Empty, LoadEpoch = ctx?.LoadEpoch ?? Guid.Empty, InstallNamespace = ctx?.InstallNamespace ?? "", SaveFolder = ctx?.SaveFolder ?? "", WorldId = world, RunId = run, OperationId = op, ClientRequestId = requestId, CommandSequence = seq, ExpectedRevision = rev, ExpectedStateHash = hash, PayloadHash = payload, OperationKind = kind, CounterDelta = delta, TargetCompactionWatermark = target, Delivery = delivery };
        return new Prepared(proposal, payload);
    }
    void StoreActive(SqliteTransaction tx, ActiveContext ctx, AcceptedState state, string hash)
    {
        using var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "INSERT INTO ActiveState(id,install,save,world,run,state,hash,ownerSession,ownerEpoch,ownerPid,ownerStartTicks,ownerPath) VALUES(1,$i,$s,$w,$r,$b,$h,$os,$oe,$pid,$ticks,$path) ON CONFLICT(id) DO UPDATE SET install=$i,save=$s,world=$w,run=$r,state=$b,hash=$h,ownerSession=$os,ownerEpoch=$oe,ownerPid=$pid,ownerStartTicks=$ticks,ownerPath=$path";
        cmd.Parameters.AddWithValue("$i", ctx.InstallNamespace); cmd.Parameters.AddWithValue("$s", ctx.SaveFolder); cmd.Parameters.AddWithValue("$w", ctx.WorldId); cmd.Parameters.AddWithValue("$r", ctx.RunId); cmd.Parameters.Add("$b", SqliteType.Blob).Value = AcceptedStateCodec.Serialize(state); cmd.Parameters.AddWithValue("$h", hash); cmd.Parameters.AddWithValue("$os", ctx.SessionId.ToString("D")); cmd.Parameters.AddWithValue("$oe", ctx.LoadEpoch.ToString("D")); cmd.Parameters.AddWithValue("$pid", ctx.ProcessId); cmd.Parameters.AddWithValue("$ticks", ctx.ProcessStartUtcTicks); cmd.Parameters.AddWithValue("$path", ctx.ExecutablePath); cmd.ExecuteNonQuery();
    }
    void SetMeta(SqliteTransaction tx, string key, string value)
    { using var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "INSERT INTO HostMeta(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v"; cmd.Parameters.AddWithValue("$k", key); cmd.Parameters.AddWithValue("$v", value); cmd.ExecuteNonQuery(); }
    ActiveContext? LoadStoredContext(SqliteTransaction tx)
    {
        using var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "SELECT install,save,world,run,ownerSession,ownerEpoch,ownerPid,ownerStartTicks,ownerPath FROM ActiveState WHERE id=1";
        using var reader = cmd.ExecuteReader(); return reader.Read() ? new ActiveContext(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), Guid.Parse(reader.GetString(4)), Guid.Parse(reader.GetString(5)), reader.GetInt32(6), reader.GetInt64(7), reader.GetString(8), 0, Array.Empty<string>(), Array.Empty<InventoryCapability>(), null, Array.Empty<ObservedStock>()) : null;
    }
    AcceptedState? LoadStoredState(SqliteTransaction tx)
    {
        using var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "SELECT state FROM ActiveState WHERE id=1";
        using var reader = cmd.ExecuteReader(); if (!reader.Read()) return null; return AcceptedStateCodec.Deserialize((byte[])reader[0]);
    }
    void InsertSettled(SqliteTransaction tx, EffectProposal p, string outcome, string hash, byte[]? state, double? ut)
    {
        using var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "INSERT INTO Settled(operationId,requestId,world,run,sequence,payloadHash,kind,deliveryJson,outcome,stateHash,acceptedState,appliedUt) VALUES($o,$r,$w,$run,$s,$p,$kind,$delivery,$x,$h,$b,$u) ON CONFLICT(operationId) DO UPDATE SET run=$run,kind=$kind,deliveryJson=$delivery,outcome=$x,stateHash=$h,acceptedState=$b,appliedUt=$u";
        cmd.Parameters.AddWithValue("$o", p.OperationId); cmd.Parameters.AddWithValue("$r", p.ClientRequestId); cmd.Parameters.AddWithValue("$w", p.WorldId); cmd.Parameters.AddWithValue("$run", p.RunId); cmd.Parameters.AddWithValue("$s", p.CommandSequence); cmd.Parameters.AddWithValue("$p", p.PayloadHash); cmd.Parameters.AddWithValue("$kind", p.OperationKind); cmd.Parameters.AddWithValue("$delivery", (object?)(p.Delivery is null ? null : System.Text.Json.JsonSerializer.Serialize(p.Delivery, ClockProtocol.JsonOptions)) ?? DBNull.Value); cmd.Parameters.AddWithValue("$x", outcome); cmd.Parameters.AddWithValue("$h", hash); cmd.Parameters.Add("$b", SqliteType.Blob).Value = state is null ? DBNull.Value : state; cmd.Parameters.AddWithValue("$u", (object?)ut ?? DBNull.Value); cmd.ExecuteNonQuery();
    }
    void DeletePrepared(SqliteTransaction tx, string op) { using var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "DELETE FROM Prepared WHERE operationId=$o"; cmd.Parameters.AddWithValue("$o", op); cmd.ExecuteNonQuery(); }
    void MarkHeld(SqliteTransaction tx, string op, string reason) { using var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "UPDATE Prepared SET status='held',reason=$r WHERE operationId=$o"; cmd.Parameters.AddWithValue("$r", reason); cmd.Parameters.AddWithValue("$o", op); cmd.ExecuteNonQuery(); }
    public void Dispose() => db.Dispose();
}
