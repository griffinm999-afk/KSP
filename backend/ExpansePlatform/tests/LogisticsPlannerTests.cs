using Expanse.Domain;

namespace Expanse.Clock.Tests;

public sealed class LogisticsPlannerTests
{
    static RouteManifest Route() => new()
    {
        RouteId = "route-a", Version = 3, SourceDepotId = "depot-a", DestinationDepotId = "depot-b",
        TravelDurationSeconds = 20, Provenance = "user-entered static transport profile",
        Resources = [new ResourceAmount { ResourceName = "Fuel", AmountMicroUnits = 8_000_000 }, new ResourceAmount { ResourceName = "Oxidizer", AmountMicroUnits = 4_000_000 }]
    };

    static StockObservation Stock(string depot, params StockAmount[] rows) => new()
    {
        DepotId = depot, Available = true, MicroUnitProjectionSafe = true, Resources = rows
    };

    [Fact]
    public void DispatchDebitsAllManifestRowsOnceAndCreatesDueCargoIntent()
    {
        var route = Route();
        var result = LogisticsPlanner.PlanDispatch(route, Stock("depot-a",
            new StockAmount { ResourceName = "Fuel", AmountMicroUnits = 10_000_000, CapacityMicroUnits = 10_000_000 },
            new StockAmount { ResourceName = "Oxidizer", AmountMicroUnits = 4_000_000, CapacityMicroUnits = 4_000_000 }), "shipment-1", 100);

        Assert.Equal("dispatch", result.Outcome);
        Assert.Equal(100, result.DepartureUt);
        Assert.Equal(120, result.DueUt);
        Assert.Equal(route.Resources.Select(x => x.AmountMicroUnits), result.Debits.Select(x => x.AmountMicroUnits));
        Assert.Equal("user-entered static transport profile", route.Provenance);
        Assert.Equal("shipment-1", result.Shipment!.ShipmentId);
        Assert.Equal(result.Debits.Select(x => x.AmountMicroUnits), result.Shipment.RemainingResources.Select(x => x.AmountMicroUnits));
    }

    [Fact]
    public void DispatchHoldsWithoutPartialDebitWhenOneResourceIsShortOrObservationUnsafe()
    {
        var source = Stock("depot-a",
            new StockAmount { ResourceName = "Fuel", AmountMicroUnits = 9_000_000, CapacityMicroUnits = 9_000_000 },
            new StockAmount { ResourceName = "Oxidizer", AmountMicroUnits = 3_999_999, CapacityMicroUnits = 4_000_000 });
        var shortResult = LogisticsPlanner.PlanDispatch(Route(), source, "shipment-1", 100);
        Assert.Equal("held", shortResult.Outcome);
        Assert.Empty(shortResult.Debits);
        Assert.Null(shortResult.Shipment);

        source.MicroUnitProjectionSafe = false;
        var unsafeResult = LogisticsPlanner.PlanDispatch(Route(), source, "shipment-1", 100);
        Assert.Equal("held", unsafeResult.Outcome);
        Assert.Contains("cannot safely project", unsafeResult.Reason);
    }

    [Fact]
    public void ArrivalWaitsUntilDueThenCreditsOnlyCapacityAndRetainsResidualCargo()
    {
        var dispatched = LogisticsPlanner.PlanDispatch(Route(), Stock("depot-a",
            new StockAmount { ResourceName = "Fuel", AmountMicroUnits = 8_000_000, CapacityMicroUnits = 8_000_000 },
            new StockAmount { ResourceName = "Oxidizer", AmountMicroUnits = 4_000_000, CapacityMicroUnits = 4_000_000 }), "shipment-1", 100);
        var destination = Stock("depot-b",
            new StockAmount { ResourceName = "Fuel", AmountMicroUnits = 9_000_000, CapacityMicroUnits = 12_000_000 },
            new StockAmount { ResourceName = "Oxidizer", AmountMicroUnits = 0, CapacityMicroUnits = 4_000_000 });

        var early = LogisticsPlanner.PlanArrival(dispatched.Shipment!, destination, 119);
        Assert.Equal("held", early.Outcome);
        Assert.Contains("not due", early.Reason);
        Assert.Empty(early.Credits);

        var due = LogisticsPlanner.PlanArrival(dispatched.Shipment!, destination, 120);
        Assert.Equal("partial", due.Outcome);
        Assert.Equal(new long[] { 3_000_000, 4_000_000 }, due.Credits.Select(x => x.AmountMicroUnits));
        Assert.Equal(new long[] { 5_000_000 }, due.RemainingCargo.Select(x => x.AmountMicroUnits));
        Assert.False(due.Complete);

        destination.Resources[0].AmountMicroUnits = 4_000_000;
        destination.Resources[1].CapacityMicroUnits = 4_000_000;
        var full = LogisticsPlanner.PlanArrival(dispatched.Shipment!, destination, 120);
        Assert.Equal("arrived", full.Outcome);
        Assert.True(full.Complete);
        Assert.Empty(full.RemainingCargo);
    }

    [Fact]
    public void ArrivalForMissingUnavailableOrMismatchedDestinationKeepsCargoHeld()
    {
        var cargo = new CargoManifest
        {
            ShipmentId = "s", RouteId = "r", RouteVersion = 1, SourceDepotId = "a", DestinationDepotId = "b",
            DepartureUt = 1, DueUt = 2, RemainingResources = [new ResourceAmount { ResourceName = "Fuel", AmountMicroUnits = 7 }]
        };
        var missing = LogisticsPlanner.PlanArrival(cargo, Stock("b"), 2);
        Assert.Equal("held", missing.Outcome);
        Assert.Equal(7, Assert.Single(missing.RemainingCargo).AmountMicroUnits);
        var unavailable = Stock("b"); unavailable.Available = false; unavailable.UnavailableReason = "BRP endpoint not synchronized";
        var held = LogisticsPlanner.PlanArrival(cargo, unavailable, 2);
        Assert.Equal("BRP endpoint not synchronized", held.Reason);
        Assert.Equal("held", LogisticsPlanner.PlanArrival(cargo, Stock("other"), 2).Outcome);

        cargo.RemainingResources =
        [
            new ResourceAmount { ResourceName = "Fuel", AmountMicroUnits = 7 },
            new ResourceAmount { ResourceName = "Oxidizer", AmountMicroUnits = 3 }
        ];
        var partialObservation = Stock("b", new StockAmount { ResourceName = "Fuel", AmountMicroUnits = 0, CapacityMicroUnits = 10 });
        var atomicHold = LogisticsPlanner.PlanArrival(cargo, partialObservation, 2);
        Assert.Equal("held", atomicHold.Outcome);
        Assert.Empty(atomicHold.Credits);
        Assert.Equal(new long[] { 7, 3 }, atomicHold.RemainingCargo.Select(x => x.AmountMicroUnits));
    }

    [Fact]
    public void RepeatCoalescesBlockedSlotsAndDoesBoundedWorkAcrossHugeUtJump()
    {
        var schedule = new RepeatSchedule { NextDueUt = 10, WaitingRequest = true, WaitingScheduledUt = 10, WaitingCoalescedSlots = 1 };
        var blocked = LogisticsPlanner.PlanRepeat(schedule, intervalSeconds: 10, targetUt: 10_000_000_000, routeAvailable: false, holdReason: "destination unavailable");
        Assert.Equal("waiting", blocked.Outcome);
        Assert.Equal("destination unavailable", blocked.Reason);
        Assert.Equal(1_000_000_001, blocked.CoalescedSlots);
        Assert.Equal(10, blocked.ScheduledUt);
        Assert.True(blocked.Next.NextDueUt > 10_000_000_000);

        var ready = LogisticsPlanner.PlanRepeat(blocked.Next, 10, 10_000_000_001, true);
        Assert.Equal("dispatch", ready.Outcome);
        Assert.Equal(10, ready.ScheduledUt);
        Assert.Equal(0, ready.Next.WaitingCoalescedSlots);
        Assert.Equal(10_000_000_001, ready.ProcessedUt);
        Assert.Equal(blocked.Next.NextDueUt, ready.Next.NextDueUt);
    }

    [Fact]
    public void RepeatDoesNotCreatePastDeparturesAndHoldsInvalidOrOverflowingSchedules()
    {
        var notDue = LogisticsPlanner.PlanRepeat(new RepeatSchedule { NextDueUt = 25 }, 10, 24, true);
        Assert.Equal("notDue", notDue.Outcome);
        var invalid = LogisticsPlanner.PlanRepeat(new RepeatSchedule { NextDueUt = 1 }, 0, 100, true);
        Assert.Equal("held", invalid.Outcome);
        var overflow = LogisticsPlanner.PlanRepeat(new RepeatSchedule { NextDueUt = 1e300 }, 1e200, 1e300, true);
        Assert.Equal("held", overflow.Outcome);
        var countOverflow = LogisticsPlanner.PlanRepeat(new RepeatSchedule { NextDueUt = 1, WaitingRequest = true, WaitingScheduledUt = 1, WaitingCoalescedSlots = long.MaxValue }, 1, 1, false);
        Assert.Equal("held", countOverflow.Outcome);
    }

    [Fact]
    public void KeepStockCountsStockAndInboundExactlyOnceAndRequestsBoundedShortfall()
    {
        var stock = Stock("depot-b", new StockAmount { ResourceName = "Fuel", AmountMicroUnits = 25, CapacityMicroUnits = 100 });
        var shipment = Inbound("inbound-1", 20);
        var inbound = new[] { shipment };
        var below = LogisticsPlanner.PlanKeepStock("Fuel", lowTriggerMicroUnits: 50, targetMicroUnits: 100, batchSizeMicroUnits: 30, stock, inbound);
        Assert.Equal("dispatch", below.Outcome);
        Assert.Equal(45, below.ProjectedStockMicroUnits);
        Assert.Equal(30, below.RequestedDispatchMicroUnits);

        inbound = new[] { Inbound("inbound-2", 30) };
        var enough = LogisticsPlanner.PlanKeepStock("Fuel", 50, 100, 30, stock, inbound);
        Assert.Equal("noDispatch", enough.Outcome);
        Assert.Equal(55, enough.ProjectedStockMicroUnits);
        Assert.Equal(0, enough.RequestedDispatchMicroUnits);
    }

    [Fact]
    public void KeepStockStopsAtLowTriggerAfterOneBatchInsteadOfFillingTarget()
    {
        var destination = Stock("depot-b", new StockAmount { ResourceName = "LF", AmountMicroUnits = 400_000_000, CapacityMicroUnits = 405_000_000 });
        var first = LogisticsPlanner.PlanKeepStock("LF", lowTriggerMicroUnits: 401_000_000, targetMicroUnits: 405_000_000, batchSizeMicroUnits: 1_000_000, destination, Array.Empty<CargoManifest>());
        Assert.Equal("dispatch", first.Outcome);
        Assert.Equal(400_000_000, first.ProjectedStockMicroUnits);
        Assert.Equal(1_000_000, first.RequestedDispatchMicroUnits);

        var inbound = new CargoManifest { ShipmentId = "one-in-flight", RouteId = "route", RouteVersion = 1, SourceDepotId = "depot-a", DestinationDepotId = "depot-b", DepartureUt = 1, DueUt = 31, RemainingResources = [new ResourceAmount { ResourceName = "LF", AmountMicroUnits = 1_000_000 }] };
        var afterAcceptedDispatch = LogisticsPlanner.PlanKeepStock("LF", lowTriggerMicroUnits: 401_000_000, targetMicroUnits: 405_000_000, batchSizeMicroUnits: 1_000_000, destination, [inbound]);
        Assert.Equal("noDispatch", afterAcceptedDispatch.Outcome);
        Assert.Equal(401_000_000, afterAcceptedDispatch.ProjectedStockMicroUnits);
        Assert.Equal(0, afterAcceptedDispatch.RequestedDispatchMicroUnits);
    }

    [Fact]
    public void KeepFullOrdersOnlyTheRemainingDeficitAfterInboundCargo()
    {
        var destination = Stock("depot-b", new StockAmount { ResourceName = "Fuel", AmountMicroUnits = 700, CapacityMicroUnits = 1000 });
        var inbound = Inbound("fuel-already-coming", 100);
        var decision = LogisticsPlanner.PlanKeepStock("Fuel", lowTriggerMicroUnits: 1000, targetMicroUnits: 1000, batchSizeMicroUnits: 500, destination, [inbound]);
        Assert.Equal("dispatch", decision.Outcome);
        Assert.Equal(800, decision.ProjectedStockMicroUnits);
        Assert.Equal(200, decision.RequestedDispatchMicroUnits);
        var covered = LogisticsPlanner.PlanKeepStock("Fuel", 1000, 1000, 500, destination, [Inbound("enough-coming", 300)]);
        Assert.Equal("noDispatch", covered.Outcome);
    }

    [Fact]
    public void KeepFullProjectsEveryDistinctConcurrentShipmentOnTheSameRoute()
    {
        var destination = Stock("depot-b", new StockAmount { ResourceName = "Fuel", AmountMicroUnits = 700, CapacityMicroUnits = 1000 });
        var first = Inbound("same-route-shipment-1", 100);
        var second = Inbound("same-route-shipment-2", 200);

        var decision = LogisticsPlanner.PlanKeepStock("Fuel", lowTriggerMicroUnits: 1000, targetMicroUnits: 1000, batchSizeMicroUnits: 500, destination, [first, second]);

        Assert.Equal("noDispatch", decision.Outcome);
        Assert.Equal(1000, decision.ProjectedStockMicroUnits);
        Assert.Equal(0, decision.RequestedDispatchMicroUnits);
    }

    [Fact]
    public void KeepStockDeduplicatesShipmentIdentityAndHoldsConflictingCopies()
    {
        var stock = Stock("depot-b", new StockAmount { ResourceName = "Fuel", AmountMicroUnits = 1, CapacityMicroUnits = 10 });
        var inbound = Inbound("same-shipment", 3);
        var decision = LogisticsPlanner.PlanKeepStock("Fuel", 5, 10, 10, stock, [inbound, Inbound("same-shipment", 3)]);
        Assert.Equal(4, decision.ProjectedStockMicroUnits);
        Assert.Equal("dispatch", decision.Outcome);

        var conflict = LogisticsPlanner.PlanKeepStock("Fuel", 5, 10, 10, stock, [inbound, Inbound("same-shipment", 4)]);
        Assert.Equal("held", conflict.Outcome);
        Assert.Contains("Conflicting inbound entries", conflict.Reason);
    }

    [Fact]
    public void KeepStockHoldsWhenObservationUnavailableOrInboundOverflows()
    {
        var stock = Stock("depot-b", new StockAmount { ResourceName = "Fuel", AmountMicroUnits = 1, CapacityMicroUnits = 10 });
        stock.Available = false; stock.UnavailableReason = "inventory loading";
        Assert.Equal("inventory loading", LogisticsPlanner.PlanKeepStock("Fuel", 2, 4, 1, stock, null).Reason);
        stock.Available = true;
        var overflow = LogisticsPlanner.PlanKeepStock("Fuel", 2, 4, 1, stock,
            [Inbound("overflow", long.MaxValue)]);
        Assert.Equal("held", overflow.Outcome);
        Assert.Contains("overflow", overflow.Reason);
    }

    static CargoManifest Inbound(string id, long amount) => new()
    {
        ShipmentId = id, RouteId = "route", RouteVersion = 1, SourceDepotId = "source", DestinationDepotId = "depot-b",
        DepartureUt = 1, DueUt = 2, RemainingResources = [new ResourceAmount { ResourceName = "Fuel", AmountMicroUnits = amount }]
    };
}
