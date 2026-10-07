using Expanse.Clock.Core;
using Expanse.Clock.Host;
using Expanse.Domain;

namespace Expanse.Clock.Tests;

public sealed partial class RecoveryTests
{
    [Fact]
    public void LegacyPreparedTableMigratesWithoutLosingPendingRows()
    {
        var directory = NewDirectory();
        var path = Path.Combine(directory, "recovery.sqlite3");
        using (var legacy = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
        {
            legacy.Open();
            using var create = legacy.CreateCommand();
            create.CommandText = "CREATE TABLE Prepared(operationId TEXT PRIMARY KEY, requestId TEXT NOT NULL UNIQUE, world TEXT NOT NULL, run TEXT NOT NULL, sequence INTEGER NOT NULL, revision INTEGER NOT NULL, expectedHash TEXT NOT NULL, payloadHash TEXT NOT NULL, delta INTEGER NOT NULL, status TEXT NOT NULL, reason TEXT NULL); INSERT INTO Prepared VALUES('op','req','world','run',1,0,'hash','payload',3,'prepared',NULL); PRAGMA user_version=1;";
            create.ExecuteNonQuery();
        }
        using (var upgraded = new RecoveryCoordinator(directory)) { }
        using (var reopened = new RecoveryCoordinator(directory)) { }
        using var check = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
        check.Open();
        using var query = check.CreateCommand();
        query.CommandText = "SELECT kind,targetWatermark,deliveryJson,delta,status FROM Prepared WHERE operationId='op'";
        using var row = query.ExecuteReader();
        Assert.True(row.Read());
        Assert.Equal("counterIncrement", row.GetString(0));
        Assert.Equal(0L, row.GetInt64(1));
        Assert.True(row.IsDBNull(2));
        Assert.Equal(3L, row.GetInt64(3));
        Assert.Equal("prepared", row.GetString(4));
    }

    static readonly Guid Session = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    static readonly Guid Epoch = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    const string Install = @"C:\KSP";
    const string Save = "Sandbox";
    const int Pid = 4312;
    const long Start = 638946720000000000;
    const string Exe = @"C:\KSP\KSP_x64.exe";

    sealed class AlwaysLiveVerifier : IBridgeProcessVerifier
    {
        public ProcessIdentityState Verify(int processId, long startUtcTicks, string executablePath, string installNamespace, out string reason)
        { reason = "synthetic process is live"; return ProcessIdentityState.SameLiveProcess; }
    }

    static string NewDirectory() { var path = Path.Combine(Path.GetTempPath(), "ExpanseRecoveryTests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    static AcceptedState InitialState(string? world = null) => new() { WorldId = world ?? Guid.NewGuid().ToString("D"), CheckpointId = Guid.NewGuid().ToString("N") };
    static ClockState FreshClock(string? worldId = null, string runId = "run-one", Guid? session = null, Guid? epoch = null, string save = Save)
    {
        var state = new ClockState(); state.SetPublisherConnected(true);
        state.Accept(new ClockSample(1, "clockSample", 1, session ?? Session, epoch ?? Epoch, Install, save, "Synthetic", 100, true, "Flight", false, "Year 1", 1, WorldId: worldId, RunId: runId));
        return state;
    }
    static EffectAttach AttachFor(AcceptedState state, Guid? session = null, Guid? epoch = null, Guid? world = null, string? run = null, int pid = Pid) => new()
    {
        ProtocolVersion = 1, MessageType = "effectAttach", SessionId = session ?? Session, LoadEpoch = epoch ?? Epoch,
        BridgeProcessId = pid, BridgeProcessStartUtcTicks = Start, BridgeExecutablePath = Exe,
        InstallNamespace = Install, SaveFolder = Save, WorldId = world?.ToString("D") ?? state.WorldId, RunId = run ?? "run-one",
        CheckpointId = state.CheckpointId, Revision = state.Revision, AcceptedSequence = state.AcceptedSequence,
        CompactionWatermark = state.CompactionWatermark, StateHash = AcceptedStateCodec.ComputeHash(state), CanWrite = true, Capsule = AcceptedStateCodec.CreateCapsule(state)
    };
    static SubmitCommand Command(string id, string world, long delta = 1) => new() { ProtocolVersion = 1, MessageType = "submitCommand", ClientRequestId = id, WorldId = world, RunId = "run-one", CommandKind = "counterIncrement", CounterDelta = delta };

    [Fact]
    public void CanonicalCapsuleRoundTripsAndRejectsTampering()
    {
        var state = InitialState(); state.Depots = [new DepotRecord { DepotId = "z", MembershipRevision = 2 }, new DepotRecord { DepotId = "a", MembershipRevision = 1 }];
        var bytes = AcceptedStateCodec.Serialize(state); var decoded = AcceptedStateCodec.Deserialize(bytes);
        Assert.Equal(AcceptedStateCodec.ComputeHash(state), AcceptedStateCodec.ComputeHash(decoded));
        Assert.Equal("a", System.Text.Encoding.UTF8.GetString(bytes).Length > 0 ? decoded.Depots.OrderBy(d => d.DepotId).First().DepotId : "");
        var capsule = AcceptedStateCodec.CreateCapsule(state); Assert.Equal(state.WorldId, AcceptedStateCodec.ReadCapsule(capsule).WorldId);
        capsule.StateSha256 = new string('0', 64); Assert.Throws<InvalidDataException>(() => AcceptedStateCodec.ReadCapsule(capsule));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void ConfigNodeSafeCapsuleReadsLegacySlashSlashWithoutChangingCanonicalHash(int wireVersion)
    {
        var state = new AcceptedState { SchemaVersion = 2, CapsuleEncodingVersion = wireVersion,
            WorldId = "3bba0a04-1978-47e4-b12f-11618c19b2a9", CheckpointId = "copy-migration",
            Counter = 0xFFFFFF };
        byte[] bytes = AcceptedStateCodec.Serialize(state);
        string standard = Convert.ToBase64String(bytes);
        Assert.Contains("//", standard);
        string canonicalHash = AcceptedStateCodec.ComputeHash(state);
        var legacy = new RecoveryCapsule { WorldId = state.WorldId, StateBytesBase64 = standard, StateSha256 = canonicalHash };
        Assert.Equal(canonicalHash, AcceptedStateCodec.ComputeHash(AcceptedStateCodec.ReadCapsule(legacy)));

        RecoveryCapsule safe = AcceptedStateCodec.CreateCapsule(state);
        Assert.DoesNotContain("/", safe.StateBytesBase64);
        Assert.DoesNotContain("+", safe.StateBytesBase64);
        Assert.Equal(canonicalHash, safe.StateSha256);
        Assert.Equal(bytes, AcceptedStateCodec.DecodeCapsuleBytes(safe.StateBytesBase64));
        Assert.Equal(canonicalHash, AcceptedStateCodec.ComputeHash(AcceptedStateCodec.ReadCapsule(safe)));
    }

    [Fact]
    public void ReducerBindsOperationIdToSequenceAndHoldsUntilVerifiedCompaction()
    {
        var state = InitialState(); const long delta = 2; var payloadHash = OperationIdentity.CounterIncrementPayloadHash(delta);
        var firstId = OperationIdentity.Create(state.WorldId, 1, "request-1");
        var first = AcceptedStateCodec.Increment(state, state.WorldId, firstId, "request-1", 1, payloadHash, delta, 12.5);
        Assert.Equal("accepted", first.Outcome); Assert.Equal(12.5, first.State.Receipts[0].AppliedUt);
        Assert.Equal(AcceptedStateCodec.ComputeHash(first.State), AcceptedStateCodec.ComputeHash(AcceptedStateCodec.Deserialize(AcceptedStateCodec.Serialize(first.State))));
        Assert.Equal("duplicate", AcceptedStateCodec.Increment(first.State, state.WorldId, firstId, "request-1", 1, payloadHash, delta, 12.5).Outcome);
        Assert.Equal("rejected", AcceptedStateCodec.Increment(first.State, state.WorldId, firstId, "request-1", 2, payloadHash, delta, 12.5).Outcome);

        var full = state;
        for (var sequence = 1; sequence <= AcceptedStateCodec.MaxReceipts; sequence++)
        {
            var requestId = "request-" + sequence; var operationId = OperationIdentity.Create(full.WorldId, sequence, requestId);
            var result = AcceptedStateCodec.Increment(full, full.WorldId, operationId, requestId, sequence, OperationIdentity.CounterIncrementPayloadHash(1), 1, sequence);
            Assert.Equal("accepted", result.Outcome); full = result.State;
        }
        var request33 = "request-33"; var op33 = OperationIdentity.Create(full.WorldId, 33, request33);
        Assert.Equal("held", AcceptedStateCodec.Increment(full, full.WorldId, op33, request33, 33, OperationIdentity.CounterIncrementPayloadHash(1), 1, 33).Outcome);
        var preCompact = full; var beforeHash = AcceptedStateCodec.ComputeHash(full); var compactRequest = "compact:32:16"; var compactId = OperationIdentity.Create(full.WorldId, 33, compactRequest);
        full = AcceptedStateCodec.Compact(full, compactId, compactRequest, 33, OperationIdentity.CompactionPayloadHash(16), full.Revision, beforeHash, 16, 33);
        Assert.Equal(17, full.Receipts.Length); Assert.Equal(16, full.CompactionWatermark); Assert.Equal(33, full.AcceptedSequence);
        var replayedCompaction = AcceptedStateCodec.Compact(AcceptedStateCodec.Deserialize(AcceptedStateCodec.Serialize(preCompact)), compactId, compactRequest, 33, OperationIdentity.CompactionPayloadHash(16), preCompact.Revision, beforeHash, 16, 33);
        Assert.Equal(full.CheckpointId, replayedCompaction.CheckpointId); Assert.Equal(preCompact.CheckpointId, full.ParentCheckpointId);
        Assert.Equal("alreadySettledCompacted", AcceptedStateCodec.Increment(full, full.WorldId, firstId, "request-1", 1, payloadHash, delta, 12.5).Outcome);
        Assert.Throws<InvalidDataException>(() => AcceptedStateCodec.Compact(full, compactId, compactRequest, 34, OperationIdentity.CompactionPayloadHash(16), full.Revision - 1, beforeHash, 16, 34));
    }

    [Fact]
    public void SqlitePreparedCommandSurvivesHostRestartAndThousandRetriesSettleOnce()
    {
        var directory = NewDirectory(); var state = InitialState(); var clock = FreshClock(state.WorldId); var verifier = new AlwaysLiveVerifier();
        try
        {
            string operationId;
            using (var host = new RecoveryCoordinator(directory, verifier, enableDevCounter: true))
            {
                Assert.IsType<EffectIdle>(host.Attach(AttachFor(state), clock));
                var request = Command("retry-stable", state.WorldId); var first = host.Submit(request); Assert.Equal("pending", first.Status); operationId = first.OperationId!;
                for (var i = 0; i < 999; i++) { var duplicate = host.Submit(request); Assert.Equal("pending", duplicate.Status); Assert.Equal(operationId, duplicate.OperationId); }
                var proposal = Assert.IsType<EffectProposal>(host.Poll(PollFor(state), clock));
                var accepted = AcceptedStateCodec.Increment(state, state.WorldId, proposal.OperationId, proposal.ClientRequestId, proposal.CommandSequence, proposal.PayloadHash, proposal.CounterDelta, 123.25);
                var receipt = ReceiptFor(proposal, accepted.State, 123.25);
                Assert.IsType<EffectIdle>(host.Receipt(receipt, clock));
                Assert.Equal("accepted", host.Submit(request).Status);
            }
            var acceptedState = AcceptedStateCodec.Increment(state, state.WorldId, operationId, "retry-stable", 1, OperationIdentity.CounterIncrementPayloadHash(1), 1, 123.25).State;
            var backup = Path.Combine(directory, "explicit-backup.sqlite3");
            using (var host = new RecoveryCoordinator(directory, verifier, enableDevCounter: true))
            {
                Assert.IsType<EffectIdle>(host.Attach(AttachFor(acceptedState), clock));
                host.BackupDatabase(backup);
                var retry = host.Submit(Command("retry-stable", state.WorldId)); Assert.Equal("accepted", retry.Status); Assert.Equal(operationId, retry.OperationId);
            }
            Assert.True(new FileInfo(backup).Length > 0);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void TypedRouteCommandPersistsPreparedPayloadAndVerifiesFullV2Capsule()
    {
        var directory = NewDirectory(); var verifier = new AlwaysLiveVerifier();
        var state = InitialState(); var clock = FreshClock(state.WorldId); state.Depots = [new DepotRecord { DepotId = "source", MembershipRevision = 1, MembershipHash = new string('a', 64) }, new DepotRecord { DepotId = "destination", MembershipRevision = 1, MembershipHash = new string('b', 64) }];
        var registry = new DepotRegistrySnapshot { RegistryVersion = "registry-1", Depots = state.Depots.Select(d => new DepotRecord { DepotId = d.DepotId, MembershipRevision = d.MembershipRevision, MembershipHash = d.MembershipHash }).ToArray() };
        registry.RegistryHash = OperationIdentity.ComputeDepotRegistryHash(registry.Depots);
        var route = new RouteVersionRecord { RouteId = "route-a", Version = 1, SourceDepotId = "source", SourceMembershipRevision = 1, SourceMembershipHash = new string('a', 64), DestinationDepotId = "destination", DestinationMembershipRevision = 1, DestinationMembershipHash = new string('b', 64), TravelDurationSeconds = 60, Provenance = "test", Resources = [new ResourceAmount { ResourceName = "Fuel", AmountMicroUnits = 2_000_000 }] };
        var request = new SubmitCommand { ProtocolVersion = 1, MessageType = "submitCommand", ClientRequestId = "route-request", WorldId = state.WorldId, RunId = "run-one", CommandKind = "routeUpsert", Delivery = new DeliveryCommandPayload { Kind = "routeUpsert", RouteVersion = route } };
        AcceptedState synced;
        try
        {
            using (var first = new RecoveryCoordinator(directory, verifier))
            {
                var attach = AttachFor(state); attach.RegistrySnapshot = registry; attach.InventoryEndpoints = [
                    new InventoryCapability { DepotId = "source", MembershipRevision = 1 },
                    new InventoryCapability { DepotId = "destination", MembershipRevision = 1 }];
                Assert.IsType<EffectIdle>(first.Attach(attach, clock));
                var sync = Assert.IsType<EffectProposal>(first.Poll(PollFor(state), clock)); Assert.Equal("syncDepots", sync.OperationKind);
                var mirror = AcceptedStateCodec.SyncDepots(state, sync.OperationId, sync.ClientRequestId, sync.CommandSequence, sync.PayloadHash, sync.ExpectedRevision, sync.ExpectedStateHash, registry, 42);
                Assert.Equal("accepted", mirror.Outcome); synced = mirror.State;
                var syncReceipt = ReceiptFor(sync, synced, 42); syncReceipt.ActualCounterDelta = 0;
                Assert.IsType<EffectIdle>(first.Receipt(syncReceipt, clock));
                Assert.Equal("pending", first.Submit(request).Status);
            }
            using var restarted = new RecoveryCoordinator(directory, verifier);
            var resumedAttach = AttachFor(synced); resumedAttach.RegistrySnapshot = registry; resumedAttach.InventoryEndpoints = [
                new InventoryCapability { DepotId = "source", MembershipRevision = 1 },
                new InventoryCapability { DepotId = "destination", MembershipRevision = 1 }];
            Assert.IsType<EffectIdle>(restarted.Attach(resumedAttach, clock));
            var proposal = Assert.IsType<EffectProposal>(restarted.Poll(PollFor(synced), clock));
            Assert.NotNull(proposal.Delivery?.RouteVersion);
            Assert.Equal(OperationIdentity.RouteVersionPayloadHash(route), proposal.PayloadHash);
            var transition = AcceptedStateCodec.UpsertRoute(synced, proposal.OperationId, proposal.ClientRequestId, proposal.CommandSequence, proposal.PayloadHash, proposal.ExpectedRevision, proposal.ExpectedStateHash, proposal.Delivery!.RouteVersion!, 45);
            Assert.Equal("accepted", transition.Outcome);
            var receipt = ReceiptFor(proposal, transition.State, 45); receipt.ActualCounterDelta = 0;
            Assert.IsType<EffectIdle>(restarted.Receipt(receipt, clock));
            Assert.Equal(2, transition.State.SchemaVersion);
            var status = restarted.GetCommandStatus(new GetCommandStatus { ProtocolVersion = 1, MessageType = "getCommandStatus", ClientRequestId = request.ClientRequestId, WorldId = synced.WorldId, RunId = "run-one" });
            Assert.Equal("accepted", status.Status); Assert.Equal(proposal.OperationId, status.OperationId); Assert.Equal(AcceptedStateCodec.ComputeHash(transition.State), status.StateHash);
            Assert.NotNull(AcceptedStateCodec.ReadCapsule(status.AcceptedCapsule!));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void SendOncePreparesDurableDispatchAndVerifiesPhysicalReceiptWithBridgeApplyUt()
    {
        var directory = NewDirectory(); var verifier = new AlwaysLiveVerifier(); var state = InitialState(); var clock = FreshClock(state.WorldId);
        try
        {
            var registry = new DepotRegistrySnapshot { RegistryVersion = "registry", Depots = [new DepotRecord { DepotId = "source", MembershipRevision = 1, MembershipHash = new string('a', 64) }, new DepotRecord { DepotId = "destination", MembershipRevision = 1, MembershipHash = new string('b', 64) }] };
            registry.RegistryHash = OperationIdentity.ComputeDepotRegistryHash(registry.Depots);
            var sourceCap = WritableCapability("source", 1, new string('a', 64), 7, [7]); var destinationCap = WritableCapability("destination", 1, new string('b', 64), 9, [9]);
            using var host = new RecoveryCoordinator(directory, verifier);
            var attach = AttachFor(state); attach.RegistrySnapshot = registry; attach.InventoryEndpoints = [sourceCap, destinationCap];
            Assert.IsType<EffectIdle>(host.Attach(attach, clock));
            var sync = Assert.IsType<EffectProposal>(host.Poll(PollFor(state), clock));
            var synced = AcceptedStateCodec.SyncDepots(state, sync.OperationId, sync.ClientRequestId, sync.CommandSequence, sync.PayloadHash, sync.ExpectedRevision, sync.ExpectedStateHash, registry, 20).State;
            Assert.IsType<EffectIdle>(host.Receipt(ReceiptFor(sync, synced, 20), clock));
            var observation = Observation(registry, sourceCap, state.WorldId, 3_000_000, 4_000_000, 3_000_000, 4_000_000);
            var route = new RouteVersionRecord { RouteId = "route", Version = 1, SourceDepotId = "source", SourceMembershipRevision = 1, SourceMembershipHash = new string('a', 64), DestinationDepotId = "destination", DestinationMembershipRevision = 1, DestinationMembershipHash = new string('b', 64), TravelDurationSeconds = 12.5, Provenance = "synthetic", Resources = [new ResourceAmount { ResourceName = "Fuel", AmountMicroUnits = 2_000_000 }] };
            var routeRequest = new SubmitCommand { ProtocolVersion = 1, MessageType = "submitCommand", ClientRequestId = "route-config", WorldId = state.WorldId, RunId = "run-one", CommandKind = "routeUpsert", Delivery = new DeliveryCommandPayload { Kind = "routeUpsert", RouteVersion = route } };
            Assert.Equal("pending", host.Submit(routeRequest).Status);
            var routeProposal = Assert.IsType<EffectProposal>(host.Poll(PollFor(synced), clock));
            var routeAccepted = AcceptedStateCodec.UpsertRoute(synced, routeProposal.OperationId, routeProposal.ClientRequestId, routeProposal.CommandSequence, routeProposal.PayloadHash, routeProposal.ExpectedRevision, routeProposal.ExpectedStateHash, routeProposal.Delivery!.RouteVersion!, 21).State;
            Assert.IsType<EffectIdle>(host.Receipt(ReceiptFor(routeProposal, routeAccepted, 21), clock));
            var stockPoll = PollFor(routeAccepted); stockPoll.InventoryObservations = [observation]; Assert.IsType<EffectIdle>(host.Poll(stockPoll, clock));

            var heldSend = new SubmitCommand { ProtocolVersion = 1, MessageType = "submitCommand", ClientRequestId = "send-held", WorldId = state.WorldId, RunId = "run-one", CommandKind = "sendOnce", Delivery = new DeliveryCommandPayload { Kind = "sendOnce", RouteId = "route", RouteVersionNumber = 1 } };
            Assert.Equal("pending", host.Submit(heldSend).Status);
            var heldPoll = PollFor(routeAccepted); heldPoll.InventoryObservations = [observation];
            var heldProposal = Assert.IsType<EffectProposal>(host.Poll(heldPoll, clock));
            var heldReceipt = ReceiptFor(heldProposal, routeAccepted, 22); heldReceipt.Outcome = "held"; heldReceipt.Reason = "Physical endpoint changed before apply.";
            Assert.IsType<EffectIdle>(host.Receipt(heldReceipt, clock));
            var heldStatus = host.GetCommandStatus(new GetCommandStatus { ProtocolVersion = 1, MessageType = "getCommandStatus", ClientRequestId = heldSend.ClientRequestId, WorldId = state.WorldId, RunId = "run-one" });
            Assert.Equal("rejected", heldStatus.Status); Assert.True(heldStatus.ConfirmedTerminal);
            Assert.True(SendOnceRequestLifecycle.IsTerminal(heldStatus));
            var heldReplay = host.Submit(heldSend);
            Assert.Equal("rejected", heldReplay.Status); Assert.True(heldReplay.ConfirmedTerminal);

            var send = new SubmitCommand { ProtocolVersion = 1, MessageType = "submitCommand", ClientRequestId = "send-1", WorldId = state.WorldId, RunId = "run-one", CommandKind = "sendOnce", Delivery = new DeliveryCommandPayload { Kind = "sendOnce", RouteId = "route", RouteVersionNumber = 1 } };
            Assert.Equal("pending", host.Submit(send).Status);
            using (var detached = new RecoveryCoordinator(directory, verifier))
            {
                var uncertainRetry = detached.Submit(send);
                Assert.Equal("rejected", uncertainRetry.Status);
                Assert.False(uncertainRetry.ConfirmedTerminal); // A detached Host cannot disprove the prepared debit.
                Assert.False(SendOnceRequestLifecycle.IsTerminal(uncertainRetry));
                var detachedStatus = detached.GetCommandStatus(new GetCommandStatus { ProtocolVersion = 1, MessageType = "getCommandStatus", ClientRequestId = send.ClientRequestId, WorldId = state.WorldId, RunId = "run-one" });
                Assert.Equal("unknown", detachedStatus.Status);
                Assert.False(SendOnceRequestLifecycle.IsTerminal(detachedStatus));
            }
            var sendPoll = PollFor(routeAccepted); sendPoll.InventoryObservations = [observation];
            var dispatch = Assert.IsType<EffectProposal>(host.Poll(sendPoll, clock));
            Assert.Equal("dispatch", dispatch.OperationKind); Assert.Equal(0, dispatch.Delivery!.Shipment!.DepartureUt); Assert.Equal(0, dispatch.Delivery.Shipment.DueUt);
            Assert.Equal(2_000_000, Math.Abs(dispatch.Delivery.PhysicalEffect!.Deltas.Sum(d => d.DeltaMicroUnits)));
            var appliedUt = 1_234_567.25; // Deliberately far beyond Host's sample: receipt UT is the sole departure authority.
            var dispatched = AcceptedStateCodec.Dispatch(routeAccepted, dispatch.OperationId, dispatch.ClientRequestId, dispatch.CommandSequence, dispatch.PayloadHash, dispatch.ExpectedRevision, dispatch.ExpectedStateHash, dispatch.Delivery.Shipment, dispatch.Delivery.PhysicalEffect, appliedUt, null, SuccessWitness(dispatch.Delivery.PhysicalEffect, 10));
            Assert.Equal("accepted", dispatched.Outcome); Assert.Equal(appliedUt, Assert.Single(dispatched.State.ActiveShipments).DepartureUt); Assert.Equal(appliedUt + 12.5, dispatched.State.ActiveShipments[0].DueUt);
            var receipt = ReceiptFor(dispatch, dispatched.State, appliedUt);
            receipt.PhysicalResult = new PhysicalEffectResult { Status = "applied", RollbackStatus = "none", Deltas = dispatch.Delivery.PhysicalEffect.Deltas.Select(d => new PhysicalResourceDelta { MemberPersistentId = d.MemberPersistentId, ResourceName = d.ResourceName, BeforeAmount = 10, IntendedDeltaMicroUnits = d.DeltaMicroUnits, IntendedAfterAmount = 10 + d.DeltaMicroUnits / 1_000_000d, ObservedAfterKnown = true, ObservedAfterAmount = 10 + d.DeltaMicroUnits / 1_000_000d }).ToArray() };
            Assert.IsType<EffectIdle>(host.Receipt(receipt, clock));
            var status = host.GetCommandStatus(new GetCommandStatus { ProtocolVersion = 1, MessageType = "getCommandStatus", ClientRequestId = "send-1", WorldId = state.WorldId, RunId = "run-one" });
            Assert.Equal("accepted", status.Status);
            using (var detached = new RecoveryCoordinator(directory, verifier))
            {
                var detachedSettled = detached.GetCommandStatus(new GetCommandStatus { ProtocolVersion = 1, MessageType = "getCommandStatus", ClientRequestId = send.ClientRequestId, WorldId = state.WorldId, RunId = "run-one" });
                Assert.Equal("unknown", detachedSettled.Status);
                Assert.False(SendOnceRequestLifecycle.IsTerminal(detachedSettled));
            }
            var duplicateSend = host.Submit(send); Assert.Equal("accepted", duplicateSend.Status); Assert.Equal(dispatch.OperationId, duplicateSend.OperationId);

            clock.Accept(new ClockSample(1, "clockSample", 2, Session, Epoch, Install, Save, "Synthetic", 2_000_000, true, "Flight", false, "Year 1", 1, WorldId: state.WorldId, RunId: "run-one"));
            var destinationStock = Observation(registry, destinationCap, state.WorldId, 0, 1_000_000, 0, 1_000_000);
            var arrivalPoll = PollFor(dispatched.State); arrivalPoll.InventoryObservations = [destinationStock];
            var arrival = Assert.IsType<EffectProposal>(host.Poll(arrivalPoll, clock)); Assert.Equal("arrival", arrival.OperationKind); Assert.Equal(1_000_000, Assert.Single(arrival.Delivery!.Credits).AmountMicroUnits); Assert.Equal(1_000_000, Assert.Single(arrival.Delivery.RemainingCargo).AmountMicroUnits);
            var arrived = AcceptedStateCodec.Arrive(dispatched.State, arrival.OperationId, arrival.ClientRequestId, arrival.CommandSequence, arrival.PayloadHash, arrival.ExpectedRevision, arrival.ExpectedStateHash, arrival.Delivery.ShipmentId, arrival.Delivery.Credits, arrival.Delivery.RemainingCargo, arrival.Delivery.PhysicalEffect!, 2_000_000, SuccessWitness(arrival.Delivery.PhysicalEffect!, 0));
            Assert.Equal("accepted", arrived.Outcome);
            var arrivalReceipt = ReceiptFor(arrival, arrived.State, 2_000_000);
            arrivalReceipt.PhysicalResult = new PhysicalEffectResult { Status = "applied", RollbackStatus = "none", Deltas = arrival.Delivery.PhysicalEffect!.Deltas.Select(d => new PhysicalResourceDelta { MemberPersistentId = d.MemberPersistentId, ResourceName = d.ResourceName, BeforeAmount = 0, IntendedDeltaMicroUnits = d.DeltaMicroUnits, IntendedAfterAmount = 1, ObservedAfterKnown = true, ObservedAfterAmount = 1 }).ToArray() };
            Assert.IsType<EffectIdle>(host.Receipt(arrivalReceipt, clock)); Assert.Equal(1_000_000, Assert.Single(arrived.State.ActiveShipments).RemainingResources[0].AmountMicroUnits);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task SendOnceStockFreshnessNeedsANewCompletedObservationRevisionAfterSixSeconds()
    {
        var directory = NewDirectory(); var verifier = new AlwaysLiveVerifier(); var state = InitialState(); var clock = FreshClock(state.WorldId);
        try
        {
            var sourceHash = new string('a', 64); var destinationHash = new string('b', 64);
            var registry = new DepotRegistrySnapshot { RegistryVersion = "fresh-scan-registry", Depots = [new DepotRecord { DepotId = "source", MembershipRevision = 1, MembershipHash = sourceHash }, new DepotRecord { DepotId = "destination", MembershipRevision = 1, MembershipHash = destinationHash }] };
            registry.RegistryHash = OperationIdentity.ComputeDepotRegistryHash(registry.Depots);
            var sourceCap = WritableCapability("source", 1, sourceHash, 7, [7]); var destinationCap = WritableCapability("destination", 1, destinationHash, 9, [9]);
            using var host = new RecoveryCoordinator(directory, verifier);
            var attach = AttachFor(state); attach.RegistrySnapshot = registry; attach.InventoryEndpoints = [sourceCap, destinationCap];
            Assert.IsType<EffectIdle>(host.Attach(attach, clock));
            var sync = Assert.IsType<EffectProposal>(host.Poll(PollFor(state), clock));
            state = AcceptedStateCodec.SyncDepots(state, sync.OperationId, sync.ClientRequestId, sync.CommandSequence, sync.PayloadHash, sync.ExpectedRevision, sync.ExpectedStateHash, registry, 1).State;
            Assert.IsType<EffectIdle>(host.Receipt(ReceiptFor(sync, state, 1), clock));

            var route = new RouteVersionRecord { RouteId = "freshness-route", Version = 1, SourceDepotId = "source", SourceMembershipRevision = 1, SourceMembershipHash = sourceHash, DestinationDepotId = "destination", DestinationMembershipRevision = 1, DestinationMembershipHash = destinationHash, TravelDurationSeconds = 60, Provenance = "fresh observation test", Resources = [new ResourceAmount { ResourceName = "Fuel", AmountMicroUnits = 2_000_000 }] };
            var routeRequest = new SubmitCommand { ProtocolVersion = 1, MessageType = "submitCommand", ClientRequestId = "freshness-route-upsert", WorldId = state.WorldId, RunId = "run-one", CommandKind = "routeUpsert", Delivery = new DeliveryCommandPayload { Kind = "routeUpsert", RouteVersion = route } };
            Assert.Equal("pending", host.Submit(routeRequest).Status);
            var routeProposal = Assert.IsType<EffectProposal>(host.Poll(PollFor(state), clock));
            state = AcceptedStateCodec.UpsertRoute(state, routeProposal.OperationId, routeProposal.ClientRequestId, routeProposal.CommandSequence, routeProposal.PayloadHash, routeProposal.ExpectedRevision, routeProposal.ExpectedStateHash, routeProposal.Delivery!.RouteVersion!, 2).State;
            Assert.IsType<EffectIdle>(host.Receipt(ReceiptFor(routeProposal, state, 2), clock));

            var sameRowsRevisionOne = Observation(registry, sourceCap, state.WorldId, 3_000_000, 4_000_000, 3_000_000, 4_000_000, observationRevision: 1);
            var poll = PollFor(state); poll.InventoryObservations = [sameRowsRevisionOne]; Assert.IsType<EffectIdle>(host.Poll(poll, clock));
            // Repeated transport frames do not represent new scans: the first-sighting age must keep advancing.
            for (var i = 0; i < 6; i++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1100));
                clock.Accept(new ClockSample(1, "clockSample", i + 2, Session, Epoch, Install, Save, "Synthetic", 100, true, "Flight", false, "Year 1", 1, WorldId: state.WorldId, RunId: "run-one"));
                poll = PollFor(state); poll.InventoryObservations = [sameRowsRevisionOne]; Assert.IsType<EffectIdle>(host.Poll(poll, clock));
            }
            var send = new SubmitCommand { ProtocolVersion = 1, MessageType = "submitCommand", ClientRequestId = "freshness-send", WorldId = state.WorldId, RunId = "run-one", CommandKind = "sendOnce", Delivery = new DeliveryCommandPayload { Kind = "sendOnce", RouteId = route.RouteId, RouteVersionNumber = route.Version } };
            var stale = host.Submit(send);
            Assert.Equal("rejected", stale.Status); Assert.Contains("Fresh source stock", stale.Reason, StringComparison.OrdinalIgnoreCase);

            // A higher revision is a new completed scan even if all stock values are identical.
            var sameRowsRevisionTwo = Observation(registry, sourceCap, state.WorldId, 3_000_000, 4_000_000, 3_000_000, 4_000_000, observationRevision: 2);
            poll = PollFor(state); poll.InventoryObservations = [sameRowsRevisionTwo]; Assert.IsType<EffectIdle>(host.Poll(poll, clock));
            var fresh = host.Submit(send);
            Assert.Equal("pending", fresh.Status); Assert.False(string.IsNullOrWhiteSpace(fresh.OperationId));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void PhysicalDispatchLostAckReconcilesFromCapsuleWitnessAfterHostRestart()
    {
        var directory = NewDirectory(); var verifier = new AlwaysLiveVerifier(); var state = InitialState(); var clock = FreshClock(state.WorldId);
        var sourceHash = new string('a', 64); var destinationHash = new string('b', 64);
        var registry = new DepotRegistrySnapshot { RegistryVersion = "lost-ack-registry", Depots = [new DepotRecord { DepotId = "source", MembershipRevision = 1, MembershipHash = sourceHash }, new DepotRecord { DepotId = "destination", MembershipRevision = 1, MembershipHash = destinationHash }] };
        registry.RegistryHash = OperationIdentity.ComputeDepotRegistryHash(registry.Depots);
        var sourceCap = WritableCapability("source", 1, sourceHash, 7, [7]); var destinationCap = WritableCapability("destination", 1, destinationHash, 9, [9]);
        var route = new RouteVersionRecord { RouteId = "lost-ack-route", Version = 1, SourceDepotId = "source", SourceMembershipRevision = 1, SourceMembershipHash = sourceHash, DestinationDepotId = "destination", DestinationMembershipRevision = 1, DestinationMembershipHash = destinationHash, TravelDurationSeconds = 60, Provenance = "lost ACK recovery test", Resources = [new ResourceAmount { ResourceName = "Fuel", AmountMicroUnits = 2_000_000 }] };
        var sourceObservation = Observation(registry, sourceCap, state.WorldId, 3_000_000, 4_000_000, 3_000_000, 4_000_000);
        var send = new SubmitCommand { ProtocolVersion = 1, MessageType = "submitCommand", ClientRequestId = "dispatch-lost-ack", WorldId = state.WorldId, RunId = "run-one", CommandKind = "sendOnce", Delivery = new DeliveryCommandPayload { Kind = "sendOnce", RouteId = route.RouteId, RouteVersionNumber = route.Version } };
        AcceptedState applied; string operationId;
        try
        {
            using (var firstHost = new RecoveryCoordinator(directory, verifier))
            {
                var attach = AttachFor(state); attach.RegistrySnapshot = registry; attach.InventoryEndpoints = [sourceCap, destinationCap];
                Assert.IsType<EffectIdle>(firstHost.Attach(attach, clock));
                var sync = Assert.IsType<EffectProposal>(firstHost.Poll(PollFor(state), clock));
                state = AcceptedStateCodec.SyncDepots(state, sync.OperationId, sync.ClientRequestId, sync.CommandSequence, sync.PayloadHash, sync.ExpectedRevision, sync.ExpectedStateHash, registry, 10).State;
                Assert.IsType<EffectIdle>(firstHost.Receipt(ReceiptFor(sync, state, 10), clock));

                var routeRequest = new SubmitCommand { ProtocolVersion = 1, MessageType = "submitCommand", ClientRequestId = "lost-ack-route-upsert", WorldId = state.WorldId, RunId = "run-one", CommandKind = "routeUpsert", Delivery = new DeliveryCommandPayload { Kind = "routeUpsert", RouteVersion = route } };
                Assert.Equal("pending", firstHost.Submit(routeRequest).Status);
                var routeProposal = Assert.IsType<EffectProposal>(firstHost.Poll(PollFor(state), clock));
                state = AcceptedStateCodec.UpsertRoute(state, routeProposal.OperationId, routeProposal.ClientRequestId, routeProposal.CommandSequence, routeProposal.PayloadHash, routeProposal.ExpectedRevision, routeProposal.ExpectedStateHash, routeProposal.Delivery!.RouteVersion!, 11).State;
                Assert.IsType<EffectIdle>(firstHost.Receipt(ReceiptFor(routeProposal, state, 11), clock));

                var poll = PollFor(state); poll.InventoryObservations = [sourceObservation]; Assert.IsType<EffectIdle>(firstHost.Poll(poll, clock));
                Assert.Equal("pending", firstHost.Submit(send).Status);
                poll = PollFor(state); poll.InventoryObservations = [sourceObservation];
                var proposal = Assert.IsType<EffectProposal>(firstHost.Poll(poll, clock)); operationId = proposal.OperationId;
                var witness = SuccessWitness(proposal.Delivery!.PhysicalEffect!, 3);
                var transition = AcceptedStateCodec.Dispatch(state, proposal.OperationId, proposal.ClientRequestId, proposal.CommandSequence, proposal.PayloadHash, proposal.ExpectedRevision, proposal.ExpectedStateHash, proposal.Delivery.Shipment!, proposal.Delivery.PhysicalEffect!, 123.5, null, witness);
                Assert.Equal("accepted", transition.Outcome); Assert.Equal(3, transition.State.CapsuleEncodingVersion);
                applied = transition.State;
                // Fault point: KSP has applied and saved this capsule; Host never receives the ACK.
            }

            using var restarted = new RecoveryCoordinator(directory, verifier);
            var recovered = Assert.IsType<EffectIdle>(restarted.Attach(AttachFor(applied), clock));
            Assert.Contains("attached", recovered.Reason, StringComparison.OrdinalIgnoreCase);
            var status = restarted.GetCommandStatus(new GetCommandStatus { ProtocolVersion = 1, MessageType = "getCommandStatus", ClientRequestId = send.ClientRequestId, WorldId = state.WorldId, RunId = "run-one" });
            Assert.Equal("accepted", status.Status); Assert.Equal(operationId, status.OperationId); Assert.Equal(AcceptedStateCodec.ComputeHash(applied), status.StateHash);
            Assert.Equal(operationId, restarted.Submit(send).OperationId);
            Assert.Single(applied.ActiveShipments);
            Assert.Single(applied.Receipts, receipt => receipt.OperationId == operationId && receipt.OperationKind == "dispatch");
            var idle = Assert.IsType<EffectIdle>(restarted.Poll(PollFor(applied), clock));
            Assert.Contains("No prepared", idle.Reason);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void PreparedPhysicalDispatchMissingFromSelectedBranchIsHeldAndNeverReoffered()
    {
        var directory = NewDirectory(); var state = InitialState(); var clock = FreshClock(state.WorldId);
        var sourceHash = new string('a', 64); var destinationHash = new string('b', 64);
        var registry = new DepotRegistrySnapshot { RegistryVersion = "branch-registry", Depots = [new DepotRecord { DepotId = "source", MembershipRevision = 1, MembershipHash = sourceHash }, new DepotRecord { DepotId = "destination", MembershipRevision = 1, MembershipHash = destinationHash }] };
        registry.RegistryHash = OperationIdentity.ComputeDepotRegistryHash(registry.Depots);
        var sourceCap = WritableCapability("source", 1, sourceHash, 7, [7]); var destinationCap = WritableCapability("destination", 1, destinationHash, 9, [9]);
        var route = new RouteVersionRecord { RouteId = "branch-route", Version = 1, SourceDepotId = "source", SourceMembershipRevision = 1, SourceMembershipHash = sourceHash, DestinationDepotId = "destination", DestinationMembershipRevision = 1, DestinationMembershipHash = destinationHash, TravelDurationSeconds = 60, Provenance = "branch test", Resources = [new ResourceAmount { ResourceName = "Fuel", AmountMicroUnits = 2_000_000 }] };
        var sync = AcceptedStateCodec.SyncDepots(state, OperationIdentity.Create(state.WorldId, 1, "sync"), "sync", 1, OperationIdentity.DepotRegistryPayloadHash(registry), state.Revision, AcceptedStateCodec.ComputeHash(state), registry, 10);
        state = sync.State;
        state = AcceptedStateCodec.UpsertRoute(state, OperationIdentity.Create(state.WorldId, 2, "route"), "route", 2, OperationIdentity.RouteVersionPayloadHash(route), state.Revision, AcceptedStateCodec.ComputeHash(state), route, 11).State;
        var send = new SubmitCommand { ProtocolVersion = 1, MessageType = "submitCommand", ClientRequestId = "old-branch-physical", WorldId = state.WorldId, RunId = "run-one", CommandKind = "sendOnce", Delivery = new DeliveryCommandPayload { Kind = "sendOnce", RouteId = route.RouteId, RouteVersionNumber = route.Version } };
        try
        {
            using var host = new RecoveryCoordinator(directory, new AlwaysLiveVerifier());
            var attach = AttachFor(state); attach.RegistrySnapshot = registry; attach.InventoryEndpoints = [sourceCap, destinationCap];
            Assert.IsType<EffectIdle>(host.Attach(attach, clock));
            var observation = Observation(registry, sourceCap, state.WorldId, 3_000_000, 4_000_000, 3_000_000, 4_000_000);
            var poll = PollFor(state); poll.InventoryObservations = [observation]; Assert.IsType<EffectIdle>(host.Poll(poll, clock));
            Assert.Equal("pending", host.Submit(send).Status);
            poll = PollFor(state); poll.InventoryObservations = [observation];
            var oldPrepared = Assert.IsType<EffectProposal>(host.Poll(poll, clock)); Assert.Equal("dispatch", oldPrepared.OperationKind);

            clock.Accept(new ClockSample(1, "clockSample", 2, Session, Epoch, Install, Save, "Synthetic", 100, true, "Flight", false, "Year 1", 1, WorldId: state.WorldId, RunId: "branch-run"));
            var branchAttach = AttachFor(state, run: "branch-run"); branchAttach.RegistrySnapshot = registry; branchAttach.InventoryEndpoints = [sourceCap, destinationCap];
            Assert.IsType<EffectIdle>(host.Attach(branchAttach, clock)); // selected capsule excludes the unreceipted physical proposal
            var current = host.GetAcceptedState(new GetAcceptedState { ProtocolVersion = 1, MessageType = "getAcceptedState", WorldId = state.WorldId, RunId = "branch-run" });
            Assert.Equal("available", current.Status); Assert.Equal(2, current.AcceptedSequence);
            Assert.Equal("historical", host.GetCommandStatus(new GetCommandStatus { ProtocolVersion = 1, MessageType = "getCommandStatus", ClientRequestId = send.ClientRequestId, WorldId = state.WorldId, RunId = "branch-run" }).Status);
            Assert.IsType<EffectIdle>(host.Poll(PollFor(state, "branch-run"), clock)); // held operation is archival, never reoffered

            var repeatedObservation = Observation(registry, sourceCap, state.WorldId, 3_000_000, 4_000_000, 3_000_000, 4_000_000, observationRevision: 2);
            poll = PollFor(state, "branch-run"); poll.InventoryObservations = [repeatedObservation]; Assert.IsType<EffectIdle>(host.Poll(poll, clock));
            var newSend = new SubmitCommand { ProtocolVersion = 1, MessageType = "submitCommand", ClientRequestId = "new-branch-physical", WorldId = state.WorldId, RunId = "branch-run", CommandKind = "sendOnce", Delivery = new DeliveryCommandPayload { Kind = "sendOnce", RouteId = route.RouteId, RouteVersionNumber = route.Version } };
            var preparedAgain = host.Submit(newSend); Assert.Equal("pending", preparedAgain.Status); Assert.NotEqual(oldPrepared.OperationId, preparedAgain.OperationId);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void HostRunsRepeatRuleOnceAfterBlockedHugeUtJumpAndAtomicallyAdvancesRule()
    {
        var directory = NewDirectory(); var verifier = new AlwaysLiveVerifier(); var state = InitialState(); var clock = FreshClock(state.WorldId); long scheduleTicks = 0;
        try
        {
            var registry = new DepotRegistrySnapshot { RegistryVersion = "registry", Depots = [new DepotRecord { DepotId = "source", MembershipRevision = 1, MembershipHash = new string('a', 64) }, new DepotRecord { DepotId = "destination", MembershipRevision = 1, MembershipHash = new string('b', 64) }] };
            registry.RegistryHash = OperationIdentity.ComputeDepotRegistryHash(registry.Depots);
            var sourceCap = WritableCapability("source", 1, new string('a', 64), 7, [7]); var destinationCap = WritableCapability("destination", 1, new string('b', 64), 9, [9]);
            using var host = new RecoveryCoordinator(directory, verifier, monotonicTimestampProvider: () => scheduleTicks);
            var attach = AttachFor(state); attach.RegistrySnapshot = registry; attach.InventoryEndpoints = [sourceCap, destinationCap];
            Assert.IsType<EffectIdle>(host.Attach(attach, clock));
            var sync = Assert.IsType<EffectProposal>(host.Poll(PollFor(state), clock));
            var synced = AcceptedStateCodec.SyncDepots(state, sync.OperationId, sync.ClientRequestId, sync.CommandSequence, sync.PayloadHash, sync.ExpectedRevision, sync.ExpectedStateHash, registry, 101).State;
            Assert.IsType<EffectIdle>(host.Receipt(ReceiptFor(sync, synced, 101), clock));
            var route = new RouteVersionRecord { RouteId = "repeat-route", Version = 1, SourceDepotId = "source", SourceMembershipRevision = 1, SourceMembershipHash = new string('a', 64), DestinationDepotId = "destination", DestinationMembershipRevision = 1, DestinationMembershipHash = new string('b', 64), TravelDurationSeconds = 120, Provenance = "repeat schedule fixture", Resources = [new ResourceAmount { ResourceName = "Fuel", AmountMicroUnits = 2_000_000 }] };
            var routeRequest = new SubmitCommand { ProtocolVersion = 1, MessageType = "submitCommand", ClientRequestId = "repeat-route-upsert", WorldId = state.WorldId, RunId = "run-one", CommandKind = "routeUpsert", Delivery = new DeliveryCommandPayload { Kind = "routeUpsert", RouteVersion = route } };
            Assert.Equal("pending", host.Submit(routeRequest).Status);
            var routeProposal = Assert.IsType<EffectProposal>(host.Poll(PollFor(synced), clock));
            var routeAccepted = AcceptedStateCodec.UpsertRoute(synced, routeProposal.OperationId, routeProposal.ClientRequestId, routeProposal.CommandSequence, routeProposal.PayloadHash, routeProposal.ExpectedRevision, routeProposal.ExpectedStateHash, routeProposal.Delivery!.RouteVersion!, 102).State;
            Assert.IsType<EffectIdle>(host.Receipt(ReceiptFor(routeProposal, routeAccepted, 102), clock));

            var rule = new DeliveryRuleRecord { RuleId = "repeat-rule", Revision = 1, Kind = "repeat", RouteId = route.RouteId, RouteVersion = route.Version, Enabled = true, NextDueUt = 110, IntervalSeconds = 10 };
            var ruleRequest = new SubmitCommand { ProtocolVersion = 1, MessageType = "submitCommand", ClientRequestId = "repeat-rule-upsert", WorldId = state.WorldId, RunId = "run-one", CommandKind = "ruleUpsert", Delivery = new DeliveryCommandPayload { Kind = "ruleUpsert", Rule = rule } };
            Assert.Equal("pending", host.Submit(ruleRequest).Status);
            var ruleProposal = Assert.IsType<EffectProposal>(host.Poll(PollFor(routeAccepted), clock));
            var ruled = AcceptedStateCodec.UpsertRule(routeAccepted, ruleProposal.OperationId, ruleProposal.ClientRequestId, ruleProposal.CommandSequence, ruleProposal.PayloadHash, ruleProposal.ExpectedRevision, ruleProposal.ExpectedStateHash, ruleProposal.Delivery!.Rule!, 103).State;
            Assert.IsType<EffectIdle>(host.Receipt(ReceiptFor(ruleProposal, ruled, 103), clock));

            var insufficient = Observation(registry, sourceCap, state.WorldId, 1_000_000, 4_000_000, 1_000_000, 4_000_000, 1);
            var hugeUt = 10_000_000_000d;
            clock.Accept(new ClockSample(1, "clockSample", 2, Session, Epoch, Install, Save, "Synthetic", hugeUt, true, "Flight", false, "Year 1", 1, WorldId: state.WorldId, RunId: "run-one"));
            var blockedPoll = PollFor(ruled); blockedPoll.InventoryObservations = [insufficient];
            var blocked = Assert.IsType<EffectIdle>(host.Poll(blockedPoll, clock));
            Assert.Contains("No prepared", blocked.Reason);
            Assert.Equal(110, Assert.Single(ruled.DeliveryRules).NextDueUt); // Blocked inventory did not consume schedule slots.

            var sufficient = Observation(registry, sourceCap, state.WorldId, 3_000_000, 4_000_000, 3_000_000, 4_000_000, 2);
            var readyPoll = PollFor(ruled); readyPoll.InventoryObservations = [sufficient];
            var dispatch = Assert.IsType<EffectProposal>(host.Poll(readyPoll, clock));
            Assert.Equal("dispatch", dispatch.OperationKind); Assert.NotNull(dispatch.Delivery!.ScheduleRuleUpdate);
            Assert.Equal(10_000_000_000d + 10, dispatch.Delivery.ScheduleRuleUpdate!.NextDueUt);
            Assert.Equal(110, dispatch.Delivery.ScheduleRuleUpdate.WaitingScheduledUt);
            Assert.True(dispatch.Delivery.ScheduleRuleUpdate.WaitingCoalescedSlots > 1);
            Assert.False(dispatch.Delivery.ScheduleRuleUpdate.WaitingRequest);
            Assert.Equal(2_000_000, Math.Abs(dispatch.Delivery.PhysicalEffect!.Deltas.Sum(x => x.DeltaMicroUnits)));

            var heldReceipt = ReceiptFor(dispatch, ruled, hugeUt); heldReceipt.Outcome = "held"; heldReceipt.Reason = "PartResource, BRP Amount, OriginalAmount and Snapshot baseline are not exactly coherent";
            Assert.IsType<EffectIdle>(host.Receipt(heldReceipt, clock));
            var repeatedScan = Observation(registry, sourceCap, state.WorldId, 3_000_000, 4_000_000, 3_000_000, 4_000_000, 3);
            var sameBackoffBucketPoll = PollFor(ruled); sameBackoffBucketPoll.InventoryObservations = [repeatedScan];
            Assert.IsType<EffectIdle>(host.Poll(sameBackoffBucketPoll, clock)); // Fresh identical scans do not make another durable attempt.

            scheduleTicks += 16L * System.Diagnostics.Stopwatch.Frequency;
            var nextBackoffBucketPoll = PollFor(ruled); nextBackoffBucketPoll.InventoryObservations = [Observation(registry, sourceCap, state.WorldId, 3_000_000, 4_000_000, 3_000_000, 4_000_000, 4)];
            dispatch = Assert.IsType<EffectProposal>(host.Poll(nextBackoffBucketPoll, clock));
            Assert.NotEqual(heldReceipt.OperationId, dispatch.OperationId); // A new bounded retry is allowed after real monotonic time advances.

            var applyUt = hugeUt + 250;
            var dispatchDelivery = dispatch.Delivery!;
            var transition = AcceptedStateCodec.Dispatch(ruled, dispatch.OperationId, dispatch.ClientRequestId, dispatch.CommandSequence, dispatch.PayloadHash, dispatch.ExpectedRevision, dispatch.ExpectedStateHash, dispatchDelivery.Shipment!, dispatchDelivery.PhysicalEffect!, applyUt, dispatchDelivery.ScheduleRuleUpdate, SuccessWitness(dispatchDelivery.PhysicalEffect!, 10));
            Assert.Equal("accepted", transition.Outcome);
            var savedRule = Assert.Single(transition.State.DeliveryRules);
            Assert.Equal(hugeUt + 10, savedRule.NextDueUt); Assert.Equal(110, savedRule.WaitingScheduledUt); Assert.True(savedRule.WaitingCoalescedSlots > 1);
            Assert.Equal(applyUt, Assert.Single(transition.State.ActiveShipments).DepartureUt);
            var receipt = ReceiptFor(dispatch, transition.State, applyUt);
            receipt.PhysicalResult = new PhysicalEffectResult { Status = "applied", RollbackStatus = "none", Deltas = dispatchDelivery.PhysicalEffect!.Deltas.Select(d => new PhysicalResourceDelta { MemberPersistentId = d.MemberPersistentId, ResourceName = d.ResourceName, BeforeAmount = 10, IntendedDeltaMicroUnits = d.DeltaMicroUnits, IntendedAfterAmount = 10 + d.DeltaMicroUnits / 1_000_000d, ObservedAfterKnown = true, ObservedAfterAmount = 10 + d.DeltaMicroUnits / 1_000_000d }).ToArray() };
            Assert.IsType<EffectIdle>(host.Receipt(receipt, clock));
            var status = host.GetCommandStatus(new GetCommandStatus { ProtocolVersion = 1, MessageType = "getCommandStatus", ClientRequestId = dispatch.ClientRequestId, WorldId = state.WorldId, RunId = "run-one" });
            Assert.Equal("accepted", status.Status); Assert.NotNull(status.AcceptedCapsule);
            Assert.Equal(hugeUt + 10, Assert.Single(AcceptedStateCodec.ReadCapsule(status.AcceptedCapsule!).DeliveryRules).NextDueUt);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void HostKeepStockUsesUniqueInboundOnceAndDispatchesOnlyTargetShortfall()
    {
        var directory = NewDirectory(); var verifier = new AlwaysLiveVerifier(); var state = InitialState(); var clock = FreshClock(state.WorldId);
        try
        {
            var sourceHash = new string('a', 64); var destinationHash = new string('b', 64);
            var registry = new DepotRegistrySnapshot { RegistryVersion = "registry", Depots = [new DepotRecord { DepotId = "source", MembershipRevision = 1, MembershipHash = sourceHash }, new DepotRecord { DepotId = "destination", MembershipRevision = 1, MembershipHash = destinationHash }] };
            registry.RegistryHash = OperationIdentity.ComputeDepotRegistryHash(registry.Depots);
            var sourceCap = WritableCapability("source", 1, sourceHash, 7, [7]); var destinationCap = WritableCapability("destination", 1, destinationHash, 9, [9]);
            var sync = AcceptedStateCodec.SyncDepots(state, OperationIdentity.Create(state.WorldId, 1, "sync"), "sync", 1, OperationIdentity.DepotRegistryPayloadHash(registry), state.Revision, AcceptedStateCodec.ComputeHash(state), registry, 10);
            state = sync.State;
            var route = new RouteVersionRecord { RouteId = "stock-route", Version = 1, SourceDepotId = "source", SourceMembershipRevision = 1, SourceMembershipHash = sourceHash, DestinationDepotId = "destination", DestinationMembershipRevision = 1, DestinationMembershipHash = destinationHash, TravelDurationSeconds = 120, Provenance = "keep-stock fixture", Resources = [new ResourceAmount { ResourceName = "Fuel", AmountMicroUnits = 100_000_000 }] };
            var routeRequestId = "route"; state = AcceptedStateCodec.UpsertRoute(state, OperationIdentity.Create(state.WorldId, 2, routeRequestId), routeRequestId, 2, OperationIdentity.RouteVersionPayloadHash(route), state.Revision, AcceptedStateCodec.ComputeHash(state), route, 11).State;
            var seedIntent = new PhysicalEffectIntent { EffectKind = "dispatchDebit", Capability = sourceCap, MembershipRevision = 1, MemberPersistentIds = [7], Deltas = [new PhysicalEffectResourceDelta { MemberPersistentId = 7, ResourceName = "Fuel", DeltaMicroUnits = -100_000_000 }] };
            var seedShipment = new ActiveShipmentRecord { ShipmentId = "existing-inbound", RouteId = route.RouteId, RouteVersion = route.Version, SourceDepotId = "source", DestinationDepotId = "destination", RemainingResources = [new ResourceAmount { ResourceName = "Fuel", AmountMicroUnits = 100_000_000 }] };
            var seedRequestId = "seed-dispatch"; var seedHash = OperationIdentity.DispatchPayloadHash(seedShipment, seedIntent);
            state = AcceptedStateCodec.Dispatch(state, OperationIdentity.Create(state.WorldId, 3, seedRequestId), seedRequestId, 3, seedHash, state.Revision, AcceptedStateCodec.ComputeHash(state), seedShipment, seedIntent, 10).State;

            using var host = new RecoveryCoordinator(directory, verifier);
            var attach = AttachFor(state); attach.RegistrySnapshot = registry; attach.InventoryEndpoints = [sourceCap, destinationCap];
            Assert.IsType<EffectIdle>(host.Attach(attach, clock));
            var rule = new DeliveryRuleRecord { RuleId = "keep-fuel", Revision = 1, Kind = "keepStock", RouteId = route.RouteId, RouteVersion = route.Version, Enabled = true, ResourceName = "Fuel", LowTriggerMicroUnits = 130_000_000, TargetMicroUnits = 200_000_000, BatchSizeMicroUnits = 100_000_000 };
            var ruleRequest = new SubmitCommand { ProtocolVersion = 1, MessageType = "submitCommand", ClientRequestId = "keep-rule", WorldId = state.WorldId, RunId = "run-one", CommandKind = "ruleUpsert", Delivery = new DeliveryCommandPayload { Kind = "ruleUpsert", Rule = rule } };
            Assert.Equal("pending", host.Submit(ruleRequest).Status);
            var ruleProposal = Assert.IsType<EffectProposal>(host.Poll(PollFor(state), clock));
            state = AcceptedStateCodec.UpsertRule(state, ruleProposal.OperationId, ruleProposal.ClientRequestId, ruleProposal.CommandSequence, ruleProposal.PayloadHash, ruleProposal.ExpectedRevision, ruleProposal.ExpectedStateHash, ruleProposal.Delivery!.Rule!, 12).State;
            Assert.IsType<EffectIdle>(host.Receipt(ReceiptFor(ruleProposal, state, 12), clock));

            var sourceStock = Observation(registry, sourceCap, state.WorldId, 1_000_000_000, 2_000_000_000, 1_000_000_000, 2_000_000_000);
            var destinationStock = Observation(registry, destinationCap, state.WorldId, 20_000_000, 100_000_000, 20_000_000, 100_000_000);
            var poll = PollFor(state); poll.InventoryObservations = [sourceStock, destinationStock];
            var dispatch = Assert.IsType<EffectProposal>(host.Poll(poll, clock));
            Assert.Equal("dispatch", dispatch.OperationKind); Assert.Equal("keep-fuel", dispatch.Delivery!.ScheduleRuleUpdate!.RuleId);
            Assert.Equal(80_000_000, Assert.Single(dispatch.Delivery.Shipment!.RemainingResources).AmountMicroUnits);
            Assert.Equal(-80_000_000, Assert.Single(dispatch.Delivery.PhysicalEffect!.Deltas).DeltaMicroUnits);
            var appliedUt = 100d;
            var transition = AcceptedStateCodec.Dispatch(state, dispatch.OperationId, dispatch.ClientRequestId, dispatch.CommandSequence, dispatch.PayloadHash, dispatch.ExpectedRevision, dispatch.ExpectedStateHash, dispatch.Delivery.Shipment, dispatch.Delivery.PhysicalEffect, appliedUt, dispatch.Delivery.ScheduleRuleUpdate, SuccessWitness(dispatch.Delivery.PhysicalEffect, 1_000));
            Assert.Equal("accepted", transition.Outcome); Assert.Equal(2, transition.State.ActiveShipments.Length);
            Assert.Contains(transition.State.ActiveShipments, x => x.ShipmentId == "existing-inbound" && x.RemainingResources.Single().AmountMicroUnits == 100_000_000);
            Assert.Contains(transition.State.ActiveShipments, x => x.ShipmentId != "existing-inbound" && x.RemainingResources.Single().AmountMicroUnits == 80_000_000);
            var receipt = ReceiptFor(dispatch, transition.State, appliedUt);
            receipt.PhysicalResult = new PhysicalEffectResult { Status = "applied", RollbackStatus = "none", Deltas = dispatch.Delivery.PhysicalEffect.Deltas.Select(d => new PhysicalResourceDelta { MemberPersistentId = d.MemberPersistentId, ResourceName = d.ResourceName, BeforeAmount = 1_000, IntendedDeltaMicroUnits = d.DeltaMicroUnits, IntendedAfterAmount = 1_000 + d.DeltaMicroUnits / 1_000_000d, ObservedAfterKnown = true, ObservedAfterAmount = 1_000 + d.DeltaMicroUnits / 1_000_000d }).ToArray() };
            Assert.IsType<EffectIdle>(host.Receipt(receipt, clock));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void AcceptedStateReadProjectionIsBoundedVerifiedAndContextScoped()
    {
        var directory = NewDirectory(); var state = InitialState();
        try
        {
            using var host = new RecoveryCoordinator(directory, new AlwaysLiveVerifier());
            Assert.IsType<EffectIdle>(host.Attach(AttachFor(state), FreshClock(state.WorldId)));
            var visible = host.GetAcceptedState(new GetAcceptedState { ProtocolVersion = 1, MessageType = "getAcceptedState", WorldId = state.WorldId, RunId = "run-one" });
            Assert.Equal("available", visible.Status); Assert.Equal(AcceptedStateCodec.ComputeHash(state), visible.StateHash);
            Assert.Equal(state.WorldId, AcceptedStateCodec.ReadCapsule(visible.AcceptedCapsule!).WorldId);
            var mismatch = host.GetAcceptedState(new GetAcceptedState { ProtocolVersion = 1, MessageType = "getAcceptedState", WorldId = state.WorldId, RunId = "copied-run" });
            Assert.Equal("unavailable", mismatch.Status); Assert.Null(mismatch.AcceptedCapsule);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void DueArrivalHoldIsTransientClearsOnRecoveryAndDoesNotCrossWorldLoad()
    {
        var directory = NewDirectory(); var state = InitialState();
        try
        {
            var sourceHash = new string('a', 64); var destinationHash = new string('b', 64);
            var registry = new DepotRegistrySnapshot { RegistryVersion = "registry", Depots =
            [new DepotRecord { DepotId = "source", MembershipRevision = 1, MembershipHash = sourceHash },
             new DepotRecord { DepotId = "destination", MembershipRevision = 1, MembershipHash = destinationHash }] };
            registry.RegistryHash = OperationIdentity.ComputeDepotRegistryHash(registry.Depots);
            state = AcceptedStateCodec.SyncDepots(state, OperationIdentity.Create(state.WorldId, 1, "sync"), "sync", 1,
                OperationIdentity.DepotRegistryPayloadHash(registry), state.Revision, AcceptedStateCodec.ComputeHash(state), registry, 1).State;
            var route = new RouteVersionRecord { RouteId = "route", Version = 1, SourceDepotId = "source", SourceMembershipRevision = 1,
                SourceMembershipHash = sourceHash, DestinationDepotId = "destination", DestinationMembershipRevision = 1,
                DestinationMembershipHash = destinationHash, TravelDurationSeconds = 50, Provenance = "transient arrival fixture",
                Resources = [new ResourceAmount { ResourceName = "Fuel", AmountMicroUnits = 1_000_000 }] };
            state = AcceptedStateCodec.UpsertRoute(state, OperationIdentity.Create(state.WorldId, 2, "route"), "route", 2,
                OperationIdentity.RouteVersionPayloadHash(route), state.Revision, AcceptedStateCodec.ComputeHash(state), route, 2).State;
            var sourceCapability = WritableCapability("source", 1, sourceHash, 7, [7]);
            var destinationCapability = WritableCapability("destination", 1, destinationHash, 9, [9]);
            var shipment = new ActiveShipmentRecord { ShipmentId = "due-cargo", RouteId = route.RouteId, RouteVersion = route.Version,
                SourceDepotId = route.SourceDepotId, DestinationDepotId = route.DestinationDepotId,
                RemainingResources = [new ResourceAmount { ResourceName = "Fuel", AmountMicroUnits = 1_000_000 }] };
            var debit = new PhysicalEffectIntent { EffectKind = "dispatchDebit", Capability = sourceCapability, MembershipRevision = 1,
                MemberPersistentIds = [7], Deltas = [new PhysicalEffectResourceDelta { MemberPersistentId = 7, ResourceName = "Fuel", DeltaMicroUnits = -1_000_000 }] };
            var dispatchHash = OperationIdentity.DispatchPayloadHash(shipment, debit);
            var dispatchTransition = AcceptedStateCodec.Dispatch(state, OperationIdentity.Create(state.WorldId, 3, "dispatch"), "dispatch", 3,
                dispatchHash, state.Revision, AcceptedStateCodec.ComputeHash(state), shipment, debit, 3);
            Assert.True(dispatchTransition.Outcome == "accepted", dispatchTransition.Reason);
            state = dispatchTransition.State;
            var secondShipment = new ActiveShipmentRecord { ShipmentId = "due-cargo-2", RouteId = route.RouteId, RouteVersion = route.Version,
                SourceDepotId = route.SourceDepotId, DestinationDepotId = route.DestinationDepotId,
                RemainingResources = [new ResourceAmount { ResourceName = "Fuel", AmountMicroUnits = 1_000_000 }] };
            var secondHash = OperationIdentity.DispatchPayloadHash(secondShipment, debit);
            var secondDispatch = AcceptedStateCodec.Dispatch(state, OperationIdentity.Create(state.WorldId, 4, "dispatch-2"), "dispatch-2", 4,
                secondHash, state.Revision, AcceptedStateCodec.ComputeHash(state), secondShipment, debit, 4);
            Assert.True(secondDispatch.Outcome == "accepted", secondDispatch.Reason);
            state = secondDispatch.State;
            Assert.Equal(2, state.ActiveShipments.Length);
            Assert.Equal(2, state.SchemaVersion);

            var clock = FreshClock(state.WorldId);
            using var host = new RecoveryCoordinator(directory, new AlwaysLiveVerifier());
            var attach = AttachFor(state); attach.Capabilities = ["dispatch", "arrival"]; attach.RegistrySnapshot = registry;
            attach.InventoryEndpoints = [sourceCapability, destinationCapability];
            Assert.IsType<EffectIdle>(host.Attach(attach, clock));
            var full = PollFor(state); full.InventoryObservations = [Observation(registry, destinationCapability, state.WorldId, 1_000_000, 1_000_000, 1_000_000, 1_000_000)];
            var held = Assert.IsType<EffectIdle>(host.Poll(full, clock));
            Assert.Contains("no capacity", held.Reason, StringComparison.OrdinalIgnoreCase);
            var projection = host.GetAcceptedState(new GetAcceptedState { ProtocolVersion = 1, MessageType = "getAcceptedState", WorldId = state.WorldId, RunId = "run-one" });
            Assert.Equal(new[] { "due-cargo", "due-cargo-2" }, projection.TransientShipmentHolds.Select(x => x.ShipmentId));
            Assert.All(projection.TransientShipmentHolds, x => Assert.Contains("no capacity", x.Reason, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(AcceptedStateCodec.ComputeHash(state), projection.StateHash);
            Assert.All(AcceptedStateCodec.ReadCapsule(projection.AcceptedCapsule!).ActiveShipments, x => Assert.Equal(1_000_000, x.RemainingResources[0].AmountMicroUnits));

            var loadedDestination = WritableCapability("destination", 1, destinationHash, 9, [9]);
            loadedDestination.WriteSupported = false;
            loadedDestination.HoldReason = "Loaded destination is unsupported for physical delivery.";
            var repeatedAttach = AttachFor(state); repeatedAttach.Capabilities = ["dispatch", "arrival"]; repeatedAttach.RegistrySnapshot = registry;
            repeatedAttach.InventoryEndpoints = [sourceCapability, loadedDestination];
            Assert.IsType<EffectIdle>(host.Attach(repeatedAttach, clock));
            projection = host.GetAcceptedState(new GetAcceptedState { ProtocolVersion = 1, MessageType = "getAcceptedState", WorldId = state.WorldId, RunId = "run-one" });
            Assert.Equal(2, projection.TransientShipmentHolds.Length); // Same-load reattach does not erase the last known holds.
            var loadedPoll = PollFor(state); loadedPoll.InventoryObservations = [Observation(registry, loadedDestination, state.WorldId, 0, 1_000_000, 0, 1_000_000, 2)];
            Assert.IsType<EffectIdle>(host.Poll(loadedPoll, clock));
            projection = host.GetAcceptedState(new GetAcceptedState { ProtocolVersion = 1, MessageType = "getAcceptedState", WorldId = state.WorldId, RunId = "run-one" });
            Assert.All(projection.TransientShipmentHolds, x => Assert.Contains("Loaded destination", x.Reason, StringComparison.Ordinal));

            var writableAgain = AttachFor(state); writableAgain.Capabilities = ["dispatch", "arrival"]; writableAgain.RegistrySnapshot = registry;
            writableAgain.InventoryEndpoints = [sourceCapability, destinationCapability];
            Assert.IsType<EffectIdle>(host.Attach(writableAgain, clock));
            var available = PollFor(state); available.InventoryObservations = [Observation(registry, destinationCapability, state.WorldId, 0, 1_000_000, 0, 1_000_000, 2)];
            Assert.IsType<EffectProposal>(host.Poll(available, clock));
            projection = host.GetAcceptedState(new GetAcceptedState { ProtocolVersion = 1, MessageType = "getAcceptedState", WorldId = state.WorldId, RunId = "run-one" });
            Assert.Empty(projection.TransientShipmentHolds);
            Assert.Equal(AcceptedStateCodec.ComputeHash(state), projection.StateHash);
            Assert.All(AcceptedStateCodec.ReadCapsule(projection.AcceptedCapsule!).ActiveShipments, x => Assert.Equal(1_000_000, x.RemainingResources[0].AmountMicroUnits));

            var nextWorld = InitialState();
            var nextClock = FreshClock(nextWorld.WorldId, session: Session, epoch: Guid.NewGuid());
            var nextAttach = AttachFor(nextWorld, epoch: nextClock.CurrentSample!.LoadEpoch);
            Assert.IsType<EffectIdle>(host.Attach(nextAttach, nextClock));
            var nextProjection = host.GetAcceptedState(new GetAcceptedState { ProtocolVersion = 1, MessageType = "getAcceptedState", WorldId = nextWorld.WorldId, RunId = "run-one" });
            Assert.Empty(nextProjection.TransientShipmentHolds);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void DeliveryReadinessRequiresMatchingBridgeEffectsEndpointsAndFreshSelectedStock()
    {
        var directory = NewDirectory(); var state = InitialState(); var clock = FreshClock(state.WorldId);
        try
        {
            var sourceHash = new string('a', 64); var destinationHash = new string('b', 64);
            var registry = new DepotRegistrySnapshot { RegistryVersion = "registry", Depots =
            [new DepotRecord { DepotId = "source", MembershipRevision = 1, MembershipHash = sourceHash },
             new DepotRecord { DepotId = "destination", MembershipRevision = 1, MembershipHash = destinationHash }] };
            registry.RegistryHash = OperationIdentity.ComputeDepotRegistryHash(registry.Depots);
            state = AcceptedStateCodec.SyncDepots(state, OperationIdentity.Create(state.WorldId, 1, "sync"), "sync", 1,
                OperationIdentity.DepotRegistryPayloadHash(registry), state.Revision, AcceptedStateCodec.ComputeHash(state), registry, 10).State;
            var route = new RouteVersionRecord { RouteId = "saved-route", Version = 1, SourceDepotId = "source",
                SourceMembershipRevision = 1, SourceMembershipHash = sourceHash, DestinationDepotId = "destination",
                DestinationMembershipRevision = 1, DestinationMembershipHash = destinationHash, TravelDurationSeconds = 120,
                Provenance = "readiness fixture", Resources = [new ResourceAmount { ResourceName = "Fuel", AmountMicroUnits = 100_000_000 }] };
            state = AcceptedStateCodec.UpsertRoute(state, OperationIdentity.Create(state.WorldId, 2, "route"), "route", 2,
                OperationIdentity.RouteVersionPayloadHash(route), state.Revision, AcceptedStateCodec.ComputeHash(state), route, 11).State;
            var source = WritableCapability("source", 1, sourceHash, 7, [7]);
            var destination = WritableCapability("destination", 1, destinationHash, 9, [9]);
            using var host = new RecoveryCoordinator(directory, new AlwaysLiveVerifier());
            var attach = AttachFor(state); attach.RegistrySnapshot = registry; attach.InventoryEndpoints = [source, destination];
            Assert.IsType<EffectIdle>(host.Attach(attach, clock));
            var query = new GetDeliveryReadiness { ProtocolVersion = 1, MessageType = "getDeliveryReadiness",
                WorldId = state.WorldId, RunId = "run-one", RouteId = route.RouteId, RouteVersion = route.Version };
            Assert.Equal("held", host.GetDeliveryReadiness(query).Status);
            Assert.Contains("dispatch and arrival", host.GetDeliveryReadiness(query).Reason);

            attach.Capabilities = ["dispatch", "arrival"];
            Assert.IsType<EffectIdle>(host.Attach(attach, clock));
            Assert.Contains("Fresh authoritative source", host.GetDeliveryReadiness(query).Reason);
            var poll = PollFor(state); poll.InventoryObservations =
            [Observation(registry, source, state.WorldId, 1_000_000_000, 2_000_000_000, 1_000_000_000, 2_000_000_000),
             Observation(registry, destination, state.WorldId, 0, 50_000_000, 0, 50_000_000)];
            Assert.IsType<EffectIdle>(host.Poll(poll, clock));
            Assert.Equal("held", host.GetDeliveryReadiness(query).Status);
            poll.InventoryObservations =
            [Observation(registry, source, state.WorldId, 1_000_000_000, 2_000_000_000, 1_000_000_000, 2_000_000_000, 2),
             Observation(registry, destination, state.WorldId, 0, 1_000_000_000, 0, 1_000_000_000, 2)];
            Assert.IsType<EffectIdle>(host.Poll(poll, clock));
            var ready = host.GetDeliveryReadiness(query);
            Assert.Equal("ready", ready.Status);
            Assert.Equal(state.AcceptedSequence, ready.AcceptedSequence);
            Assert.Equal(source.ProviderId, ready.SourceProviderId);
            Assert.Equal(destination.ProviderId, ready.DestinationProviderId);
            Assert.Equal("held", host.GetDeliveryReadiness(new GetDeliveryReadiness { ProtocolVersion = 1,
                MessageType = "getDeliveryReadiness", WorldId = state.WorldId, RunId = "different-run",
                RouteId = route.RouteId, RouteVersion = route.Version }).Status);
        }
        finally { Directory.Delete(directory, true); }
    }

    static PhysicalSuccessWitness SuccessWitness(PhysicalEffectIntent intent, double beforeAmount) => new()
    {
        ProviderId = intent.Capability.ProviderId, ProviderVersion = intent.Capability.ProviderVersion,
        Rows = intent.Deltas.OrderBy(x => x.MemberPersistentId).ThenBy(x => x.ResourceName, StringComparer.Ordinal).Select(x => new PhysicalSuccessWitnessRow { MemberPersistentId = x.MemberPersistentId, ResourceName = x.ResourceName, BeforeAmount = beforeAmount, IntendedAfterAmount = beforeAmount + x.DeltaMicroUnits / 1_000_000d, ObservedAfterAmount = beforeAmount + x.DeltaMicroUnits / 1_000_000d }).ToArray()
    };
    static InventoryCapability WritableCapability(string depot, long revision, string membershipHash, uint anchor, uint[] members) => new()
    {
        DepotId = depot, MembershipRevision = revision, MembershipHash = membershipHash, AnchorPersistentId = anchor, MemberSetHash = OperationIdentity.ComputeMemberSetHash(anchor, members), ProviderId = "synthetic", ProviderVersion = "1", Scene = "Flight", ObservationAvailable = true, ReadSupported = true, WriteSupported = true, SynchronousRollbackSupported = true, PersistenceSyncSupported = true
    };
    static StockObservation Observation(DepotRegistrySnapshot registry, InventoryCapability capability, string worldId, long amount, long capacity, long memberAmount, long memberCapacity, long observationRevision = 1) => new()
    {
        DepotId = capability.DepotId, SessionId = Session, LoadEpoch = Epoch, WorldId = worldId, RegistryHash = registry.RegistryHash, MembershipRevision = capability.MembershipRevision, MembershipHash = capability.MembershipHash, ObservationRevision = observationRevision, ObservedUt = 1, AgeSeconds = 0, Available = true, MicroUnitProjectionSafe = true,
        Resources = [new StockAmount { ResourceName = "Fuel", AmountMicroUnits = amount, CapacityMicroUnits = capacity, DebitAllowed = true }],
        MemberStocks = [new MemberStockObservation { MemberPersistentId = capability.AnchorPersistentId, Resources = [new StockAmount { ResourceName = "Fuel", AmountMicroUnits = memberAmount, CapacityMicroUnits = memberCapacity, DebitAllowed = true }] }]
    };

    [Fact]
    public void PreparedCommandReoffersOnlyToSameOwnerAndDifferentLiveGameIsFenced()
    {
        var directory = NewDirectory(); var state = InitialState(); var verifier = new AlwaysLiveVerifier();
        try
        {
            using (var firstHost = new RecoveryCoordinator(directory, verifier, enableDevCounter: true))
            {
                Assert.IsType<EffectIdle>(firstHost.Attach(AttachFor(state), FreshClock(state.WorldId)));
                Assert.Equal("pending", firstHost.Submit(Command("persist-me", state.WorldId)).Status);
            }
            using (var restartedHost = new RecoveryCoordinator(directory, verifier, enableDevCounter: true))
            {
                Assert.IsType<EffectIdle>(restartedHost.Attach(AttachFor(state), FreshClock(state.WorldId)));
                var proposal = Assert.IsType<EffectProposal>(restartedHost.Poll(PollFor(state), FreshClock(state.WorldId)));
                Assert.Equal("persist-me", proposal.ClientRequestId);
                var otherSession = Guid.NewGuid(); var otherEpoch = Guid.NewGuid();
                var otherState = FreshClock(state.WorldId, "other-run", otherSession, otherEpoch);
                var rejected = Assert.IsType<EffectNeedAttach>(restartedHost.Attach(AttachFor(state, otherSession, otherEpoch, run: "other-run", pid: Pid + 1), otherState));
                Assert.Contains("Ambiguous writable game owner", rejected.Reason);
                var stillOwned = Assert.IsType<EffectProposal>(restartedHost.Poll(PollFor(state), FreshClock(state.WorldId)));
                Assert.Equal("persist-me", stillOwned.ClientRequestId);
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void LostAckAfterKspApplyIsReconciledFromCapsuleAfterHostRestart()
    {
        var directory = NewDirectory(); var state = InitialState(); var clock = FreshClock(state.WorldId); var verifier = new AlwaysLiveVerifier();
        try
        {
            string operationId; AcceptedState applied;
            using (var first = new RecoveryCoordinator(directory, verifier, enableDevCounter: true))
            {
                Assert.IsType<EffectIdle>(first.Attach(AttachFor(state), clock));
                var submission = first.Submit(Command("ack-lost", state.WorldId)); operationId = submission.OperationId!;
                var proposal = Assert.IsType<EffectProposal>(first.Poll(PollFor(state), clock));
                applied = AcceptedStateCodec.Increment(state, state.WorldId, proposal.OperationId, proposal.ClientRequestId, proposal.CommandSequence, proposal.PayloadHash, proposal.CounterDelta, 8.5).State;
                // Fault point: KSP swapped its accepted capsule but the receipt/ack did not settle in Host.
            }
            using (var restarted = new RecoveryCoordinator(directory, verifier, enableDevCounter: true))
            {
                Assert.IsType<EffectIdle>(restarted.Attach(AttachFor(applied), clock));
                var replay = restarted.Submit(Command("ack-lost", state.WorldId)); Assert.Equal("accepted", replay.Status); Assert.Equal(operationId, replay.OperationId);
                Assert.Equal(1, applied.Counter); Assert.Equal(1, applied.AcceptedSequence);
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void OlderSelectedSaveHoldsFuturePreparedWorkAndCopiedSaveAttachesAsSeparateContext()
    {
        var directory = NewDirectory(); var state = InitialState(); var clock = FreshClock(state.WorldId);
        var nextEpoch = Guid.NewGuid();
        try
        {
            using var host = new RecoveryCoordinator(directory, new AlwaysLiveVerifier(), enableDevCounter: true);
            Assert.IsType<EffectIdle>(host.Attach(AttachFor(state), clock));
            var first = host.Submit(Command("future-from-newer-save", state.WorldId));
            Assert.Equal("pending", first.Status);

            // Selecting an older quicksave restores the baseline capsule under a new run/epoch.
            clock.Accept(new ClockSample(1, "clockSample", 2, Session, nextEpoch, Install, Save, "Synthetic", 50, true, "Flight", false, "Year 1", 1, WorldId: state.WorldId, RunId: "quickload-run"));
            var older = AttachFor(state, epoch: nextEpoch, run: "quickload-run");
            Assert.IsType<EffectIdle>(host.Attach(older, clock));
            var poll = PollFor(state); poll.LoadEpoch = nextEpoch; poll.RunId = "quickload-run";
            Assert.IsType<EffectIdle>(host.Poll(poll, clock));
            var sameRequest = Command("future-from-newer-save", state.WorldId); sameRequest.RunId = "quickload-run";
            Assert.Equal("rejected", host.Submit(sameRequest).Status);

            // A copied save shares world/capsule identity, but has a distinct save-folder context.
            var copiedEpoch = Guid.NewGuid(); const string copiedSave = "Sandbox-copy";
            clock.Accept(new ClockSample(1, "clockSample", 3, Session, copiedEpoch, Install, copiedSave, "Synthetic copy", 51, true, "Flight", false, "Year 1", 1, WorldId: state.WorldId, RunId: "copy-run"));
            var copied = AttachFor(state, epoch: copiedEpoch, run: "copy-run"); copied.SaveFolder = copiedSave;
            Assert.IsType<EffectIdle>(host.Attach(copied, clock));
            var copiedPoll = PollFor(state); copiedPoll.LoadEpoch = copiedEpoch; copiedPoll.SaveFolder = copiedSave; copiedPoll.RunId = "copy-run";
            Assert.IsType<EffectIdle>(host.Poll(copiedPoll, clock));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void ReceiptWithValidCapsuleButWrongDeterministicProjectionDoesNotSettlePreparedOperation()
    {
        var directory = NewDirectory(); var state = InitialState(); var clock = FreshClock(state.WorldId);
        try
        {
            using var host = new RecoveryCoordinator(directory, new AlwaysLiveVerifier(), enableDevCounter: true);
            Assert.IsType<EffectIdle>(host.Attach(AttachFor(state), clock));
            Assert.Equal("pending", host.Submit(Command("invalid-result", state.WorldId)).Status);
            var proposal = Assert.IsType<EffectProposal>(host.Poll(PollFor(state), clock));
            var candidate = AcceptedStateCodec.Increment(state, state.WorldId, proposal.OperationId, proposal.ClientRequestId, proposal.CommandSequence, proposal.PayloadHash, proposal.CounterDelta, 12).State;
            candidate.Counter += 10; // Still a structurally valid capsule, but not the reducer result.
            var tampered = ReceiptFor(proposal, candidate, 12);

            var needAttach = Assert.IsType<EffectNeedAttach>(host.Receipt(tampered, clock));
            Assert.Contains("verification failed", needAttach.Reason, StringComparison.OrdinalIgnoreCase);
            var stillPrepared = Assert.IsType<EffectProposal>(host.Poll(PollFor(state), clock));
            Assert.Equal(proposal.OperationId, stillPrepared.OperationId);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void DatabaseLossRebuildsOperationalStateFromCapsuleAndReportsHistoryUnavailable()
    {
        var directory = NewDirectory(); var lostDirectory = NewDirectory(); var state = InitialState(); var requestId = "persisted-in-world";
        var operationId = OperationIdentity.Create(state.WorldId, 1, requestId);
        state = AcceptedStateCodec.Increment(state, state.WorldId, operationId, requestId, 1, OperationIdentity.CounterIncrementPayloadHash(1), 1, 25).State;
        try
        {
            using var rebuilt = new RecoveryCoordinator(lostDirectory, new AlwaysLiveVerifier(), enableDevCounter: true);
            var attach = Assert.IsType<EffectIdle>(rebuilt.Attach(AttachFor(state), FreshClock(state.WorldId)));
            Assert.Contains("history is unavailable", attach.Reason, StringComparison.OrdinalIgnoreCase);
            var newCommand = rebuilt.Submit(Command("after-db-loss", state.WorldId)); Assert.Equal("pending", newCommand.Status);
            Assert.NotEqual(operationId, newCommand.OperationId);
        }
        finally { Directory.Delete(directory, true); Directory.Delete(lostDirectory, true); }
    }

    [Fact]
    public void BackwardUtFencesEffectPollUntilFullCapsuleReattach()
    {
        var directory = NewDirectory(); var accepted = InitialState(); var clock = FreshClock(accepted.WorldId);
        try
        {
            using var host = new RecoveryCoordinator(directory, new AlwaysLiveVerifier(), enableDevCounter: true);
            Assert.IsType<EffectIdle>(host.Attach(AttachFor(accepted), clock));
            Assert.Equal("pending", host.Submit(Command("fenced", accepted.WorldId)).Status);
            clock.Accept(new ClockSample(1, "clockSample", 2, Session, Epoch, Install, Save, "Synthetic", 50, true, "Flight", false, "Year 1", 1, WorldId: accepted.WorldId, RunId: "run-one"));
            var needsAttach = Assert.IsType<EffectNeedAttach>(host.Poll(PollFor(accepted), clock));
            Assert.Contains("fresh capsule", needsAttach.Reason, StringComparison.OrdinalIgnoreCase);
            Assert.IsType<EffectIdle>(host.Attach(AttachFor(accepted), clock));
            Assert.IsType<EffectProposal>(host.Poll(PollFor(accepted), clock));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void HostPreparesNormalAcceptedCompactionAtReceiptCapacityThenRetriesSameRequest()
    {
        var directory = NewDirectory(); var state = InitialState();
        for (var sequence = 1; sequence <= AcceptedStateCodec.MaxReceipts; sequence++)
        {
            var requestId = "seed-" + sequence; var operationId = OperationIdentity.Create(state.WorldId, sequence, requestId);
            state = AcceptedStateCodec.Increment(state, state.WorldId, operationId, requestId, sequence, OperationIdentity.CounterIncrementPayloadHash(1), 1, sequence).State;
        }
        var clock = FreshClock(state.WorldId);
        try
        {
            using var host = new RecoveryCoordinator(directory, new AlwaysLiveVerifier(), enableDevCounter: true);
            Assert.IsType<EffectIdle>(host.Attach(AttachFor(state), clock));
            var held = host.Submit(Command("after-capacity", state.WorldId)); Assert.Equal("rejected", held.Status);
            var compact = Assert.IsType<EffectProposal>(host.Poll(PollFor(state), clock)); Assert.Equal("compact", compact.OperationKind); Assert.Equal(16, compact.TargetCompactionWatermark); Assert.Equal(0, compact.CounterDelta);
            var compacted = AcceptedStateCodec.Compact(state, compact.OperationId, compact.ClientRequestId, compact.CommandSequence, compact.PayloadHash, compact.ExpectedRevision, compact.ExpectedStateHash, compact.TargetCompactionWatermark, 33);
            var compactReceipt = ReceiptFor(compact, compacted, 33); compactReceipt.OperationKind = "compact"; compactReceipt.TargetCompactionWatermark = 16;
            Assert.IsType<EffectIdle>(host.Receipt(compactReceipt, clock));
            var retry = host.Submit(Command("after-capacity", state.WorldId)); Assert.Equal("pending", retry.Status);
            var next = Assert.IsType<EffectProposal>(host.Poll(PollFor(compacted), clock)); Assert.Equal("counterIncrement", next.OperationKind); Assert.Equal(34, next.CommandSequence);
            Assert.Equal(16, compacted.CompactionWatermark); Assert.Equal(17, compacted.Receipts.Length); Assert.Equal(32, compacted.Counter);
            Assert.Equal("alreadySettledCompacted", AcceptedStateCodec.Increment(compacted, state.WorldId, OperationIdentity.Create(state.WorldId, 1, "seed-1"), "seed-1", 1, OperationIdentity.CounterIncrementPayloadHash(1), 1, 1).Outcome);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void NewRunBranchArchivesPreparedRegistrySyncAndRejectsDelayedOldMessages()
    {
        var directory = NewDirectory(); var state = InitialState(); var clock = FreshClock(state.WorldId, "old-run");
        var oldRegistry = new DepotRegistrySnapshot { RegistryVersion = "empty-v1", Depots = Array.Empty<DepotRecord>() };
        oldRegistry.RegistryHash = OperationIdentity.ComputeDepotRegistryHash(oldRegistry.Depots);
        var sourceHash = new string('a', 64); var destinationHash = new string('b', 64);
        var newRegistry = new DepotRegistrySnapshot { RegistryVersion = "flight-v2", Depots = [new DepotRecord { DepotId = "source", MembershipRevision = 1, MembershipHash = sourceHash }, new DepotRecord { DepotId = "destination", MembershipRevision = 1, MembershipHash = destinationHash }] };
        newRegistry.RegistryHash = OperationIdentity.ComputeDepotRegistryHash(newRegistry.Depots);
        try
        {
            using var host = new RecoveryCoordinator(directory, new AlwaysLiveVerifier());
            var oldAttach = AttachFor(state, run: "old-run"); oldAttach.RegistrySnapshot = oldRegistry;
            Assert.IsType<EffectIdle>(host.Attach(oldAttach, clock));
            var oldProposal = Assert.IsType<EffectProposal>(host.Poll(PollFor(state, "old-run"), clock));
            Assert.Equal("syncDepots", oldProposal.OperationKind);

            // A same-session, same-epoch run transition at unchanged UT still changes the context generation.
            clock.Accept(new ClockSample(1, "clockSample", 2, Session, Epoch, Install, Save, "Synthetic", 100, true, "Flight", false, "Year 1", 1, WorldId: state.WorldId, RunId: "new-run"));
            Assert.IsType<EffectNeedAttach>(host.Attach(oldAttach, clock)); // delayed old attach, before fresh attach

            var nextAttach = AttachFor(state, run: "new-run"); nextAttach.RegistrySnapshot = newRegistry; nextAttach.InventoryEndpoints = [new InventoryCapability { DepotId = "source", MembershipRevision = 1 }, new InventoryCapability { DepotId = "destination", MembershipRevision = 1 }];
            Assert.IsType<EffectIdle>(host.Attach(nextAttach, clock)); // selected seq-0 capsule is authoritative; old sync is archived
            var accepted = host.GetAcceptedState(new GetAcceptedState { ProtocolVersion = 1, MessageType = "getAcceptedState", WorldId = state.WorldId, RunId = "new-run" });
            Assert.Equal("available", accepted.Status); Assert.Equal(0, accepted.AcceptedSequence);
            Assert.Equal("unavailable", host.GetAcceptedState(new GetAcceptedState { ProtocolVersion = 1, MessageType = "getAcceptedState", WorldId = state.WorldId, RunId = "old-run" }).Status);

            var freshSync = Assert.IsType<EffectProposal>(host.Poll(PollFor(state, "new-run"), clock));
            Assert.Equal("syncDepots", freshSync.OperationKind); Assert.Equal(oldProposal.CommandSequence, freshSync.CommandSequence);
            Assert.NotEqual(oldProposal.OperationId, freshSync.OperationId); Assert.NotEqual(oldProposal.ClientRequestId, freshSync.ClientRequestId);

            Assert.IsType<EffectNeedAttach>(host.Attach(oldAttach, clock)); // delayed old attach, after fresh attach
            var oldApplied = AcceptedStateCodec.SyncDepots(state, oldProposal.OperationId, oldProposal.ClientRequestId, oldProposal.CommandSequence, oldProposal.PayloadHash, oldProposal.ExpectedRevision, oldProposal.ExpectedStateHash, oldRegistry, 100).State;
            Assert.IsType<EffectNeedAttach>(host.Receipt(ReceiptFor(oldProposal, oldApplied, 100), clock)); // delayed old receipt
            var stillFresh = Assert.IsType<EffectProposal>(host.Poll(PollFor(state, "new-run"), clock));
            Assert.Equal(freshSync.OperationId, stillFresh.OperationId);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void PhysicalDebitAllocationSkipsFlowLockedAndUnknownRowsWhileCreditCanUseThem()
    {
        const uint anchor = 1;
        uint[] members = [1, 2];
        var capability = new InventoryCapability
        {
            DepotId = "source", MembershipRevision = 1, MembershipHash = new string('a', 64),
            AnchorPersistentId = anchor, MemberSetHash = OperationIdentity.ComputeMemberSetHash(anchor, members),
            ProviderId = "test", ProviderVersion = "1", Scene = "Flight", ObservationAvailable = true,
            ReadSupported = true, WriteSupported = true, SynchronousRollbackSupported = true, PersistenceSyncSupported = true
        };
        var observation = new StockObservation
        {
            MemberStocks = [
                new MemberStockObservation { MemberPersistentId = 1, Resources = [new StockAmount { ResourceName = "Fuel", AmountMicroUnits = 8_000_000, CapacityMicroUnits = 10_000_000, DebitAllowed = false }] },
                new MemberStockObservation { MemberPersistentId = 2, Resources = [new StockAmount { ResourceName = "Fuel", AmountMicroUnits = 2_000_000, CapacityMicroUnits = 10_000_000, DebitAllowed = true }] }
            ]
        };
        var debit = RecoveryCoordinator.BuildIntent("dispatchDebit", capability, observation, [new ResourceAmount { ResourceName = "Fuel", AmountMicroUnits = 1_500_000 }], true, out var debitHold);
        Assert.NotNull(debit);
        Assert.Equal(new uint[] { 2 }, debit!.Deltas.Select(x => x.MemberPersistentId).ToArray());
        Assert.Equal(-1_500_000, debit.Deltas.Single().DeltaMicroUnits);

        var credit = RecoveryCoordinator.BuildIntent("arrivalCredit", capability, observation, [new ResourceAmount { ResourceName = "Fuel", AmountMicroUnits = 1_500_000 }], false, out _);
        Assert.NotNull(credit);
        Assert.Equal(1u, credit!.Deltas.Single().MemberPersistentId); // flow lock does not prevent destination credit
        Assert.Equal(1_500_000, credit.Deltas.Single().DeltaMicroUnits);

        observation.MemberStocks[1].Resources[0].DebitAllowed = null; // legacy metadata is not debit proof
        var legacy = RecoveryCoordinator.BuildIntent("dispatchDebit", capability, observation, [new ResourceAmount { ResourceName = "Fuel", AmountMicroUnits = 2_500_000 }], true, out debitHold);
        Assert.Null(legacy);
        Assert.NotEmpty(debitHold);
    }

    [Fact]
    public void DebitEligibilityWireMetadataIsBackwardCompatibleAndFailsClosedWhenMissing()
    {
        var current = System.Text.Json.JsonSerializer.Deserialize<StockObservation>(
            "{\"depotId\":\"source\",\"resources\":[{\"resourceName\":\"Fuel\",\"amountMicroUnits\":1000000,\"capacityMicroUnits\":2000000,\"debitAllowed\":false}],\"memberStocks\":[{\"memberPersistentId\":1,\"resources\":[{\"resourceName\":\"Fuel\",\"amountMicroUnits\":1000000,\"capacityMicroUnits\":2000000,\"debitAllowed\":false}]}]}", ClockProtocol.JsonOptions);
        Assert.False(current!.MemberStocks[0].Resources[0].DebitAllowed);
        var legacy = System.Text.Json.JsonSerializer.Deserialize<StockObservation>(
            "{\"depotId\":\"source\",\"resources\":[{\"resourceName\":\"Fuel\",\"amountMicroUnits\":1000000,\"capacityMicroUnits\":2000000}],\"memberStocks\":[{\"memberPersistentId\":1,\"resources\":[{\"resourceName\":\"Fuel\",\"amountMicroUnits\":1000000,\"capacityMicroUnits\":2000000}]}]}", ClockProtocol.JsonOptions);
        Assert.Null(legacy!.MemberStocks[0].Resources[0].DebitAllowed);
    }

    [Fact]
    public void RuleEditKeepsIdentityScheduleAndPausedStateButRejectsStaleRevision()
    {
        var (state, route) = RuleLifecycleState();
        var original = new DeliveryRuleRecord { RuleId = "fuel-order", Revision = 1, Kind = "repeat", RouteId = route.RouteId, RouteVersion = 1, Enabled = false, IntervalSeconds = 60, NextDueUt = 900, WaitingRequest = true, WaitingScheduledUt = 840, WaitingCoalescedSlots = 2 };
        state = AcceptedStateCodec.UpsertRule(state, OperationIdentity.Create(state.WorldId, 3, "create"), "create", 3, OperationIdentity.RulePayloadHash(original), state.Revision, AcceptedStateCodec.ComputeHash(state), original, 100).State;
        var edit = new DeliveryRuleRecord { RuleId = "fuel-order", Revision = 2, Kind = "repeat", RouteId = route.RouteId, RouteVersion = 1, Enabled = false, IntervalSeconds = 60, NextDueUt = 160 };
        var result = AcceptedStateCodec.UpsertRule(state, OperationIdentity.Create(state.WorldId, 4, "edit"), "edit", 4, OperationIdentity.RulePayloadHash(edit), state.Revision, AcceptedStateCodec.ComputeHash(state), edit, 110);
        Assert.Equal("accepted", result.Outcome);
        var saved = Assert.Single(result.State.DeliveryRules);
        Assert.Equal("fuel-order", saved.RuleId); Assert.Equal(2, saved.Revision); Assert.False(saved.Enabled);
        Assert.Equal(900, saved.NextDueUt); Assert.True(saved.WaitingRequest); Assert.Equal(840, saved.WaitingScheduledUt); Assert.Equal(2, saved.WaitingCoalescedSlots);
        var stale = AcceptedStateCodec.UpsertRule(result.State, OperationIdentity.Create(state.WorldId, 5, "stale"), "stale", 5, OperationIdentity.RulePayloadHash(edit), result.State.Revision, AcceptedStateCodec.ComputeHash(result.State), edit, 120);
        Assert.Equal("rejected", stale.Outcome);
        var changedInterval = new DeliveryRuleRecord { RuleId = "fuel-order", Revision = 3, Kind = "repeat", RouteId = route.RouteId, RouteVersion = 1, Enabled = false, IntervalSeconds = 120, NextDueUt = 1_200, WaitingRequest = true, WaitingScheduledUt = 840, WaitingCoalescedSlots = 2 };
        var changed = AcceptedStateCodec.UpsertRule(result.State, OperationIdentity.Create(state.WorldId, 5, "retime"), "retime", 5, OperationIdentity.RulePayloadHash(changedInterval), result.State.Revision, AcceptedStateCodec.ComputeHash(result.State), changedInterval, 120);
        Assert.Equal("accepted", changed.Outcome);
        var retimed = Assert.Single(changed.State.DeliveryRules);
        Assert.Equal(1_200, retimed.NextDueUt); Assert.False(retimed.Enabled); Assert.False(retimed.WaitingRequest);
        Assert.Equal(0, retimed.WaitingScheduledUt); Assert.Equal(0, retimed.WaitingCoalescedSlots);
    }

    [Fact]
    public void RuleCancelIsExactAndDoesNotRemoveInTransitCargo()
    {
        var directory = NewDirectory();
        try
        {
            var (state, route) = RuleLifecycleState();
            var first = new DeliveryRuleRecord { RuleId = "fuel-order::stock::Fuel", Revision = 1, Kind = "keepStock", RouteId = route.RouteId, RouteVersion = 1, Enabled = true, ResourceName = "Fuel", LowTriggerMicroUnits = 1_000_000, TargetMicroUnits = 2_000_000, BatchSizeMicroUnits = 2_000_000 };
            var second = new DeliveryRuleRecord { RuleId = "fuel-order::stock::Oxidizer", Revision = 1, Kind = "repeat", RouteId = route.RouteId, RouteVersion = 1, Enabled = true, NextDueUt = 900, IntervalSeconds = 60 };
            state = AcceptedStateCodec.UpsertRule(state, OperationIdentity.Create(state.WorldId, 3, "first"), "first", 3, OperationIdentity.RulePayloadHash(first), state.Revision, AcceptedStateCodec.ComputeHash(state), first, 100).State;
            state = AcceptedStateCodec.UpsertRule(state, OperationIdentity.Create(state.WorldId, 4, "second"), "second", 4, OperationIdentity.RulePayloadHash(second), state.Revision, AcceptedStateCodec.ComputeHash(state), second, 100).State;
            state.ActiveShipments = [new ActiveShipmentRecord { ShipmentId = "already-in-transit", RouteId = route.RouteId, RouteVersion = 1, SourceDepotId = route.SourceDepotId, DestinationDepotId = route.DestinationDepotId, DepartureUt = 100, DueUt = 220, RemainingResources = [new ResourceAmount { ResourceName = "Fuel", AmountMicroUnits = 1_000_000 }] }];
            AcceptedStateCodec.Validate(state);
            var clock = FreshClock(state.WorldId);
            using var host = new RecoveryCoordinator(directory, new AlwaysLiveVerifier());
            Assert.IsType<EffectIdle>(host.Attach(AttachFor(state), clock));
            var request = new SubmitCommand { ProtocolVersion = 1, MessageType = "submitCommand", ClientRequestId = "cancel-first", WorldId = state.WorldId, RunId = "run-one", CommandKind = "ruleCancel", Delivery = new DeliveryCommandPayload { Kind = "ruleCancel", RuleId = first.RuleId } };
            var submitted = host.Submit(request); Assert.Equal("pending", submitted.Status);
            Assert.Equal(submitted.OperationId, host.Submit(request).OperationId);
            var proposal = Assert.IsType<EffectProposal>(host.Poll(PollFor(state), clock));
            Assert.Equal("ruleCancel", proposal.OperationKind); Assert.Equal(first.RuleId, proposal.Delivery!.RuleId);
            var transition = AcceptedStateCodec.CancelRule(state, proposal.OperationId, proposal.ClientRequestId, proposal.CommandSequence, proposal.PayloadHash, proposal.ExpectedRevision, proposal.ExpectedStateHash, proposal.Delivery.RuleId, 110);
            Assert.Equal("accepted", transition.Outcome);
            Assert.Equal(second.RuleId, Assert.Single(transition.State.DeliveryRules).RuleId);
            Assert.Equal("already-in-transit", Assert.Single(transition.State.ActiveShipments).ShipmentId);
            Assert.Single(transition.State.RouteVersions);
            Assert.IsType<EffectIdle>(host.Receipt(ReceiptFor(proposal, transition.State, 110), clock));
            Assert.Equal("accepted", host.Submit(request).Status);
            Assert.Equal("rejected", host.Submit(new SubmitCommand { ProtocolVersion = 1, MessageType = "submitCommand", ClientRequestId = "cancel-unknown", WorldId = state.WorldId, RunId = "run-one", CommandKind = "ruleCancel", Delivery = new DeliveryCommandPayload { Kind = "ruleCancel", RuleId = first.RuleId } }).Status);
        }
        finally { Directory.Delete(directory, true); }
    }

    static (AcceptedState State, RouteVersionRecord Route) RuleLifecycleState()
    {
        var state = InitialState();
        var sourceHash = new string('a', 64); var destinationHash = new string('b', 64);
        var registry = new DepotRegistrySnapshot { RegistryVersion = "rule-lifecycle", Depots = [new DepotRecord { DepotId = "source", MembershipRevision = 1, MembershipHash = sourceHash }, new DepotRecord { DepotId = "destination", MembershipRevision = 1, MembershipHash = destinationHash }] };
        registry.RegistryHash = OperationIdentity.ComputeDepotRegistryHash(registry.Depots);
        state = AcceptedStateCodec.SyncDepots(state, OperationIdentity.Create(state.WorldId, 1, "sync"), "sync", 1, OperationIdentity.DepotRegistryPayloadHash(registry), state.Revision, AcceptedStateCodec.ComputeHash(state), registry, 10).State;
        var route = new RouteVersionRecord { RouteId = "rule-lifecycle-route", Version = 1, SourceDepotId = "source", SourceMembershipRevision = 1, SourceMembershipHash = sourceHash, DestinationDepotId = "destination", DestinationMembershipRevision = 1, DestinationMembershipHash = destinationHash, TravelDurationSeconds = 120, Provenance = "rule lifecycle fixture", Resources = [new ResourceAmount { ResourceName = "Fuel", AmountMicroUnits = 2_000_000 }] };
        state = AcceptedStateCodec.UpsertRoute(state, OperationIdentity.Create(state.WorldId, 2, "route"), "route", 2, OperationIdentity.RouteVersionPayloadHash(route), state.Revision, AcceptedStateCodec.ComputeHash(state), route, 20).State;
        return (state, route);
    }

    [Theory]
    [InlineData(false, false, 1)]
    [InlineData(true, false, 1000)]
    [InlineData(false, true, 2500)]
    [InlineData(true, true, 1000000)]
    public void OreExportDebitsOneBatchThenSettlesOrHoldsFundsAcrossLostReceiptRestart(bool uncertain, bool receiptDelivered, long oreUnits)
    {
        var batch = oreUnits * 1000000; var payout = oreUnits * 100;
        var directory = NewDirectory(); var state = InitialState(); var hash = new string('a', 64);
        var registry = new DepotRegistrySnapshot { RegistryVersion = "ore-source", Depots = [new DepotRecord { DepotId = "minmus-source", MembershipRevision = 1, MembershipHash = hash }] };
        registry.RegistryHash = OperationIdentity.ComputeDepotRegistryHash(registry.Depots);
        state = AcceptedStateCodec.SyncDepots(state, OperationIdentity.Create(state.WorldId, 1, "ore-sync"), "ore-sync", 1, OperationIdentity.DepotRegistryPayloadHash(registry), state.Revision, AcceptedStateCodec.ComputeHash(state), registry, 1).State;
        var route = new RouteVersionRecord { RouteId = "ore-export", Version = 1, SourceDepotId = "minmus-source", SourceMembershipRevision = 1, SourceMembershipHash = hash,
            DestinationKind = OreExportPolicy.VirtualDestinationKind, DestinationDepotId = OreExportPolicy.KerbinBuyerId, FundsPerUnit = 100, TravelDurationSeconds = 64800, Provenance = "modeled recovery",
            Resources = [new ResourceAmount { ResourceName = "Ore", AmountMicroUnits = batch }] };
        state = AcceptedStateCodec.UpsertRoute(state, OperationIdentity.Create(state.WorldId, 2, "ore-route"), "ore-route", 2, OperationIdentity.RouteVersionPayloadHash(route), state.Revision, AcceptedStateCodec.ComputeHash(state), route, 2).State;
        var rule = new DeliveryRuleRecord { RuleId = "ore-rule", Revision = 1, Kind = "exportStock", RouteId = route.RouteId, RouteVersion = 1, Enabled = true, ResourceName = "Ore", BatchSizeMicroUnits = batch };
        state = AcceptedStateCodec.UpsertRule(state, OperationIdentity.Create(state.WorldId, 3, "ore-rule"), "ore-rule", 3, OperationIdentity.RulePayloadHash(rule), state.Revision, AcceptedStateCodec.ComputeHash(state), rule, 3).State;
        Assert.Single(state.DeliveryRules);
        var capability = WritableCapability("minmus-source", 1, hash, 7, [7]);
        var clock = FreshClock(state.WorldId); EffectProposal sale; AcceptedState completed;
        EffectAttach ExportAttach(AcceptedState value)
        {
            var attach = AttachFor(value); attach.Capabilities = ["dispatch", "arrival", "economicRecovery.v1"]; attach.RegistrySnapshot = registry; attach.InventoryEndpoints = [capability]; return attach;
        }
        StockObservation OreStock(long amount, long revision)
        {
            var stock = Observation(registry, capability, state.WorldId, amount, 2 * batch, amount, 2 * batch, revision);
            stock.Resources[0].ResourceName = "Ore"; stock.MemberStocks[0].Resources[0].ResourceName = "Ore"; return stock;
        }
        try
        {
            using (var host = new RecoveryCoordinator(directory, new AlwaysLiveVerifier()))
            {
                var incapableAttach = ExportAttach(state); incapableAttach.Capabilities = ["dispatch", "arrival"];
                Assert.IsType<EffectIdle>(host.Attach(incapableAttach, clock));
                var incapablePoll = PollFor(state); incapablePoll.InventoryObservations = [OreStock(batch, 1)];
                Assert.IsType<EffectIdle>(host.Poll(incapablePoll, clock)); // Funds-unavailable Bridge cannot debit export cargo.
                Assert.IsType<EffectIdle>(host.Attach(ExportAttach(state), clock));
                var shortage = PollFor(state); shortage.InventoryObservations = [OreStock(batch - 1, 1)];
                Assert.IsType<EffectIdle>(host.Poll(shortage, clock));
                var ready = PollFor(state); ready.InventoryObservations = [OreStock(batch, 2)];
                var dispatch = Assert.IsType<EffectProposal>(host.Poll(ready, clock));
                Assert.Equal("dispatch", dispatch.OperationKind); Assert.Equal(-batch, Assert.Single(dispatch.Delivery!.PhysicalEffect!.Deltas).DeltaMicroUnits);
                var transition = AcceptedStateCodec.Dispatch(state, dispatch.OperationId, dispatch.ClientRequestId, dispatch.CommandSequence, dispatch.PayloadHash, dispatch.ExpectedRevision, dispatch.ExpectedStateHash, dispatch.Delivery.Shipment!, dispatch.Delivery.PhysicalEffect!, 100, dispatch.Delivery.ScheduleRuleUpdate, SuccessWitness(dispatch.Delivery.PhysicalEffect!, oreUnits));
                Assert.True(transition.Outcome == "accepted", transition.Reason); state = transition.State;
                Assert.Equal(64900, Assert.Single(state.ActiveShipments).DueUt);
                var dispatchReceipt = ReceiptFor(dispatch, state, 100);
                dispatchReceipt.PhysicalResult = new PhysicalEffectResult { Status = "applied", RollbackStatus = "none", Deltas = [new PhysicalResourceDelta { MemberPersistentId = 7, ResourceName = "Ore", BeforeAmount = oreUnits, IntendedDeltaMicroUnits = -batch, IntendedAfterAmount = 0, ObservedAfterKnown = true, ObservedAfterAmount = 0 }] };
                Assert.IsType<EffectIdle>(host.Receipt(dispatchReceipt, clock));
                var empty = PollFor(state); empty.InventoryObservations = [OreStock(0, 3)];
                Assert.IsType<EffectIdle>(host.Poll(empty, clock));
                clock.Accept(new ClockSample(1, "clockSample", 2, Session, Epoch, Install, Save, "Synthetic", 64900, true, "Flight", false, "Year 1", 1, WorldId: state.WorldId, RunId: "run-one"));
                sale = Assert.IsType<EffectProposal>(host.Poll(PollFor(state), clock));
                Assert.Equal("recoverySale", sale.OperationKind); Assert.Equal(payout, sale.Delivery!.RecoveryIntent!.FundsDelta);
                var result = new EconomicEffectResult { Status = uncertain ? "uncertain" : "applied", BeforeFunds = 12345, IntendedDeltaFunds = payout, IntendedAfterFunds = 12345 + payout, ObservedAfterKnown = !uncertain, ObservedAfterFunds = uncertain ? 0 : 12345 + payout, Reason = uncertain ? "Callback persisted pending economic effect" : "" };
                var settlement = uncertain
                    ? AcceptedStateCodec.FaultRecovery(state, sale.OperationId, sale.ClientRequestId, sale.CommandSequence, sale.PayloadHash, sale.ExpectedRevision, sale.ExpectedStateHash, sale.Delivery.RecoveryIntent, result, 64900)
                    : AcceptedStateCodec.SettleRecovery(state, sale.OperationId, sale.ClientRequestId, sale.CommandSequence, sale.PayloadHash, sale.ExpectedRevision, sale.ExpectedStateHash, sale.Delivery.RecoveryIntent, 64900, new FundsSuccessWitness { BeforeFunds = 12345, IntendedDeltaFunds = payout, IntendedAfterFunds = 12345 + payout, ObservedAfterFunds = 12345 + payout });
                Assert.Equal(uncertain ? "faulted" : "accepted", settlement.Outcome); completed = settlement.State;
                if (receiptDelivered)
                {
                    var settlementReceipt = ReceiptFor(sale, completed, 64900); settlementReceipt.Outcome = settlement.Outcome;
                    settlementReceipt.EconomicResult = new EconomicEffectResult { Status = result.Status, BeforeFunds = result.BeforeFunds, IntendedDeltaFunds = result.IntendedDeltaFunds, IntendedAfterFunds = result.IntendedAfterFunds, ObservedAfterKnown = result.ObservedAfterKnown, ObservedAfterFunds = result.ObservedAfterFunds, Reason = result.Reason };
                    settlementReceipt.EconomicResult.IntendedDeltaFunds = payout - 1;
                    Assert.IsType<EffectNeedAttach>(host.Receipt(settlementReceipt, clock));
                    settlementReceipt.EconomicResult.IntendedDeltaFunds = payout;
                    Assert.IsType<EffectIdle>(host.Receipt(settlementReceipt, clock));
                }
                // Deliberately lose the receipt after the game committed its save witness.
            }
            using (var restarted = new RecoveryCoordinator(directory, new AlwaysLiveVerifier()))
            {
                Assert.IsType<EffectIdle>(restarted.Attach(ExportAttach(completed), clock));
                var status = restarted.GetCommandStatus(new GetCommandStatus { ProtocolVersion = 1, MessageType = "getCommandStatus", ClientRequestId = sale.ClientRequestId, WorldId = completed.WorldId, RunId = "run-one" });
                Assert.Equal(uncertain ? "faulted" : "accepted", status.Status);
                Assert.Equal(uncertain, completed.WritesBlocked); Assert.Equal(uncertain ? 1 : 0, completed.ActiveShipments.Length);
                Assert.IsType<EffectIdle>(restarted.Poll(PollFor(completed), clock));
                if (!uncertain)
                {
                    // Quickload selects the earlier dispatched capsule together with
                    // its earlier funds balance. Newer Host settlement history is
                    // archival and must not strand this restored shipment.
                    var restoredEpoch = Guid.NewGuid(); const string restoredRun = "restored-before-recovery";
                    clock.Accept(new ClockSample(1, "clockSample", 3, Session, restoredEpoch, Install, Save, "Synthetic restored", 64900, true, "Flight", false, "Year 1", 1, WorldId: state.WorldId, RunId: restoredRun));
                    var restoredAttach = ExportAttach(state); restoredAttach.LoadEpoch = restoredEpoch; restoredAttach.RunId = restoredRun;
                    Assert.IsType<EffectIdle>(restarted.Attach(restoredAttach, clock));
                    var restoredPoll = PollFor(state, restoredRun); restoredPoll.LoadEpoch = restoredEpoch;
                    var restoredSale = Assert.IsType<EffectProposal>(restarted.Poll(restoredPoll, clock));
                    Assert.Equal("recoverySale", restoredSale.OperationKind);
                    Assert.Equal(sale.Delivery!.RecoveryIntent!.ShipmentId, restoredSale.Delivery!.RecoveryIntent!.ShipmentId);
                    Assert.NotEqual(sale.ClientRequestId, restoredSale.ClientRequestId);
                    Assert.NotEqual(sale.OperationId, restoredSale.OperationId);
                    Assert.Equal(payout, restoredSale.Delivery.RecoveryIntent.FundsDelta);
                    var sameRunRetry = Assert.IsType<EffectProposal>(restarted.Poll(restoredPoll, clock));
                    Assert.Equal(restoredSale.OperationId, sameRunRetry.OperationId);
                }
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void HistoricalDepartedCargoPreparesOriginalFiveHundredThousandRecovery()
    {
        var state=OreExportTests.Dispatched().State; var directory=NewDirectory();
        try
        {
            using var host=new RecoveryCoordinator(directory,new AlwaysLiveVerifier());
            var clock=FreshClock(state.WorldId); clock.Accept(new ClockSample(1,"clockSample",2,Session,Epoch,Install,Save,"Synthetic",64900,true,"Flight",false,"Year 1",1,WorldId:state.WorldId,RunId:"run-one"));
            var attach=AttachFor(state); attach.Capabilities=["dispatch","arrival","economicRecovery.v1"]; attach.RegistrySnapshot=new DepotRegistrySnapshot { RegistryVersion=state.DepotRegistryVersion, RegistryHash=state.DepotRegistryHash, Depots=state.Depots.Where(x=>x.Active).ToArray() }; attach.InventoryEndpoints=state.Depots.Where(x=>x.Active).Select(x=>WritableCapability(x.DepotId,x.MembershipRevision,x.MembershipHash,7,[7])).ToArray();
            Assert.IsType<EffectIdle>(host.Attach(attach,clock));
            var polled=host.Poll(PollFor(state),clock); Assert.True(polled is EffectProposal, (polled as EffectIdle)?.Reason); var sale=Assert.IsType<EffectProposal>(polled);
            Assert.Equal("recoverySale",sale.OperationKind); Assert.Equal(500000,sale.Delivery!.RecoveryIntent!.FundsDelta);
        }
        finally { Directory.Delete(directory,true); }
    }

    [Fact]
    public void DueOreRecoveryCompactsFullReceiptLedgerThenPaysExactlyOnce()
    {
        var state = OreExportTests.Dispatched().State;
        var registry = new DepotRegistrySnapshot { RegistryVersion = state.DepotRegistryVersion,
            RegistryHash = state.DepotRegistryHash, Depots = state.Depots.Where(x => x.Active).ToArray() };
        while (state.Receipts.Length < AcceptedStateCodec.MaxReceipts)
        {
            var sequence = state.AcceptedSequence + 1;
            var request = "fill-receipts-" + sequence;
            var transition = AcceptedStateCodec.Increment(state, state.WorldId, OperationIdentity.Create(state.WorldId, sequence, request),
                request, sequence, OperationIdentity.CounterIncrementPayloadHash(1), 1, 20);
            Assert.Equal("accepted", transition.Outcome);
            state = transition.State;
        }
        var directory = NewDirectory();
        try
        {
            using var host = new RecoveryCoordinator(directory, new AlwaysLiveVerifier());
            var clock = FreshClock(state.WorldId);
            clock.Accept(new ClockSample(1, "clockSample", 2, Session, Epoch, Install, Save, "Synthetic", 64900,
                true, "Flight", false, "Year 1", 1, WorldId: state.WorldId, RunId: "run-one"));
            var attach = AttachFor(state);
            attach.Capabilities = ["dispatch", "arrival", "economicRecovery.v1"];
            attach.RegistrySnapshot = registry;
            attach.InventoryEndpoints = state.Depots.Where(x => x.Active)
                .Select(x => WritableCapability(x.DepotId, x.MembershipRevision, x.MembershipHash, 7, [7])).ToArray();
            Assert.IsType<EffectIdle>(host.Attach(attach, clock));
            var compact = Assert.IsType<EffectProposal>(host.Poll(PollFor(state), clock));
            Assert.Equal("compact", compact.OperationKind);
            Assert.Equal(16, compact.TargetCompactionWatermark);
            state = AcceptedStateCodec.Compact(state, compact.OperationId, compact.ClientRequestId,
                compact.CommandSequence, compact.PayloadHash, compact.ExpectedRevision, compact.ExpectedStateHash,
                compact.TargetCompactionWatermark, 64900);
            Assert.Single(state.ActiveShipments);
            Assert.IsType<EffectIdle>(host.Receipt(ReceiptFor(compact, state, 64900), clock));
            var sale = Assert.IsType<EffectProposal>(host.Poll(PollFor(state), clock));
            Assert.Equal("recoverySale", sale.OperationKind);
            var intent = sale.Delivery!.RecoveryIntent!;
            Assert.Equal(500000, intent.FundsDelta);
            var settled = AcceptedStateCodec.SettleRecovery(state, sale.OperationId, sale.ClientRequestId,
                sale.CommandSequence, sale.PayloadHash, sale.ExpectedRevision, sale.ExpectedStateHash, intent, 64900,
                new FundsSuccessWitness { BeforeFunds = 125, IntendedDeltaFunds = 500000,
                    IntendedAfterFunds = 500125, ObservedAfterFunds = 500125 });
            Assert.Equal("accepted", settled.Outcome);
            var receipt = ReceiptFor(sale, settled.State, 64900);
            receipt.EconomicResult = new EconomicEffectResult { Status = "applied", BeforeFunds = 125,
                IntendedDeltaFunds = 500000, IntendedAfterFunds = 500125, ObservedAfterKnown = true, ObservedAfterFunds = 500125 };
            Assert.IsType<EffectIdle>(host.Receipt(receipt, clock));
            Assert.IsType<EffectNeedAttach>(host.Receipt(receipt, clock));
            Assert.Equal("duplicate", AcceptedStateCodec.SettleRecovery(settled.State, sale.OperationId,
                sale.ClientRequestId, sale.CommandSequence, sale.PayloadHash, sale.ExpectedRevision, sale.ExpectedStateHash,
                intent, 64900, settled.State.Receipts.Last().FundsWitness!).Outcome);
            Assert.IsType<EffectIdle>(host.Poll(PollFor(settled.State), clock));
            Assert.Empty(settled.State.ActiveShipments);
            Assert.Single(settled.State.Receipts.Where(x => x.OperationKind == "recoverySale"));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisableOnlyPreservesLegacyRuleTermsEvenWhenEndpointsAreStale(bool stale)
    {
        var state=OreExportTests.Dispatched().State;
        var rule=new DeliveryRuleRecord { RuleId="legacy-rule", Revision=1, Kind="exportStock", RouteId="ore-route", RouteVersion=1, Enabled=true, ResourceName="Ore", BatchSizeMicroUnits=OreExportPolicy.BatchMicroUnits };
        state=AcceptedStateCodec.UpsertRule(state,OperationIdentity.Create(state.WorldId,4,"rule"),"rule",4,OperationIdentity.RulePayloadHash(rule),state.Revision,AcceptedStateCodec.ComputeHash(state),rule,11).State;
        if(stale) { var registry=new DepotRegistrySnapshot { RegistryVersion="stale", Depots=[] }; registry.RegistryHash=OperationIdentity.ComputeDepotRegistryHash(registry.Depots); state=AcceptedStateCodec.SyncDepots(state,OperationIdentity.Create(state.WorldId,5,"stale"),"stale",5,OperationIdentity.DepotRegistryPayloadHash(registry),state.Revision,AcceptedStateCodec.ComputeHash(state),registry,12).State; }
        var method=typeof(RecoveryCoordinator).GetMethod("IsDisableOnly",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!;
        bool Check(DeliveryRuleRecord candidate)=>(bool)method.Invoke(null,[state,candidate])!;
        var pause=new DeliveryRuleRecord { RuleId=rule.RuleId, Revision=2, Kind=rule.Kind, RouteId=rule.RouteId, RouteVersion=rule.RouteVersion, Enabled=false, ResourceName="Ore", BatchSizeMicroUnits=rule.BatchSizeMicroUnits };
        Assert.True(Check(pause)); pause.BatchSizeMicroUnits=1000000; Assert.False(Check(pause)); pause.BatchSizeMicroUnits=rule.BatchSizeMicroUnits;
        pause.RouteVersion=2; Assert.False(Check(pause)); pause.RouteVersion=1; pause.Enabled=true; Assert.False(Check(pause));
        var directory=NewDirectory();
        try
        {
            using var host=new RecoveryCoordinator(directory,new AlwaysLiveVerifier()); var clock=FreshClock(state.WorldId); var attach=AttachFor(state); attach.Capabilities=["dispatch","arrival","economicRecovery.v1"]; attach.RegistrySnapshot=new DepotRegistrySnapshot { RegistryVersion=state.DepotRegistryVersion, RegistryHash=state.DepotRegistryHash, Depots=state.Depots.Where(x=>x.Active).ToArray() }; attach.InventoryEndpoints=state.Depots.Where(x=>x.Active).Select(x=>WritableCapability(x.DepotId,x.MembershipRevision,x.MembershipHash,7,[7])).ToArray();
            Assert.IsType<EffectIdle>(host.Attach(attach,clock));
            var response=host.Submit(new SubmitCommand { ProtocolVersion=1, MessageType="submitCommand", ClientRequestId="enable-legacy", WorldId=state.WorldId, RunId="run-one", CommandKind="ruleUpsert", Delivery=new DeliveryCommandPayload { Kind="ruleUpsert", Rule=pause } });
            Assert.Equal("rejected",response.Status);
            pause.Enabled=false; var paused=host.Submit(new SubmitCommand { ProtocolVersion=1, MessageType="submitCommand", ClientRequestId="pause-legacy", WorldId=state.WorldId, RunId="run-one", CommandKind="ruleUpsert", Delivery=new DeliveryCommandPayload { Kind="ruleUpsert", Rule=pause } }); Assert.Equal("pending",paused.Status);
        }
        finally { Directory.Delete(directory,true); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreUpgradePreparedLegacyDispatchIsHeldUnlessItsAcceptedReceiptExists(bool accepted)
    {
        var departed=OreExportTests.Dispatched(); var state=AcceptedStateCodec.Deserialize(AcceptedStateCodec.Serialize(departed.State));
        state.ActiveShipments=[]; state.Receipts=state.Receipts.Take(2).ToArray(); state.Revision--; state.AcceptedSequence--;
        var request="dispatch"; var sequence=3L; var operation=OperationIdentity.Create(state.WorldId,sequence,request);
        var payload=OperationIdentity.DispatchPayloadHash(departed.Shipment,departed.Intent);
        var delivery=new DeliveryEffectPayload { Kind="dispatch", Shipment=departed.Shipment, PhysicalEffect=departed.Intent };
        var directory=NewDirectory();
        try
        {
            using(var initialize=new RecoveryCoordinator(directory,new AlwaysLiveVerifier())) { var initialAttach=AttachFor(state); initialAttach.Capabilities=["dispatch","arrival","economicRecovery.v1"]; Assert.IsType<EffectIdle>(initialize.Attach(initialAttach,FreshClock(state.WorldId))); }
            using(var database=new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(directory,"recovery.sqlite3")};Pooling=False"))
            {
                database.Open(); using var insert=database.CreateCommand();
                insert.CommandText="INSERT INTO Prepared(operationId,requestId,world,run,sequence,revision,expectedHash,payloadHash,kind,delta,targetWatermark,status,deliveryJson) VALUES($op,$req,$world,'run-one',3,$rev,$hash,$payload,'dispatch',0,0,'prepared',$delivery)";
                insert.Parameters.AddWithValue("$op",operation); insert.Parameters.AddWithValue("$req",request); insert.Parameters.AddWithValue("$world",state.WorldId); insert.Parameters.AddWithValue("$rev",state.Revision); insert.Parameters.AddWithValue("$hash",AcceptedStateCodec.ComputeHash(state)); insert.Parameters.AddWithValue("$payload",payload); insert.Parameters.AddWithValue("$delivery",System.Text.Json.JsonSerializer.Serialize(delivery,ClockProtocol.JsonOptions)); insert.ExecuteNonQuery();
            }
            using var host=new RecoveryCoordinator(directory,new AlwaysLiveVerifier()); var selected=accepted?departed.State:state; var clock=FreshClock(state.WorldId); var attach=AttachFor(selected); attach.Capabilities=["dispatch","arrival","economicRecovery.v1"];
            Assert.IsType<EffectIdle>(host.Attach(attach,clock));
            Assert.IsNotType<EffectProposal>(host.Poll(PollFor(selected),clock));
            var status=host.GetCommandStatus(new GetCommandStatus { ProtocolVersion=1, MessageType="getCommandStatus", ClientRequestId=request, WorldId=state.WorldId, RunId="run-one" });
            Assert.Equal(accepted?"accepted":"rejected",status.Status); if(!accepted) Assert.True(status.ConfirmedTerminal);
        }
        finally { Directory.Delete(directory,true); }
    }
    static EffectPoll PollFor(AcceptedState state, string run = "run-one") => new() { ProtocolVersion = 1, MessageType = "effectPoll", SessionId = Session, LoadEpoch = Epoch, InstallNamespace = Install, SaveFolder = Save, WorldId = state.WorldId, RunId = run, CheckpointId = state.CheckpointId, Revision = state.Revision, AcceptedSequence = state.AcceptedSequence, CompactionWatermark = state.CompactionWatermark, StateHash = AcceptedStateCodec.ComputeHash(state) };
    static EffectReceipt ReceiptFor(EffectProposal p, AcceptedState state, double ut) => new() { ProtocolVersion = 1, MessageType = "effectReceipt", SessionId = p.SessionId, LoadEpoch = p.LoadEpoch, InstallNamespace = p.InstallNamespace, SaveFolder = p.SaveFolder, WorldId = p.WorldId, RunId = p.RunId, OperationId = p.OperationId, ClientRequestId = p.ClientRequestId, CommandSequence = p.CommandSequence, PayloadHash = p.PayloadHash, OperationKind = p.OperationKind, TargetCompactionWatermark = p.TargetCompactionWatermark, Outcome = "accepted", AppliedUt = ut, ActualCounterDelta = p.CounterDelta, AcceptedRevision = state.Revision, AcceptedSequence = state.AcceptedSequence, StateHash = AcceptedStateCodec.ComputeHash(state), AcceptedCapsule = AcceptedStateCodec.CreateCapsule(state) };
}
