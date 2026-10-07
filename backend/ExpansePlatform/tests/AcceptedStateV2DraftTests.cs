using Expanse.Domain;

namespace Expanse.Clock.Tests;

public sealed class AcceptedStateV2DraftTests
{
    static AcceptedState LegacyState() => new()
    {
        WorldId = "world", CheckpointId = "checkpoint",
        Depots = [new DepotRecord { DepotId = "source", MembershipRevision = 1, MembershipHash = new string('a', 64) }, new DepotRecord { DepotId = "destination", MembershipRevision = 2, MembershipHash = new string('b', 64) }]
    };

    static RouteVersionRecord Route() => new()
    {
        RouteId = "route", Version = 1, SourceDepotId = "source", DestinationDepotId = "destination",
        SourceMembershipRevision = 1, SourceMembershipHash = new string('a', 64), DestinationMembershipRevision = 2, DestinationMembershipHash = new string('b', 64),
        TravelDurationSeconds = 12.5, Provenance = "user-entered static profile",
        Resources = [new ResourceAmount { ResourceName = "Fuel", AmountMicroUnits = 2_000_000 }]
    };

    [Fact]
    public void V2CollectionsValidateImmutableRouteReferencesActiveCargoAndRuleState()
    {
        var route = Route();
        var rule = new DeliveryRuleRecord { RuleId = "repeat", Revision = 2, Kind = "repeat", RouteId = route.RouteId, RouteVersion = route.Version, Enabled = true, NextDueUt = 100, IntervalSeconds = 30, WaitingRequest = true, WaitingScheduledUt = 70, WaitingCoalescedSlots = 3 };
        var shipment = new ActiveShipmentRecord { ShipmentId = "shipment", Revision = 1, RouteId = route.RouteId, RouteVersion = 1, SourceDepotId = "source", DestinationDepotId = "destination", DepartureUt = 5, DueUt = 17.5, RemainingResources = [new ResourceAmount { ResourceName = "Fuel", AmountMicroUnits = 1_000_000 }], HeldReason = "destination full" };

        AcceptedStateV2Draft.Validate(LegacyState(), [route], [rule], [shipment], []);
        route.Version = 2;
        var duplicateVersion = new RouteVersionRecord { RouteId = route.RouteId, Version = route.Version, SourceDepotId = route.SourceDepotId, DestinationDepotId = route.DestinationDepotId, TravelDurationSeconds = route.TravelDurationSeconds, Provenance = route.Provenance, Resources = route.Resources };
        Assert.Throws<InvalidDataException>(() => AcceptedStateV2Draft.Validate(LegacyState(), [route, duplicateVersion], [rule], [shipment], []));
    }

    [Fact]
    public void V2ValidatorRejectsUnknownReferencesBadDurationsAndOversizedCollections()
    {
        var route = Route(); route.DestinationDepotId = "unknown";
        Assert.Throws<InvalidDataException>(() => AcceptedStateV2Draft.Validate(LegacyState(), [route], [], [], []));
        route = Route(); route.TravelDurationSeconds = double.PositiveInfinity;
        Assert.Throws<InvalidDataException>(() => AcceptedStateV2Draft.Validate(LegacyState(), [route], [], [], []));
        Assert.Throws<InvalidDataException>(() => AcceptedStateV2Draft.Validate(LegacyState(), Enumerable.Range(0, 17).Select(i => new RouteVersionRecord { RouteId = "r" + i, Version = 1, SourceDepotId = "source", DestinationDepotId = "destination", TravelDurationSeconds = 1, Provenance = "static", Resources = [new ResourceAmount { ResourceName = "Fuel", AmountMicroUnits = 1 }] }).ToArray(), [], [], []));
    }

    [Fact]
    public void V1OpaqueEntriesCanBeCarriedWithoutInventingRouteOrShipmentData()
    {
        var route = new RouteVersionRecord { RouteId = "old-route", Version = 0, LegacyOpaque = true };
        var shipment = new ActiveShipmentRecord { ShipmentId = "old-shipment", LegacyOpaque = true };
        AcceptedStateV2Draft.Validate(LegacyState(), [route], [], [shipment], []);
        Assert.Throws<InvalidDataException>(() => AcceptedStateV2Draft.Validate(LegacyState(), [new RouteVersionRecord { RouteId = "bad", Version = 0, LegacyOpaque = true, SourceDepotId = "invented" }], [], [], []));
    }

    [Fact]
    public void RegistrySyncKeepsRemovedDepotAsTombstoneAndUsesCanonicalHash()
    {
        var prior = LegacyState();
        var snapshot = new DepotRegistrySnapshot { RegistryVersion = "registry-v2", Depots = [new DepotRecord { DepotId = "source", MembershipRevision = 1, MembershipHash = new string('a', 64) }, new DepotRecord { DepotId = "destination", MembershipRevision = 3, MembershipHash = new string('c', 64) }] };
        snapshot.RegistryHash = OperationIdentity.ComputeDepotRegistryHash(snapshot.Depots);
        var seq = prior.AcceptedSequence + 1; const string request = "registry-sync";
        var result = AcceptedStateCodec.SyncDepots(prior, OperationIdentity.Create(prior.WorldId, seq, request), request, seq, OperationIdentity.DepotRegistryPayloadHash(snapshot), prior.Revision, AcceptedStateCodec.ComputeHash(prior), snapshot, 100);
        Assert.Equal("accepted", result.Outcome);
        Assert.Equal(2, result.State.SchemaVersion);
        Assert.Equal(snapshot.RegistryHash, result.State.DepotRegistryHash);
        Assert.Contains(result.State.Depots, d => d.DepotId == "source" && d.MembershipRevision == 1 && !d.Active);
        Assert.Contains(result.State.Depots, d => d.DepotId == "destination" && d.MembershipRevision == 2 && !d.Active);
        Assert.Contains(result.State.Depots, d => d.DepotId == "destination" && d.MembershipRevision == 3 && d.Active);
        var changedMembers = new[] { new DepotRecord { DepotId = "source", MembershipRevision = 1, MembershipHash = new string('a', 64) }, new DepotRecord { DepotId = "destination", MembershipRevision = 3, MembershipHash = new string('d', 64) } };
        Assert.NotEqual(snapshot.RegistryHash, OperationIdentity.ComputeDepotRegistryHash(changedMembers));
        var route = new RouteVersionRecord { RouteId = "historic", Version = 1, SourceDepotId = "source", SourceMembershipRevision = 1, SourceMembershipHash = new string('a', 64), DestinationDepotId = "destination", DestinationMembershipRevision = 3, DestinationMembershipHash = new string('c', 64), TravelDurationSeconds = 60, Provenance = "test", Resources = [new ResourceAmount { ResourceName = "Fuel", AmountMicroUnits = 1 }] };
        var routeId = OperationIdentity.Create(result.State.WorldId, 2, "route"); var routeHash = OperationIdentity.RouteVersionPayloadHash(route);
        var withRoute = AcceptedStateCodec.UpsertRoute(result.State, routeId, "route", 2, routeHash, result.State.Revision, AcceptedStateCodec.ComputeHash(result.State), route, 101);
        Assert.Equal("accepted", withRoute.Outcome);
        var changedSnapshot = new DepotRegistrySnapshot { RegistryVersion = "registry-v3", Depots = changedMembers }; changedSnapshot.RegistryHash = OperationIdentity.ComputeDepotRegistryHash(changedMembers);
        var changedSync = AcceptedStateCodec.SyncDepots(withRoute.State, OperationIdentity.Create(withRoute.State.WorldId, 3, "sync-members"), "sync-members", 3, OperationIdentity.DepotRegistryPayloadHash(changedSnapshot), withRoute.State.Revision, AcceptedStateCodec.ComputeHash(withRoute.State), changedSnapshot, 102);
        Assert.Equal("accepted", changedSync.Outcome);
        Assert.Contains(changedSync.State.Depots, d => d.DepotId == "destination" && d.MembershipRevision == 3 && d.MembershipHash == new string('c', 64) && !d.Active);
        var blockedRoute = new RouteVersionRecord { RouteId = "historic", Version = 2, SourceDepotId = "source", SourceMembershipRevision = 1, SourceMembershipHash = new string('a', 64), DestinationDepotId = "destination", DestinationMembershipRevision = 3, DestinationMembershipHash = new string('c', 64), TravelDurationSeconds = 60, Provenance = "test", Resources = [new ResourceAmount { ResourceName = "Fuel", AmountMicroUnits = 1 }] };
        Assert.Equal("held", AcceptedStateCodec.UpsertRoute(changedSync.State, OperationIdentity.Create(changedSync.State.WorldId, 4, "old-members"), "old-members", 4, OperationIdentity.RouteVersionPayloadHash(blockedRoute), changedSync.State.Revision, AcceptedStateCodec.ComputeHash(changedSync.State), blockedRoute, 103).Outcome);
        var roundTrip = AcceptedStateCodec.Deserialize(AcceptedStateCodec.Serialize(changedSync.State));
        Assert.Equal(AcceptedStateCodec.ComputeHash(changedSync.State), AcceptedStateCodec.ComputeHash(roundTrip));
    }

    [Fact]
    public void Exs2RoundTripKeepsItsBytesAndPhysicalSuccessWitnessPromotesToExs3()
    {
        var state = LegacyState();
        var registry = new DepotRegistrySnapshot { RegistryVersion = "registry", Depots = state.Depots.Select(x => new DepotRecord { DepotId = x.DepotId, MembershipRevision = x.MembershipRevision, MembershipHash = x.MembershipHash }).ToArray() };
        registry.RegistryHash = OperationIdentity.ComputeDepotRegistryHash(registry.Depots);
        var syncId = OperationIdentity.Create(state.WorldId, 1, "sync");
        state = AcceptedStateCodec.SyncDepots(state, syncId, "sync", 1, OperationIdentity.DepotRegistryPayloadHash(registry), state.Revision, AcceptedStateCodec.ComputeHash(state), registry, 1).State;
        var route = Route(); var routeId = OperationIdentity.Create(state.WorldId, 2, "route");
        state = AcceptedStateCodec.UpsertRoute(state, routeId, "route", 2, OperationIdentity.RouteVersionPayloadHash(route), state.Revision, AcceptedStateCodec.ComputeHash(state), route, 2).State;

        var exs2 = AcceptedStateCodec.Serialize(state);
        Assert.Equal(new byte[] { 69, 88, 83, 50 }, exs2[..4]);
        var exs2RoundTrip = AcceptedStateCodec.Serialize(AcceptedStateCodec.Deserialize(exs2));
        Assert.Equal(exs2, exs2RoundTrip);

        var capability = new InventoryCapability { DepotId = "source", MembershipRevision = 1, MembershipHash = new string('a', 64), AnchorPersistentId = 7, MemberSetHash = OperationIdentity.ComputeMemberSetHash(7, [7]), ProviderId = "synthetic-provider", ProviderVersion = "1", Scene = "Flight", ObservationAvailable = true, ReadSupported = true, WriteSupported = true, SynchronousRollbackSupported = true, PersistenceSyncSupported = true };
        var intent = new PhysicalEffectIntent { EffectKind = "dispatchDebit", Capability = capability, MembershipRevision = 1, MemberPersistentIds = [7], Deltas = [new PhysicalEffectResourceDelta { MemberPersistentId = 7, ResourceName = "Fuel", DeltaMicroUnits = -2_000_000 }] };
        var shipment = new ActiveShipmentRecord { ShipmentId = "shipment-1", RouteId = route.RouteId, RouteVersion = route.Version, SourceDepotId = route.SourceDepotId, DestinationDepotId = route.DestinationDepotId, RemainingResources = [new ResourceAmount { ResourceName = "Fuel", AmountMicroUnits = 2_000_000 }] };
        var witness = new PhysicalSuccessWitness { ProviderId = capability.ProviderId, ProviderVersion = capability.ProviderVersion, Rows = [new PhysicalSuccessWitnessRow { MemberPersistentId = 7, ResourceName = "Fuel", BeforeAmount = 10, IntendedAfterAmount = 8, ObservedAfterAmount = 8 }] };
        var op = OperationIdentity.Create(state.WorldId, 3, "dispatch"); var payload = OperationIdentity.DispatchPayloadHash(shipment, intent);
        var dispatched = AcceptedStateCodec.Dispatch(state, op, "dispatch", 3, payload, state.Revision, AcceptedStateCodec.ComputeHash(state), shipment, intent, 3, null, witness);
        Assert.Equal("accepted", dispatched.Outcome);
        var exs3 = AcceptedStateCodec.Serialize(dispatched.State);
        Assert.Equal(new byte[] { 69, 88, 83, 51 }, exs3[..4]);
        var exs3RoundTrip = AcceptedStateCodec.Deserialize(exs3);
        Assert.Equal(3, exs3RoundTrip.CapsuleEncodingVersion);
        Assert.Equal(AcceptedStateCodec.ComputeHash(dispatched.State), AcceptedStateCodec.ComputeHash(exs3RoundTrip));
        Assert.Equal(exs3, AcceptedStateCodec.Serialize(exs3RoundTrip));
        var persistedWitness = Assert.Single(exs3RoundTrip.Receipts, x => x.OperationId == op).PhysicalWitness!;
        Assert.Equal(capability.ProviderId, persistedWitness.ProviderId); Assert.Equal("Fuel", Assert.Single(persistedWitness.Rows).ResourceName);
    }

    [Fact]
    public void UncertainPhysicalFaultIsBoundedAndBlocksItsDepotOnly()
    {
        var fault = new PhysicalFaultRecord
        {
            FaultId = "fault", OperationId = "op", OperationKind = "dispatch", CommandSequence = 4, DepotId = "source", MembershipRevision = 1, MembershipHash = new string('a', 64),
            Reason = "Rollback could not be confirmed.", RollbackStatus = "unknown",
            Deltas = [new PhysicalResourceDelta { MemberPersistentId = 7, ResourceName = "Fuel", BeforeAmount = 10.25, IntendedDeltaMicroUnits = -1_000_000, IntendedAfterAmount = 9.25, ObservedAfterKnown = true, ObservedAfterAmount = 9.75, RollbackObserved = true, RollbackObservedAmount = 9.75 }]
        };
        AcceptedStateV2Draft.Validate(LegacyState(), [], [], [], [fault]);
        Assert.True(AcceptedStateV2Draft.IsDepotWriteBlocked([fault], "source"));
        Assert.False(AcceptedStateV2Draft.IsDepotWriteBlocked([fault], "destination"));
        fault.RollbackStatus = "confirmed";
        Assert.Throws<InvalidDataException>(() => AcceptedStateV2Draft.Validate(LegacyState(), [], [], [], [fault]));
    }

    [Fact]
    public void PhysicalIntentIsCapabilityGatedAndResultSeparatesRollbackFromUncertainFault()
    {
        var intent = new PhysicalEffectIntent
        {
            EffectKind = "dispatchDebit", MembershipRevision = 2,
            Capability = new InventoryCapability { DepotId = "source", MembershipRevision = 2, MembershipHash = new string('c', 64), AnchorPersistentId = 7, MemberSetHash = OperationIdentity.ComputeMemberSetHash(7, [7]), ProviderId = "provider", ProviderVersion = "1.0", Scene = "Flight", ObservationAvailable = true, ReadSupported = true, WriteSupported = true, SynchronousRollbackSupported = true, PersistenceSyncSupported = true },
            MemberPersistentIds = [7], Deltas = [new PhysicalEffectResourceDelta { MemberPersistentId = 7, ResourceName = "Fuel", DeltaMicroUnits = -1_000_000 }]
        };
        Assert.Null(AcceptedStateV2Draft.ValidatePhysicalIntent(intent)); // This only clears structural capability preflight.
        intent.Capability.PersistenceSyncSupported = false;
        Assert.Contains("unavailable", AcceptedStateV2Draft.ValidatePhysicalIntent(intent));

        Assert.Throws<InvalidDataException>(() => AcceptedStateV2Draft.ValidatePhysicalResult(new PhysicalEffectResult { Status = "uncertain", RollbackStatus = "none", Reason = "fault" }));
        AcceptedStateV2Draft.ValidatePhysicalResult(new PhysicalEffectResult
        {
            Status = "uncertain", RollbackStatus = "unknown", Reason = "uncertain write",
            Deltas = [new PhysicalResourceDelta { MemberPersistentId = 7, ResourceName = "Fuel", BeforeAmount = 2, IntendedDeltaMicroUnits = -1, IntendedAfterAmount = 1, ObservedAfterKnown = false, ObservedAfterAmount = 0 }]
        });
        AcceptedStateV2Draft.ValidatePhysicalResult(new PhysicalEffectResult { Status = "rollbackConfirmed", RollbackStatus = "confirmed", Reason = "restored" });
    }

    [Fact]
    public void DispatchUsesBridgeAppliedUtAndArrivalConservesPartialResidualCargo()
    {
        var initial = LegacyState();
        var snapshot = new DepotRegistrySnapshot { RegistryVersion = "registry", Depots = [new DepotRecord { DepotId = "source", MembershipRevision = 1, MembershipHash = new string('a', 64) }, new DepotRecord { DepotId = "destination", MembershipRevision = 2, MembershipHash = new string('b', 64) }] };
        snapshot.RegistryHash = OperationIdentity.ComputeDepotRegistryHash(snapshot.Depots);
        var state = AcceptedStateCodec.SyncDepots(initial, OperationIdentity.Create(initial.WorldId, 1, "sync"), "sync", 1, OperationIdentity.DepotRegistryPayloadHash(snapshot), initial.Revision, AcceptedStateCodec.ComputeHash(initial), snapshot, 10).State;
        var route = Route();
        state = AcceptedStateCodec.UpsertRoute(state, OperationIdentity.Create(state.WorldId, 2, "route"), "route", 2, OperationIdentity.RouteVersionPayloadHash(route), state.Revision, AcceptedStateCodec.ComputeHash(state), route, 11).State;

        var sourceCapability = Capability("source", 1, new string('a', 64), 7, [7, 8]);
        var debit = new PhysicalEffectIntent { EffectKind = "dispatchDebit", Capability = sourceCapability, MembershipRevision = 1, MemberPersistentIds = [7, 8], Deltas = [new PhysicalEffectResourceDelta { MemberPersistentId = 7, ResourceName = "Fuel", DeltaMicroUnits = -1_250_000 }, new PhysicalEffectResourceDelta { MemberPersistentId = 8, ResourceName = "Fuel", DeltaMicroUnits = -750_000 }] };
        var shipment = new ActiveShipmentRecord { ShipmentId = "shipment-1", RouteId = route.RouteId, RouteVersion = route.Version, SourceDepotId = route.SourceDepotId, DestinationDepotId = route.DestinationDepotId, DepartureUt = 0, DueUt = 0, RemainingResources = route.Resources.Select(x => new ResourceAmount { ResourceName = x.ResourceName, AmountMicroUnits = x.AmountMicroUnits }).ToArray() };
        var dispatchHash = OperationIdentity.DispatchPayloadHash(shipment, debit); Assert.Equal(dispatchHash, OperationIdentity.DispatchPayloadHash(new ActiveShipmentRecord { ShipmentId = shipment.ShipmentId, RouteId = shipment.RouteId, RouteVersion = shipment.RouteVersion, SourceDepotId = shipment.SourceDepotId, DestinationDepotId = shipment.DestinationDepotId, DepartureUt = 999, DueUt = 1011.5, RemainingResources = shipment.RemainingResources }, debit));
        var dispatched = AcceptedStateCodec.Dispatch(state, OperationIdentity.Create(state.WorldId, 3, "dispatch"), "dispatch", 3, dispatchHash, state.Revision, AcceptedStateCodec.ComputeHash(state), shipment, debit, 1_000);
        Assert.Equal("accepted", dispatched.Outcome);
        var active = Assert.Single(dispatched.State.ActiveShipments); Assert.Equal(1_000, active.DepartureUt); Assert.Equal(1_012.5, active.DueUt);

        var credit = new PhysicalEffectIntent { EffectKind = "arrivalCredit", Capability = Capability("destination", 2, new string('b', 64), 9, [9]), MembershipRevision = 2, MemberPersistentIds = [9], Deltas = [new PhysicalEffectResourceDelta { MemberPersistentId = 9, ResourceName = "Fuel", DeltaMicroUnits = 1_000_000 }] };
        var credits = new[] { new ResourceAmount { ResourceName = "Fuel", AmountMicroUnits = 1_000_000 } };
        var remaining = new[] { new ResourceAmount { ResourceName = "Fuel", AmountMicroUnits = 1_000_000 } };
        var arrivalHash = OperationIdentity.ArrivalPayloadHash(active.ShipmentId, credits, remaining, credit);
        Assert.Equal("rejected", AcceptedStateCodec.Arrive(dispatched.State, OperationIdentity.Create(state.WorldId, 4, "early-arrival"), "early-arrival", 4, arrivalHash, dispatched.State.Revision, AcceptedStateCodec.ComputeHash(dispatched.State), active.ShipmentId, credits, remaining, credit, 1_012).Outcome);
        var arrived = AcceptedStateCodec.Arrive(dispatched.State, OperationIdentity.Create(state.WorldId, 4, "arrival"), "arrival", 4, arrivalHash, dispatched.State.Revision, AcceptedStateCodec.ComputeHash(dispatched.State), active.ShipmentId, credits, remaining, credit, 5_000);
        Assert.Equal("accepted", arrived.Outcome); Assert.Equal(1_000_000, Assert.Single(arrived.State.ActiveShipments).RemainingResources[0].AmountMicroUnits);
        Assert.Equal(5_000, arrived.State.Receipts[^1].AppliedUt);
    }

    [Fact]
    public void SameRouteSendOnceShipmentsKeepIndependentEtaAndArrivalIdentity()
    {
        var initial = LegacyState();
        var snapshot = new DepotRegistrySnapshot { RegistryVersion = "registry", Depots = [new DepotRecord { DepotId = "source", MembershipRevision = 1, MembershipHash = new string('a', 64) }, new DepotRecord { DepotId = "destination", MembershipRevision = 2, MembershipHash = new string('b', 64) }] };
        snapshot.RegistryHash = OperationIdentity.ComputeDepotRegistryHash(snapshot.Depots);
        var state = AcceptedStateCodec.SyncDepots(initial, OperationIdentity.Create(initial.WorldId, 1, "sync"), "sync", 1, OperationIdentity.DepotRegistryPayloadHash(snapshot), initial.Revision, AcceptedStateCodec.ComputeHash(initial), snapshot, 10).State;
        var route = Route();
        state = AcceptedStateCodec.UpsertRoute(state, OperationIdentity.Create(state.WorldId, 2, "route"), "route", 2, OperationIdentity.RouteVersionPayloadHash(route), state.Revision, AcceptedStateCodec.ComputeHash(state), route, 11).State;

        ActiveShipmentRecord Template(string id) => new() { ShipmentId = id, RouteId = route.RouteId, RouteVersion = route.Version, SourceDepotId = route.SourceDepotId, DestinationDepotId = route.DestinationDepotId, RemainingResources = route.Resources.Select(x => new ResourceAmount { ResourceName = x.ResourceName, AmountMicroUnits = x.AmountMicroUnits }).ToArray() };
        PhysicalEffectIntent Debit() => new() { EffectKind = "dispatchDebit", Capability = Capability("source", 1, new string('a', 64), 7, [7]), MembershipRevision = 1, MemberPersistentIds = [7], Deltas = [new PhysicalEffectResourceDelta { MemberPersistentId = 7, ResourceName = "Fuel", DeltaMicroUnits = -2_000_000 }] };

        StateTransitionResult Dispatch(string id, long sequence, double appliedUt)
        {
            var template = Template(id); var intent = Debit(); var request = "send-" + id;
            var result = AcceptedStateCodec.Dispatch(state, OperationIdentity.Create(state.WorldId, sequence, request), request, sequence, OperationIdentity.DispatchPayloadHash(template, intent), state.Revision, AcceptedStateCodec.ComputeHash(state), template, intent, appliedUt);
            Assert.Equal("accepted", result.Outcome);
            return result;
        }

        var first = Dispatch("shipment-1", 3, 100);
        state = first.State;
        var second = Dispatch("shipment-2", 4, 113);
        state = second.State;
        Assert.Equal(new[] { "shipment-1", "shipment-2" }, state.ActiveShipments.Select(x => x.ShipmentId));
        Assert.Equal(new[] { 100d, 113d }, state.ActiveShipments.Select(x => x.DepartureUt));
        Assert.Equal(new[] { 112.5, 125.5 }, state.ActiveShipments.Select(x => x.DueUt));

        var earlyCreditIntent = new PhysicalEffectIntent { EffectKind = "arrivalCredit", Capability = Capability("destination", 2, new string('b', 64), 9, [9]), MembershipRevision = 2, MemberPersistentIds = [9], Deltas = [new PhysicalEffectResourceDelta { MemberPersistentId = 9, ResourceName = "Fuel", DeltaMicroUnits = 2_000_000 }] };
        var credits = new[] { new ResourceAmount { ResourceName = "Fuel", AmountMicroUnits = 2_000_000 } };
        var arrivalHash = OperationIdentity.ArrivalPayloadHash("shipment-1", credits, [], earlyCreditIntent);
        Assert.Equal("rejected", AcceptedStateCodec.Arrive(state, OperationIdentity.Create(state.WorldId, 5, "arrive-early"), "arrive-early", 5, arrivalHash, state.Revision, AcceptedStateCodec.ComputeHash(state), "shipment-1", credits, [], earlyCreditIntent, 112).Outcome);

        var arrivedFirst = AcceptedStateCodec.Arrive(state, OperationIdentity.Create(state.WorldId, 5, "arrive-first"), "arrive-first", 5, arrivalHash, state.Revision, AcceptedStateCodec.ComputeHash(state), "shipment-1", credits, [], earlyCreditIntent, 120);
        Assert.Equal("accepted", arrivedFirst.Outcome);
        Assert.Equal("shipment-2", Assert.Single(arrivedFirst.State.ActiveShipments).ShipmentId);
        Assert.Equal(125.5, Assert.Single(arrivedFirst.State.ActiveShipments).DueUt);
        var secondHash = OperationIdentity.ArrivalPayloadHash("shipment-2", credits, [], earlyCreditIntent);
        Assert.Equal("rejected", AcceptedStateCodec.Arrive(arrivedFirst.State, OperationIdentity.Create(state.WorldId, 6, "arrive-second-early"), "arrive-second-early", 6, secondHash, arrivedFirst.State.Revision, AcceptedStateCodec.ComputeHash(arrivedFirst.State), "shipment-2", credits, [], earlyCreditIntent, 125).Outcome);
        var arrivedSecond = AcceptedStateCodec.Arrive(arrivedFirst.State, OperationIdentity.Create(state.WorldId, 6, "arrive-second"), "arrive-second", 6, secondHash, arrivedFirst.State.Revision, AcceptedStateCodec.ComputeHash(arrivedFirst.State), "shipment-2", credits, [], earlyCreditIntent, 126);
        Assert.Equal("accepted", arrivedSecond.Outcome);
        Assert.Empty(arrivedSecond.State.ActiveShipments);
    }

    [Fact]
    public void DispatchHoldsIfDestinationMembershipChangedAfterRouteWasDefined()
    {
        var state = LegacyState(); var snapshot = new DepotRegistrySnapshot { RegistryVersion = "registry", Depots = [new DepotRecord { DepotId = "source", MembershipRevision = 1, MembershipHash = new string('a', 64) }, new DepotRecord { DepotId = "destination", MembershipRevision = 2, MembershipHash = new string('b', 64) }] }; snapshot.RegistryHash = OperationIdentity.ComputeDepotRegistryHash(snapshot.Depots);
        state = AcceptedStateCodec.SyncDepots(state, OperationIdentity.Create(state.WorldId, 1, "sync"), "sync", 1, OperationIdentity.DepotRegistryPayloadHash(snapshot), state.Revision, AcceptedStateCodec.ComputeHash(state), snapshot, 1).State;
        var route = Route(); state = AcceptedStateCodec.UpsertRoute(state, OperationIdentity.Create(state.WorldId, 2, "route"), "route", 2, OperationIdentity.RouteVersionPayloadHash(route), state.Revision, AcceptedStateCodec.ComputeHash(state), route, 2).State;
        var changed = new DepotRegistrySnapshot { RegistryVersion = "registry-2", Depots = [new DepotRecord { DepotId = "source", MembershipRevision = 1, MembershipHash = new string('a', 64) }, new DepotRecord { DepotId = "destination", MembershipRevision = 3, MembershipHash = new string('c', 64) }] }; changed.RegistryHash = OperationIdentity.ComputeDepotRegistryHash(changed.Depots);
        state = AcceptedStateCodec.SyncDepots(state, OperationIdentity.Create(state.WorldId, 3, "sync-2"), "sync-2", 3, OperationIdentity.DepotRegistryPayloadHash(changed), state.Revision, AcceptedStateCodec.ComputeHash(state), changed, 3).State;
        var intent = new PhysicalEffectIntent { EffectKind = "dispatchDebit", Capability = Capability("source", 1, new string('a', 64), 7, [7]), MembershipRevision = 1, MemberPersistentIds = [7], Deltas = [new PhysicalEffectResourceDelta { MemberPersistentId = 7, ResourceName = "Fuel", DeltaMicroUnits = -2_000_000 }] };
        var template = new ActiveShipmentRecord { ShipmentId = "s", RouteId = "route", RouteVersion = 1, SourceDepotId = "source", DestinationDepotId = "destination", RemainingResources = [new ResourceAmount { ResourceName = "Fuel", AmountMicroUnits = 2_000_000 }] };
        var request = "dispatch"; var hash = OperationIdentity.DispatchPayloadHash(template, intent);
        var result = AcceptedStateCodec.Dispatch(state, OperationIdentity.Create(state.WorldId, 4, request), request, 4, hash, state.Revision, AcceptedStateCodec.ComputeHash(state), template, intent, 4);
        Assert.Equal("held", result.Outcome); Assert.Empty(result.State.ActiveShipments);
    }

    static InventoryCapability Capability(string depot, long revision, string membershipHash, uint anchor, uint[] members) => new()
    {
        DepotId = depot, MembershipRevision = revision, MembershipHash = membershipHash, AnchorPersistentId = anchor, MemberSetHash = OperationIdentity.ComputeMemberSetHash(anchor, members), ProviderId = "fixture", ProviderVersion = "1", Scene = "Flight", ObservationAvailable = true, ReadSupported = true, WriteSupported = true, SynchronousRollbackSupported = true, PersistenceSyncSupported = true
    };
}
