using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Expanse.Domain
{
    public sealed class PhysicalTankReservation
    {
        public string DepotId { get; set; } = "";
        public uint PartId { get; set; }
        public string Resource { get; set; } = "";
    }
    public sealed class LogisticsRuntimeContext
    {
        public Guid SessionId { get; set; }
        public Guid LoadEpoch { get; set; }
        public string InstallNamespace { get; set; } = "";
        public string SaveFolder { get; set; } = "";
        public string RunId { get; set; } = "";
        public bool EconomicRecoveryAvailable { get; set; }
        public DepotRegistrySnapshot? Registry { get; set; }
        public InventoryCapability[] Capabilities { get; set; } = Array.Empty<InventoryCapability>();
        public StockObservation[] Stocks { get; set; } = Array.Empty<StockObservation>();
        public PhysicalTankReservation[] ReservedPhysicalTanks { get; set; } = Array.Empty<PhysicalTankReservation>();
    }

    public sealed class LogisticsRuntimeDecision
    {
        public EffectProposal? Proposal { get; set; }
        public TransientShipmentHold[] ArrivalHolds { get; set; } = Array.Empty<TransientShipmentHold>();
        public string Reason { get; set; } = "No due delivery work.";
        public int DueArrivals { get; set; }
    }

    /// <summary>
    /// The game owns scheduled logistics. This pure selector emits at most one typed
    /// effect per call, with arrivals ordered by saved due time before new dispatches.
    /// It never mutates inventory, funds, schedules, or its input capsule. Repeat rules
    /// deliberately coalesce missed slots; stock rules never backdate a physical debit.
    /// </summary>
    public static class LogisticsRuntimePlanner
    {
        public const string OwnershipCapability = "runtimeLogistics.v1";
        public const double MaximumObservationAgeSeconds = 6;
        public const int RetainedReceiptCount = 16;

        public static LogisticsRuntimeDecision Plan(AcceptedState state, LogisticsRuntimeContext context, double currentUt)
        {
            var result = new LogisticsRuntimeDecision();
            if (state == null || context == null || !Finite(currentUt) || context.SessionId == Guid.Empty || context.LoadEpoch == Guid.Empty ||
                String.IsNullOrWhiteSpace(context.RunId) || context.Registry == null)
            { result.Reason = "Game logistics context is unavailable."; return result; }
            if (state.WritesBlocked) { result.Reason = "Selected-save logistics is held by an unresolved inventory or funds effect."; return result; }
            if(context.ReservedPhysicalTanks==null || context.ReservedPhysicalTanks.Length>1024 || context.ReservedPhysicalTanks.Any(r=>r==null||r.PartId==0||String.IsNullOrWhiteSpace(r.DepotId)||r.DepotId.Length>128||String.IsNullOrWhiteSpace(r.Resource)||r.Resource.Length>128))
            {result.Reason="Physical reservation projection is unavailable or invalid; no delivery effect prepared.";return result;}
            if (state.Revision == Int64.MaxValue || state.AcceptedSequence == Int64.MaxValue)
            { result.Reason = "Logistics sequence is exhausted."; return result; }
            // Leave room for both a success and a conservative fault capsule before
            // any external mutation. Unresolved faults cannot be compacted away.
            if (state.Receipts.Length >= AcceptedStateCodec.MaxReceipts - 4)
            { result.Proposal = PlanCompaction(state, context); result.Reason = result.Proposal == null ? "Receipt capacity is held." : "Compacting durable delivery receipts."; return result; }
            if (state.SchemaVersion != 2 || state.DepotRegistryVersion != context.Registry.RegistryVersion || state.DepotRegistryHash != context.Registry.RegistryHash)
            {
                result.Proposal = Proposal(state, context, "syncDepots", "registry:" + context.Registry.RegistryHash,
                    OperationIdentity.DepotRegistryPayloadHash(context.Registry), new DeliveryEffectPayload { Kind = "syncDepots", DepotRegistrySnapshot = context.Registry });
                result.Reason = "Synchronizing the selected save's registered physical endpoints."; return result;
            }
            var holds = new List<TransientShipmentHold>();
            var due = state.ActiveShipments.Where(x => !x.LegacyOpaque && x.DueUt <= currentUt)
                .OrderBy(x => x.DueUt).ThenBy(x => x.ShipmentId, StringComparer.Ordinal).ToArray();
            result.DueArrivals = due.Length;
            foreach (var shipment in due)
            {
                var route = state.RouteVersions.SingleOrDefault(x => !x.LegacyOpaque && x.RouteId == shipment.RouteId && x.Version == shipment.RouteVersion);
                string? reason = null;
                if (route == null || !DestinationCurrent(state, route)) reason = "Due cargo destination registration changed; restore its exact membership.";
                else if (shipment.DestinationKind == OreExportPolicy.VirtualDestinationKind)
                {
                    if (!context.EconomicRecoveryAvailable) reason = "Modeled Kerbin recovery requires an available Career funds account.";
                    else
                    {
                        // The departed shipment supplies price and timing, never the new-order defaults.
                        var intent = new EconomicRecoveryIntent { ShipmentId = shipment.ShipmentId,
                            FundsDelta = OreExportPolicy.RecoveryFunds(shipment.RemainingResources, shipment.FundsPerUnit) };
                        result.Proposal = Proposal(state, context, "recoverySale", "recover:" + shipment.ShipmentId + ":" + shipment.Revision,
                            OperationIdentity.RecoveryPayloadHash(intent), new DeliveryEffectPayload { Kind = "recoverySale", RecoveryIntent = intent });
                    }
                }
                else
                {
                    var stock = FreshStock(state, context, shipment.DestinationDepotId);
                    var capability = Writable(context, shipment.DestinationDepotId, route.DestinationMembershipRevision, route.DestinationMembershipHash);
                    if (stock == null || capability == null) reason = ProviderHold(context, shipment.DestinationDepotId, stock);
                    else
                    {
                        var arrival = LogisticsPlanner.PlanArrival(Cargo(shipment), stock, currentUt);
                        if (arrival.Credits.Length == 0) reason = arrival.Reason;
                        else
                        {
                            var intent = BuildIntent("arrivalCredit", capability, stock, arrival.Credits, false, context.ReservedPhysicalTanks, out reason);
                            if (intent != null)
                            {
                                var payload = new DeliveryEffectPayload { Kind = "arrival", ShipmentId = shipment.ShipmentId,
                                    Credits = arrival.Credits, RemainingCargo = arrival.RemainingCargo, PhysicalEffect = intent };
                                result.Proposal = Proposal(state, context, "arrival", "arrive:" + shipment.ShipmentId + ":" + shipment.Revision,
                                    OperationIdentity.ArrivalPayloadHash(shipment.ShipmentId, arrival.Credits, arrival.RemainingCargo, intent), payload);
                            }
                        }
                    }
                }
                if (result.Proposal != null) break;
                holds.Add(new TransientShipmentHold { ShipmentId = shipment.ShipmentId, Reason = reason ?? "Arrival is held." });
            }
            result.ArrivalHolds = holds.ToArray();
            if (result.Proposal != null) { result.Reason = "Applying due cargo through its authoritative effect provider."; return result; }
            if (state.ActiveShipments.Length >= AcceptedStateV2Limits.MaxActiveShipments)
            { result.Reason = "The 32-shipment freight capacity is full."; return result; }
            foreach (var rule in state.DeliveryRules.Where(x => !x.LegacyOpaque && x.Enabled).OrderBy(x => x.RuleId, StringComparer.Ordinal))
            {
                var route = state.RouteVersions.SingleOrDefault(x => !x.LegacyOpaque && x.RouteId == rule.RouteId && x.Version == rule.RouteVersion);
                if (route == null || !SourceCurrent(state, route) || !DestinationCurrent(state, route)) { result.Reason = "Delivery route registration changed."; continue; }
                bool recovery = route.DestinationKind == OreExportPolicy.VirtualDestinationKind;
                if (recovery && (route.FundsPerUnit != OreExportPolicy.DefaultFundsPerUnit || !context.EconomicRecoveryAvailable))
                { result.Reason = route.FundsPerUnit != OreExportPolicy.DefaultFundsPerUnit ? "New Ore dispatch needs a 100-funds route; departed terms remain saved." : "Career funds recovery is unavailable."; continue; }
                var stock = FreshStock(state, context, route.SourceDepotId);
                var capability = Writable(context, route.SourceDepotId, route.SourceMembershipRevision, route.SourceMembershipHash);
                if (stock == null || capability == null) { result.Reason = ProviderHold(context, route.SourceDepotId, stock); continue; }
                if (rule.Revision == Int64.MaxValue) { result.Reason = "Delivery rule revision is exhausted."; continue; }
                var resources = route.Resources;
                var update = CloneRule(rule); update.Revision = checked(rule.Revision + 1);
                if (rule.Kind == "repeat")
                {
                    var schedule = LogisticsPlanner.PlanRepeat(new RepeatSchedule { NextDueUt = rule.NextDueUt, WaitingRequest = rule.WaitingRequest,
                        WaitingScheduledUt = rule.WaitingScheduledUt, WaitingCoalescedSlots = rule.WaitingCoalescedSlots }, rule.IntervalSeconds, currentUt, true);
                    if (schedule.Outcome != "dispatch") continue;
                    update.NextDueUt = schedule.Next.NextDueUt; update.WaitingRequest = false;
                    update.WaitingScheduledUt = schedule.ScheduledUt; update.WaitingCoalescedSlots = schedule.CoalescedSlots;
                }
                else if (rule.Kind == "exportStock")
                {
                    if (!recovery || rule.ResourceName != "Ore" || resources.Length != 1 || resources[0].AmountMicroUnits != rule.BatchSizeMicroUnits) continue;
                    var decision = LogisticsPlanner.PlanExportStock(rule.TargetMicroUnits, stock, rule.BatchSizeMicroUnits);
                    if (decision.Outcome != "dispatch") { result.Reason = decision.Reason; continue; }
                }
                else if (rule.Kind == "keepStock")
                {
                    var destination = FreshStock(state, context, route.DestinationDepotId);
                    if (destination == null) { result.Reason = "Fresh destination stock is unavailable for automatic fuel replenishment."; continue; }
                    var inbound = state.ActiveShipments.Where(x => !x.LegacyOpaque && x.DestinationDepotId == route.DestinationDepotId).Select(Cargo).ToArray();
                    var decision = LogisticsPlanner.PlanKeepStock(rule.ResourceName, rule.LowTriggerMicroUnits, rule.TargetMicroUnits, rule.BatchSizeMicroUnits, destination, inbound);
                    if (decision.Outcome != "dispatch" || decision.RequestedDispatchMicroUnits <= 0) continue;
                    resources = new[] { new ResourceAmount { ResourceName = rule.ResourceName, AmountMicroUnits = decision.RequestedDispatchMicroUnits } };
                }
                else continue;
                var requestId = RequestId(state, context, "dispatch:" + rule.RuleId + ":" + rule.Revision);
                var operation = OperationIdentity.Create(state.WorldId, state.AcceptedSequence + 1, requestId);
                var manifest = new RouteManifest { RouteId = route.RouteId, Version = route.Version, SourceDepotId = route.SourceDepotId,
                    DestinationDepotId = route.DestinationDepotId, TravelDurationSeconds = route.TravelDurationSeconds, Provenance = route.Provenance, Resources = resources };
                var planned = LogisticsPlanner.PlanDispatch(manifest, stock, "shipment:" + operation, currentUt);
                if (planned.Outcome != "dispatch") { result.Reason = planned.Reason; continue; }
                var intent = BuildIntent("dispatchDebit", capability, stock, planned.Debits, true, context.ReservedPhysicalTanks, out var allocationHold);
                if (intent == null) { result.Reason = allocationHold ?? "Selected source stock cannot supply this manifest."; continue; }
                // Reducer assigns actual departure/due UT at the physical commit, avoiding
                // elapsed travel before cargo was truly debited during background catch-up.
                var shipment = new ActiveShipmentRecord { ShipmentId = "shipment:" + operation, RouteId = route.RouteId, RouteVersion = route.Version,
                    SourceDepotId = route.SourceDepotId, DestinationDepotId = route.DestinationDepotId, DestinationKind = route.DestinationKind,
                    FundsPerUnit = route.FundsPerUnit, RemainingResources = planned.Debits };
                result.Proposal = Proposal(state, context, "dispatch", "dispatch:" + rule.RuleId + ":" + rule.Revision,
                    OperationIdentity.DispatchPayloadHash(shipment, intent, update), new DeliveryEffectPayload { Kind = "dispatch", Shipment = shipment, PhysicalEffect = intent, ScheduleRuleUpdate = update });
                result.Reason = "Dispatching one legacy delivery batch."; return result;
            }
            return result;
        }

        public static EffectProposal? PlanCompaction(AcceptedState state, LogisticsRuntimeContext context)
        {
            var watermark = Math.Max(state.CompactionWatermark, state.AcceptedSequence - RetainedReceiptCount);
            if (watermark <= state.CompactionWatermark || state.Faults.Any(x => x.CommandSequence <= watermark) || state.EconomicFaults.Any(x => x.CommandSequence <= watermark)) return null;
            var proposal = Proposal(state, context, "compact", "compact:" + watermark, OperationIdentity.CompactionPayloadHash(watermark), null);
            proposal.TargetCompactionWatermark = watermark; return proposal;
        }

        public static PhysicalEffectIntent? BuildIntent(string kind, InventoryCapability capability, StockObservation observation, ResourceAmount[] resources, bool debit, out string? hold)
            => BuildIntent(kind,capability,observation,resources,debit,Array.Empty<PhysicalTankReservation>(),out hold);

        public static PhysicalEffectIntent? BuildIntent(string kind, InventoryCapability capability, StockObservation observation, ResourceAmount[] resources, bool debit, PhysicalTankReservation[] reservations, out string? hold)
        {
            hold = "Fresh selected tank stock cannot allocate the complete manifest.";
            if (capability == null || observation == null || observation.MemberStocks == null || resources == null || resources.Length == 0 || observation.MemberStocks.Length == 0 || observation.MemberStocks.Length > 64 ||
                observation.MemberStocks.Any(x => x == null || x.MemberPersistentId == 0 || x.Resources == null || x.Resources.Any(s => s == null || String.IsNullOrWhiteSpace(s.ResourceName) || s.AmountMicroUnits < 0 || s.CapacityMicroUnits < s.AmountMicroUnits) || x.Resources.Select(s => s.ResourceName).Distinct(StringComparer.Ordinal).Count() != x.Resources.Length) ||
                observation.MemberStocks.Select(x => x.MemberPersistentId).Distinct().Count() != observation.MemberStocks.Length || resources.Any(x => x == null || String.IsNullOrWhiteSpace(x.ResourceName) || x.AmountMicroUnits <= 0) || resources.Select(x => x.ResourceName).Distinct(StringComparer.Ordinal).Count() != resources.Length) return null;
            var ids = observation.MemberStocks.Select(x => x.MemberPersistentId).OrderBy(x => x).ToArray();
            if (capability.AnchorPersistentId == 0 || capability.MemberSetHash != OperationIdentity.ComputeMemberSetHash(capability.AnchorPersistentId, ids)) return null;
            var deltas = new List<PhysicalEffectResourceDelta>();
            foreach (var resource in resources.OrderBy(x => x.ResourceName, StringComparer.Ordinal))
            {
                long left = resource.AmountMicroUnits;
                foreach (var member in observation.MemberStocks.OrderBy(x => x.MemberPersistentId))
                {
                    var row = member.Resources.SingleOrDefault(x => x.ResourceName == resource.ResourceName);
                    if (row == null || (debit && row.DebitAllowed != true)) continue;
                    if(reservations.Any(r=>r.DepotId==capability.DepotId&&r.PartId==member.MemberPersistentId&&r.Resource==resource.ResourceName))
                    {hold="Selected tank is temporarily reserved by colony work; saved delivery remains due and will retry after release.";continue;}
                    var amount = Math.Min(left, debit ? row.AmountMicroUnits : row.CapacityMicroUnits - row.AmountMicroUnits);
                    if (amount > 0) { deltas.Add(new PhysicalEffectResourceDelta { MemberPersistentId = member.MemberPersistentId, ResourceName = resource.ResourceName, DeltaMicroUnits = debit ? -amount : amount }); left -= amount; }
                    if (left == 0) break;
                }
                if (left != 0) return null;
            }
            if (deltas.Count > AcceptedStateV2Limits.MaxFaultResourceDeltas) { hold = "Manifest exceeds the 16 selected tank/resource mutation limit."; return null; }
            var intent = new PhysicalEffectIntent { EffectKind = kind, Capability = capability, MemberPersistentIds = ids, MembershipRevision = capability.MembershipRevision, Deltas = deltas.ToArray() };
            hold = AcceptedStateV2Draft.ValidatePhysicalIntent(intent); return hold == null ? intent : null;
        }

        static EffectProposal Proposal(AcceptedState state, LogisticsRuntimeContext context, string kind, string key, string payloadHash, DeliveryEffectPayload? delivery)
        {
            var request = RequestId(state, context, key); var sequence = checked(state.AcceptedSequence + 1);
            return new EffectProposal { MessageType = "effectProposal", SessionId = context.SessionId, LoadEpoch = context.LoadEpoch,
                InstallNamespace = context.InstallNamespace, SaveFolder = context.SaveFolder, WorldId = state.WorldId, RunId = context.RunId,
                OperationId = OperationIdentity.Create(state.WorldId, sequence, request), ClientRequestId = request, CommandSequence = sequence,
                ExpectedRevision = state.Revision, ExpectedStateHash = AcceptedStateCodec.ComputeHash(state), PayloadHash = payloadHash, OperationKind = kind, Delivery = delivery };
        }
        static string RequestId(AcceptedState state, LogisticsRuntimeContext context, string key)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(String.Join("\0", state.WorldId, context.RunId, state.CheckpointId, state.Revision.ToString(CultureInfo.InvariantCulture), key)));
                return "game-logistics:" + BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
            }
        }
        static StockObservation? FreshStock(AcceptedState state, LogisticsRuntimeContext context, string depot)
        {
            var rows = context.Stocks.Where(x => x.DepotId == depot).ToArray(); if (rows.Length != 1) return null; var stock = rows[0];
            var member = state.Depots.SingleOrDefault(x => x.Active && x.DepotId == depot);
            return member != null && stock.Available && stock.MicroUnitProjectionSafe && Finite(stock.AgeSeconds) && stock.AgeSeconds <= MaximumObservationAgeSeconds &&
                stock.WorldId == state.WorldId && stock.SessionId == context.SessionId && stock.LoadEpoch == context.LoadEpoch && stock.RegistryHash == state.DepotRegistryHash &&
                stock.MembershipRevision == member.MembershipRevision && stock.MembershipHash == member.MembershipHash ? stock : null;
        }
        static InventoryCapability? Writable(LogisticsRuntimeContext context, string depot, long revision, string hash)
        {
            var rows = context.Capabilities.Where(x => x.DepotId == depot).ToArray(); if (rows.Length != 1) return null; var c = rows[0];
            return c.MembershipRevision == revision && c.MembershipHash == hash && c.ObservationAvailable && c.ReadSupported && c.WriteSupported && c.SynchronousRollbackSupported && c.PersistenceSyncSupported ? c : null;
        }
        static string ProviderHold(LogisticsRuntimeContext context, string depot, StockObservation? stock)
        { var c = context.Capabilities.FirstOrDefault(x => x.DepotId == depot); return c != null && !String.IsNullOrWhiteSpace(c.HoldReason) ? c.HoldReason : stock == null ? "Fresh authoritative selected inventory is unavailable." : "Selected inventory is not writable."; }
        static bool SourceCurrent(AcceptedState state, RouteVersionRecord route) => state.Depots.Any(d => d.Active && d.DepotId == route.SourceDepotId && d.MembershipRevision == route.SourceMembershipRevision && d.MembershipHash == route.SourceMembershipHash);
        static bool DestinationCurrent(AcceptedState state, RouteVersionRecord route) => route.DestinationKind == OreExportPolicy.VirtualDestinationKind || state.Depots.Any(d => d.Active && d.DepotId == route.DestinationDepotId && d.MembershipRevision == route.DestinationMembershipRevision && d.MembershipHash == route.DestinationMembershipHash);
        static bool Finite(double value) => value >= 0 && !Double.IsNaN(value) && !Double.IsInfinity(value);
        static CargoManifest Cargo(ActiveShipmentRecord s) => new CargoManifest { ShipmentId = s.ShipmentId, RouteId = s.RouteId, RouteVersion = s.RouteVersion, SourceDepotId = s.SourceDepotId, DestinationDepotId = s.DestinationDepotId, DepartureUt = s.DepartureUt, DueUt = s.DueUt, RemainingResources = s.RemainingResources };
        static DeliveryRuleRecord CloneRule(DeliveryRuleRecord r) => new DeliveryRuleRecord { RuleId = r.RuleId, Revision = r.Revision, Kind = r.Kind, RouteId = r.RouteId, RouteVersion = r.RouteVersion, Enabled = r.Enabled, NextDueUt = r.NextDueUt, IntervalSeconds = r.IntervalSeconds, WaitingRequest = r.WaitingRequest, WaitingScheduledUt = r.WaitingScheduledUt, WaitingCoalescedSlots = r.WaitingCoalescedSlots, ResourceName = r.ResourceName, LowTriggerMicroUnits = r.LowTriggerMicroUnits, TargetMicroUnits = r.TargetMicroUnits, BatchSizeMicroUnits = r.BatchSizeMicroUnits, LegacyOpaque = r.LegacyOpaque };
    }
}
