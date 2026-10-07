using System;
using System.Collections.Generic;
using System.Linq;

namespace Expanse.Domain
{
    /// <summary>
    /// Pure planning DTOs for virtual deliveries. Stock values are provider-authored micro-units;
    /// callers may expose only conservatively floored usable stock/capacity. This planner never converts
    /// or rounds KSP tank doubles; the gateway re-reads actual doubles at effect time.
    /// </summary>
    public sealed class RouteManifest
    {
        public string RouteId { get; set; } = "";
        public long Version { get; set; }
        public string SourceDepotId { get; set; } = "";
        public string DestinationDepotId { get; set; } = "";
        public double TravelDurationSeconds { get; set; }
        public string Provenance { get; set; } = "";
        public ResourceAmount[] Resources { get; set; } = Array.Empty<ResourceAmount>();
    }

    public sealed class ResourceAmount
    {
        public string ResourceName { get; set; } = "";
        public long AmountMicroUnits { get; set; }
    }

    public sealed class StockAmount
    {
        public string ResourceName { get; set; } = "";
        public long AmountMicroUnits { get; set; }
        public long CapacityMicroUnits { get; set; }
        /// <summary>True only when the observer proves this tank can supply a debit. Null is legacy or unknown metadata.</summary>
        public bool? DebitAllowed { get; set; }
    }

    public sealed class StockObservation
    {
        public string DepotId { get; set; } = "";
        public Guid SessionId { get; set; }
        public Guid LoadEpoch { get; set; }
        public string WorldId { get; set; } = "";
        public string RegistryHash { get; set; } = "";
        public long MembershipRevision { get; set; }
        public string MembershipHash { get; set; } = "";
        public long ObservationRevision { get; set; }
        public double ObservedUt { get; set; }
        public double AgeSeconds { get; set; }
        public MemberStockObservation[] MemberStocks { get; set; } = Array.Empty<MemberStockObservation>();
        public bool Available { get; set; }
        public bool MicroUnitProjectionSafe { get; set; }
        public string UnavailableReason { get; set; } = "";
        public StockAmount[] Resources { get; set; } = Array.Empty<StockAmount>();
    }

    public sealed class MemberStockObservation
    {
        public uint MemberPersistentId { get; set; }
        public StockAmount[] Resources { get; set; } = Array.Empty<StockAmount>();
    }

    public sealed class CargoManifest
    {
        public string ShipmentId { get; set; } = "";
        public string RouteId { get; set; } = "";
        public long RouteVersion { get; set; }
        public string SourceDepotId { get; set; } = "";
        public string DestinationDepotId { get; set; } = "";
        public double DepartureUt { get; set; }
        public double DueUt { get; set; }
        public ResourceAmount[] RemainingResources { get; set; } = Array.Empty<ResourceAmount>();
    }

    public sealed class DispatchDecision
    {
        public string Outcome { get; set; } = "held";
        public string Reason { get; set; } = "";
        public double DepartureUt { get; set; }
        public double DueUt { get; set; }
        public ResourceAmount[] Debits { get; set; } = Array.Empty<ResourceAmount>();
        public CargoManifest? Shipment { get; set; }
    }

    public sealed class ArrivalDecision
    {
        public string Outcome { get; set; } = "held";
        public string Reason { get; set; } = "";
        public ResourceAmount[] Credits { get; set; } = Array.Empty<ResourceAmount>();
        public ResourceAmount[] RemainingCargo { get; set; } = Array.Empty<ResourceAmount>();
        public bool Complete => Outcome == "arrived";
    }

    public sealed class RepeatSchedule
    {
        public double NextDueUt { get; set; }
        public bool WaitingRequest { get; set; }
        public double WaitingScheduledUt { get; set; }
        public long WaitingCoalescedSlots { get; set; }
    }

    public sealed class RepeatDecision
    {
        public string Outcome { get; set; } = "notDue";
        public string Reason { get; set; } = "";
        public RepeatSchedule Next { get; set; } = new RepeatSchedule();
        public double ScheduledUt { get; set; }
        public double ProcessedUt { get; set; }
        public long CoalescedSlots { get; set; }
    }

    public sealed class KeepStockDecision
    {
        public string Outcome { get; set; } = "noDispatch";
        public string Reason { get; set; } = "";
        public long ProjectedStockMicroUnits { get; set; }
        public long RequestedDispatchMicroUnits { get; set; }
    }

    /// <summary>Bounded deterministic planning functions; no clock, provider, IO or mutable global state.</summary>
    public static class LogisticsPlanner
    {
        public const int MaxManifestResources = 32;
        public const int MaxObservationResources = 64;
        public const int MaxIdentityLength = 128;
        public const int MaxProvenanceLength = 256;

        public static DispatchDecision PlanDispatch(RouteManifest route, StockObservation source, string shipmentId, double actualDepartureUt)
        {
            var invalid = ValidateRoute(route);
            if (invalid != null) return Held(invalid);
            if (!ValidId(shipmentId)) return Held("Shipment identity is missing or too long.");
            if (!FiniteNonNegative(actualDepartureUt)) return Held("Actual departure UT is invalid.");
            if (source == null || source.DepotId != route.SourceDepotId) return Held("Source observation does not match this route.");
            invalid = ValidateObservation(source);
            if (invalid != null) return Held(invalid);
            if (!source.Available) return Held(Reason(source, "Source inventory is unavailable."));
            if (!source.MicroUnitProjectionSafe) return Held("Source stock range/provider cannot safely project usable stock to ledger micro-units.");

            var debits = new ResourceAmount[route.Resources.Length];
            for (var i = 0; i < route.Resources.Length; i++)
            {
                var need = route.Resources[i];
                var found = FindStock(source.Resources, need.ResourceName);
                if (found == null) return Held("Source is missing resource " + need.ResourceName + ".");
                if (found.AmountMicroUnits < need.AmountMicroUnits) return Held("Insufficient source stock for " + need.ResourceName + ".");
                debits[i] = Copy(need);
            }

            var dueUt = actualDepartureUt + route.TravelDurationSeconds;
            if (!FiniteNonNegative(dueUt) || dueUt <= actualDepartureUt) return Held("Route due UT overflowed or did not advance.");
            return new DispatchDecision
            {
                Outcome = "dispatch",
                Reason = "Source debit and in-transit cargo must be accepted atomically.",
                DepartureUt = actualDepartureUt,
                DueUt = dueUt,
                Debits = debits,
                Shipment = new CargoManifest
                {
                    ShipmentId = shipmentId,
                    RouteId = route.RouteId,
                    RouteVersion = route.Version,
                    SourceDepotId = route.SourceDepotId,
                    DestinationDepotId = route.DestinationDepotId,
                    DepartureUt = actualDepartureUt,
                    DueUt = dueUt,
                    RemainingResources = CloneResources(route.Resources)
                }
            };
        }

        public static ArrivalDecision PlanArrival(CargoManifest shipment, StockObservation destination, double targetUt)
        {
            var invalid = ValidateShipment(shipment);
            if (invalid != null) return ArrivalHeld(invalid, shipment?.RemainingResources);
            invalid = ValidateObservation(destination);
            if (invalid != null) return ArrivalHeld(invalid, shipment.RemainingResources);
            if (destination.DepotId != shipment.DestinationDepotId) return ArrivalHeld("Destination observation does not match this shipment.", shipment.RemainingResources);
            if (!destination.Available) return ArrivalHeld(Reason(destination, "Destination inventory is unavailable."), shipment.RemainingResources);
            if (!destination.MicroUnitProjectionSafe) return ArrivalHeld("Destination range/provider cannot safely project usable capacity to ledger micro-units.", shipment.RemainingResources);
            if (!FiniteNonNegative(targetUt)) return ArrivalHeld("Arrival target UT is invalid.", shipment.RemainingResources);
            if (targetUt < shipment.DueUt) return ArrivalHeld("Shipment is not due yet.", shipment.RemainingResources);

            var credits = new List<ResourceAmount>();
            var remaining = new List<ResourceAmount>();
            foreach (var cargo in shipment.RemainingResources)
            {
                var stock = FindStock(destination.Resources, cargo.ResourceName);
                if (stock == null) return ArrivalHeld("Destination is missing resource " + cargo.ResourceName + ".", shipment.RemainingResources);
                var free = stock.CapacityMicroUnits - stock.AmountMicroUnits;
                var credit = Math.Min(cargo.AmountMicroUnits, Math.Max(0, free));
                if (credit > 0) credits.Add(new ResourceAmount { ResourceName = cargo.ResourceName, AmountMicroUnits = credit });
                var residual = cargo.AmountMicroUnits - credit;
                if (residual > 0) remaining.Add(new ResourceAmount { ResourceName = cargo.ResourceName, AmountMicroUnits = residual });
            }
            if (remaining.Count == 0)
                return new ArrivalDecision { Outcome = "arrived", Reason = "All due cargo fits at the destination.", Credits = credits.ToArray(), RemainingCargo = Array.Empty<ResourceAmount>() };
            if (credits.Count == 0)
                return new ArrivalDecision { Outcome = "held", Reason = "Destination has no capacity for due cargo; cargo remains in transit.", Credits = Array.Empty<ResourceAmount>(), RemainingCargo = remaining.ToArray() };
            return new ArrivalDecision { Outcome = "partial", Reason = "Destination accepted available capacity; residual cargo remains in transit.", Credits = credits.ToArray(), RemainingCargo = remaining.ToArray() };
        }

        /// <summary>
        /// Evaluates a repeating rule with O(1) arithmetic regardless of the UT jump. A blocked rule
        /// retains one waiting request and coalesces all elapsed slots into its bounded count.
        /// </summary>
        public static RepeatDecision PlanRepeat(RepeatSchedule schedule, double intervalSeconds, double targetUt, bool routeAvailable, string? holdReason = null)
        {
            if (schedule == null || !FiniteNonNegative(schedule.NextDueUt) || !FinitePositive(intervalSeconds) || !FiniteNonNegative(targetUt) || schedule.WaitingCoalescedSlots < 0 || (schedule.WaitingRequest && !FiniteNonNegative(schedule.WaitingScheduledUt)))
                return new RepeatDecision { Outcome = "held", Reason = "Repeat schedule, interval, or target UT is invalid.", Next = Clone(schedule) };
            var next = Clone(schedule);
            if (targetUt < schedule.NextDueUt)
            {
                if (schedule.WaitingRequest && routeAvailable)
                {
                    next.WaitingRequest = false;
                    next.WaitingScheduledUt = 0;
                    next.WaitingCoalescedSlots = 0;
                    return new RepeatDecision { Outcome = "dispatch", Reason = "Previously coalesced request is now eligible; departure uses current trustworthy UT.", Next = next, ScheduledUt = schedule.WaitingScheduledUt, ProcessedUt = targetUt, CoalescedSlots = schedule.WaitingCoalescedSlots };
                }
                return new RepeatDecision { Outcome = schedule.WaitingRequest ? "waiting" : "notDue", Reason = schedule.WaitingRequest ? "A prior due request remains coalesced." : "Next rule slot is not due.", Next = next };
            }

            var elapsed = targetUt - schedule.NextDueUt;
            var rawSlots = Math.Floor(elapsed / intervalSeconds) + 1d;
            if (Double.IsNaN(rawSlots) || Double.IsInfinity(rawSlots) || rawSlots < 1d || rawSlots > Int64.MaxValue)
                return new RepeatDecision { Outcome = "held", Reason = "Missed-slot count exceeds bounded integer range.", Next = next };
            var dueSlots = (long)rawSlots;
            var advance = dueSlots * intervalSeconds;
            var advancedUt = schedule.NextDueUt + advance;
            // Floating-point rounding can leave the due boundary behind target; correct once, never loop.
            if (advancedUt <= targetUt)
            {
                if (dueSlots == Int64.MaxValue) return new RepeatDecision { Outcome = "held", Reason = "Next rule slot overflowed.", Next = next };
                dueSlots++;
                advancedUt = schedule.NextDueUt + dueSlots * intervalSeconds;
            }
            if (!FiniteNonNegative(advancedUt) || advancedUt <= targetUt) return new RepeatDecision { Outcome = "held", Reason = "Next rule slot overflowed.", Next = next };

            var first = schedule.WaitingRequest ? schedule.WaitingScheduledUt : schedule.NextDueUt;
            if (schedule.WaitingCoalescedSlots > Int64.MaxValue - dueSlots)
                return new RepeatDecision { Outcome = "held", Reason = "Coalesced missed-slot count exceeds bounded integer range.", Next = next };
            var aggregate = schedule.WaitingCoalescedSlots + dueSlots;
            next.NextDueUt = advancedUt;
            if (!routeAvailable)
            {
                next.WaitingRequest = true;
                next.WaitingScheduledUt = first;
                next.WaitingCoalescedSlots = aggregate;
                return new RepeatDecision { Outcome = "waiting", Reason = String.IsNullOrWhiteSpace(holdReason) ? "Route or inventory is unavailable; missed slots are coalesced." : holdReason!, Next = next, ScheduledUt = first, ProcessedUt = targetUt, CoalescedSlots = aggregate };
            }
            next.WaitingRequest = false;
            next.WaitingScheduledUt = 0;
            next.WaitingCoalescedSlots = 0;
            return new RepeatDecision { Outcome = "dispatch", Reason = "One due request is eligible; departure uses current trustworthy UT.", Next = next, ScheduledUt = first, ProcessedUt = targetUt, CoalescedSlots = aggregate };
        }

        /// <summary>Counts stock and each inbound shipment once; only requests the shortfall to target above the low trigger.</summary>
        public static KeepStockDecision PlanKeepStock(string resourceName, long lowTriggerMicroUnits, long targetMicroUnits, long batchSizeMicroUnits, StockObservation stock, IEnumerable<CargoManifest>? inboundShipments)
        {
            // Equal low/target means "keep full": any deficit requests only the
            // missing amount, up to one route batch, after counting inbound cargo.
            if (!ValidId(resourceName) || lowTriggerMicroUnits < 0 || targetMicroUnits <= 0 || targetMicroUnits < lowTriggerMicroUnits || batchSizeMicroUnits <= 0)
                return new KeepStockDecision { Outcome = "held", Reason = "Keep-stock thresholds or resource identity are invalid." };
            var invalid = ValidateObservation(stock);
            if (invalid != null) return new KeepStockDecision { Outcome = "held", Reason = invalid };
            if (!stock.Available) return new KeepStockDecision { Outcome = "held", Reason = Reason(stock, "Current stock is unavailable.") };
            if (!stock.MicroUnitProjectionSafe) return new KeepStockDecision { Outcome = "held", Reason = "Current stock range/provider cannot safely project usable stock to ledger micro-units." };
            var current = FindStock(stock.Resources, resourceName);
            if (current == null) return new KeepStockDecision { Outcome = "held", Reason = "Stock observation is missing " + resourceName + "." };
            long inboundAmount = 0;
            if (inboundShipments != null)
            {
                var seen = new Dictionary<string, CargoManifest>(StringComparer.Ordinal);
                foreach (var shipment in inboundShipments)
                {
                    if (seen.Count >= MaxObservationResources) return new KeepStockDecision { Outcome = "held", Reason = "Inbound shipment list exceeds planner bound." };
                    var invalidShipment = ValidateShipment(shipment);
                    if (invalidShipment != null) return new KeepStockDecision { Outcome = "held", Reason = "Inbound shipment is invalid: " + invalidShipment };
                    if (seen.TryGetValue(shipment!.ShipmentId, out var prior))
                    {
                        if (!SameShipment(prior, shipment)) return new KeepStockDecision { Outcome = "held", Reason = "Conflicting inbound entries share shipment ID " + shipment.ShipmentId + "." };
                        continue; // A repeated view of the same accepted shipment contributes only once.
                    }
                    seen.Add(shipment.ShipmentId, shipment);
                    foreach (var item in shipment.RemainingResources)
                        if (String.Equals(item.ResourceName, resourceName, StringComparison.Ordinal))
                        {
                            if (inboundAmount > Int64.MaxValue - item.AmountMicroUnits) return new KeepStockDecision { Outcome = "held", Reason = "Inbound quantity overflowed." };
                            inboundAmount += item.AmountMicroUnits;
                        }
                }
            }
            if (current.AmountMicroUnits > Int64.MaxValue - inboundAmount) return new KeepStockDecision { Outcome = "held", Reason = "Projected stock overflowed." };
            var projected = current.AmountMicroUnits + inboundAmount;
            if (projected >= lowTriggerMicroUnits) return new KeepStockDecision { Outcome = "noDispatch", Reason = "Projected stock meets the low trigger.", ProjectedStockMicroUnits = projected };
            var shortfall = targetMicroUnits - projected;
            var request = Math.Min(batchSizeMicroUnits, shortfall);
            return new KeepStockDecision { Outcome = "dispatch", Reason = "One batch is requested from current stock plus inbound cargo.", ProjectedStockMicroUnits = projected, RequestedDispatchMicroUnits = request };
        }

        /// <summary>Exports one whole Ore batch only when the physical source retains its configured reserve.</summary>
        public static KeepStockDecision PlanExportStock(long reserveMicroUnits, StockObservation source) => PlanExportStock(reserveMicroUnits, source, OreExportPolicy.DefaultBatchMicroUnits);
        public static KeepStockDecision PlanExportStock(long reserveMicroUnits, StockObservation source, long batchMicroUnits)
        {
            if (!OreExportPolicy.IsValidBatch(batchMicroUnits) || reserveMicroUnits < 0 || reserveMicroUnits > Int64.MaxValue - batchMicroUnits)
                return new KeepStockDecision { Outcome = "held", Reason = "Source reserve is invalid or exceeds the stock range." };
            var invalid = ValidateObservation(source);
            if (invalid != null) return new KeepStockDecision { Outcome = "held", Reason = invalid };
            if (!source.Available || !source.MicroUnitProjectionSafe) return new KeepStockDecision { Outcome = "held", Reason = "Physical source Ore observation is unavailable or unsafe." };
            var ore = FindStock(source.Resources, "Ore");
            if (ore == null) return new KeepStockDecision { Outcome = "held", Reason = "Physical source has no Ore observation." };
            return new KeepStockDecision { Outcome = ore.AmountMicroUnits >= batchMicroUnits + reserveMicroUnits ? "dispatch" : "noDispatch", Reason = "One physical Ore batch requires stock above the retained source reserve.", ProjectedStockMicroUnits = ore.AmountMicroUnits, RequestedDispatchMicroUnits = ore.AmountMicroUnits >= batchMicroUnits + reserveMicroUnits ? batchMicroUnits : 0 };
        }

        static string? ValidateRoute(RouteManifest? route)
        {
            if (route == null || !ValidId(route.RouteId) || route.Version < 1 || !ValidId(route.SourceDepotId) || !ValidId(route.DestinationDepotId) || route.SourceDepotId == route.DestinationDepotId)
                return "Route identity or endpoints are invalid.";
            if (!FinitePositive(route.TravelDurationSeconds) || route.TravelDurationSeconds > 1e15) return "Route duration must be positive, finite and bounded.";
            if (String.IsNullOrWhiteSpace(route.Provenance) || route.Provenance.Length > MaxProvenanceLength) return "Route provenance is missing or too long.";
            if (route.Resources == null || route.Resources.Length == 0 || route.Resources.Length > MaxManifestResources) return "Route manifest is empty or exceeds the resource bound.";
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in route.Resources) if (row == null || !ValidId(row.ResourceName) || row.AmountMicroUnits <= 0 || !names.Add(row.ResourceName)) return "Route resource rows must have unique names and positive amounts.";
            return null;
        }

        static string? ValidateShipment(CargoManifest? shipment)
        {
            if (shipment == null || !ValidId(shipment.ShipmentId) || !ValidId(shipment.RouteId) || shipment.RouteVersion < 1 || !ValidId(shipment.SourceDepotId) || !ValidId(shipment.DestinationDepotId) || shipment.SourceDepotId == shipment.DestinationDepotId)
                return "Shipment identity or endpoints are invalid.";
            if (!FiniteNonNegative(shipment.DepartureUt) || !FiniteNonNegative(shipment.DueUt) || shipment.DueUt <= shipment.DepartureUt) return "Shipment departure/due UT is invalid.";
            if (shipment.RemainingResources == null || shipment.RemainingResources.Length == 0 || shipment.RemainingResources.Length > MaxManifestResources) return "Shipment cargo is empty or exceeds the resource bound.";
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in shipment.RemainingResources) if (row == null || !ValidId(row.ResourceName) || row.AmountMicroUnits <= 0 || !names.Add(row.ResourceName)) return "Shipment cargo rows are invalid.";
            return null;
        }

        static string? ValidateObservation(StockObservation? observation)
        {
            if (observation == null || !ValidId(observation.DepotId)) return "Stock observation identity is invalid.";
            if (observation.Resources == null || observation.Resources.Length > MaxObservationResources) return "Stock observation exceeds resource bound.";
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in observation.Resources)
                if (row == null || !ValidId(row.ResourceName) || row.AmountMicroUnits < 0 || row.CapacityMicroUnits < row.AmountMicroUnits || !names.Add(row.ResourceName)) return "Stock observation has invalid or duplicate rows.";
            return null;
        }

        static bool SameShipment(CargoManifest left, CargoManifest right)
        {
            if (left.RouteId != right.RouteId || left.RouteVersion != right.RouteVersion || left.SourceDepotId != right.SourceDepotId || left.DestinationDepotId != right.DestinationDepotId || left.DepartureUt != right.DepartureUt || left.DueUt != right.DueUt || left.RemainingResources.Length != right.RemainingResources.Length) return false;
            var leftRows = left.RemainingResources.OrderBy(x => x.ResourceName, StringComparer.Ordinal).ToArray();
            var rightRows = right.RemainingResources.OrderBy(x => x.ResourceName, StringComparer.Ordinal).ToArray();
            for (var i = 0; i < leftRows.Length; i++) if (leftRows[i].ResourceName != rightRows[i].ResourceName || leftRows[i].AmountMicroUnits != rightRows[i].AmountMicroUnits) return false;
            return true;
        }

        static DispatchDecision Held(string reason) => new DispatchDecision { Outcome = "held", Reason = reason };
        static ArrivalDecision ArrivalHeld(string reason, ResourceAmount[]? cargo) => new ArrivalDecision { Outcome = "held", Reason = reason, RemainingCargo = cargo == null ? Array.Empty<ResourceAmount>() : CloneResources(cargo) };
        static string Reason(StockObservation observation, string fallback) => String.IsNullOrWhiteSpace(observation.UnavailableReason) ? fallback : observation.UnavailableReason;
        static bool ValidId(string? value) => !String.IsNullOrWhiteSpace(value) && value!.Length <= MaxIdentityLength;
        static bool FinitePositive(double x) => x > 0 && !Double.IsNaN(x) && !Double.IsInfinity(x);
        static bool FiniteNonNegative(double x) => x >= 0 && !Double.IsNaN(x) && !Double.IsInfinity(x);
        static StockAmount? FindStock(StockAmount[] rows, string name) => rows.FirstOrDefault(x => String.Equals(x.ResourceName, name, StringComparison.Ordinal));
        static ResourceAmount Copy(ResourceAmount x) => new ResourceAmount { ResourceName = x.ResourceName, AmountMicroUnits = x.AmountMicroUnits };
        static ResourceAmount[] CloneResources(ResourceAmount[] xs) => xs.Select(Copy).ToArray();
        static RepeatSchedule Clone(RepeatSchedule? x) => x == null ? new RepeatSchedule() : new RepeatSchedule { NextDueUt = x.NextDueUt, WaitingRequest = x.WaitingRequest, WaitingScheduledUt = x.WaitingScheduledUt, WaitingCoalescedSlots = x.WaitingCoalescedSlots };
    }
}
