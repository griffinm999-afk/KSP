using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace Expanse.WorldBridge
{
    /// <summary>
    /// BRP 0.2.7 loaded-vessel candidate. The candidate is available only after an
    /// explicitly consumed dev-fixture token; the loaded-vessel path is read-only in ordinary installs.
    /// It writes PartResource.amount, ResourceInventory.Amount and OriginalAmount as
    /// one synchronous unit, deliberately leaves the provider Snapshot untouched, and
    /// requires a fresh coherent snapshot baseline, then uses a bounded in-memory
    /// ownership witness for consecutive writes in the same loaded run. Snapshot is
    /// never changed; any lifecycle or identity boundary drops the witness.
    /// </summary>
    internal sealed class BrpInventoryGateway : IInventoryGateway
    {
        private const int MaxMembers = 64;
        private const int MaxDeltas = 16;
        private const double MicroUnits = 1000000.0;
        private const double Epsilon = 2.2204460492503131e-16;
        private const string ProviderName = "BackgroundResourceProcessing";
        private const string ProviderTypeName = "BackgroundResourceProcessing.BackgroundResourceProcessor";
        private const string ProviderVersion = "0.2.7";
        private const string ProductionHold = "This depot is currently loaded. Switch away from the depot (for example, return to the Space Center) to allow deliveries.";

        private sealed class Lineage
        {
            public PartResource Resource;
            public object Inventory, Snapshot;
            public double SnapshotBaseline, LastCommittedAmount;
        }

        private readonly Dictionary<string, Lineage> lineage = new Dictionary<string, Lineage>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> membershipScopes = new Dictionary<string, string>(StringComparer.Ordinal);
        private string runtimeScope;

        public void SetRuntimeScope(string worldId, string runId, string sessionId, string loadEpoch)
        {
            string next = (worldId ?? "") + "\n" + (runId ?? "") + "\n" + (sessionId ?? "") + "\n" + (loadEpoch ?? "");
            if (!String.Equals(runtimeScope, next, StringComparison.Ordinal))
            {
                lineage.Clear();
                membershipScopes.Clear();
                runtimeScope = next;
            }
        }

        public void InvalidateLineage() { lineage.Clear(); membershipScopes.Clear(); }

        public void MaintainLifecycle(bool loadResolved)
        {
            if (!loadResolved || !HighLogic.LoadedSceneIsFlight || FlightGlobals.fetch == null || !FlightGlobals.ready)
            {
                InvalidateLineage();
                return;
            }
            try
            {
                Vessel active = FlightGlobals.ActiveVessel;
                if (!IsSupportedScene(active)) InvalidateLineage();
            }
            catch
            {
                // FlightGlobals can be unavailable during startup/scene transition.
                InvalidateLineage();
            }
        }

        private sealed class Entry
        {
            public Part Part;
            public PartResource Resource;
            public object Inventory;
            public object Snapshot;
            public FieldInfo AmountField;
            public FieldInfo OriginalAmountField;
            public FieldInfo SnapshotAmountField;
            public uint PersistentId;
            public string ResourceName;
        }

        private sealed class Context
        {
            public Vessel Vessel;
            public InventoryEndpoint Endpoint;
            public Dictionary<string, Entry> Entries;
            public string Token;
        }

        public InventoryCapabilityEvidence Describe(InventoryEndpoint endpoint)
        {
            bool context = endpoint != null && IsSupportedScene(FlightGlobals.ActiveVessel) && HasProcessor(FlightGlobals.ActiveVessel);
            bool candidate = WorldBridgeAddon.PhysicalCandidateEnabled;
            string reason = !context ? "Depot inventory is unavailable or not supported for transfer." : candidate ? null : ProductionHold;
            return new InventoryCapabilityEvidence
            {
                ProviderId = ProviderName,
                ProviderVersion = ProviderVersion,
                Scene = endpoint == null ? "unknown" : endpoint.Scene,
                ObservationAvailable = context,
                ReadSupported = context,
                WriteSupported = context && candidate,
                SynchronousRollbackSupported = context && candidate,
                PersistenceSyncSupported = context && candidate,
                HoldReason = reason
            };
        }

        public InventoryResolution Resolve(InventoryEndpoint endpoint)
        {
            if (!ValidateEndpoint(endpoint) || !IsSupportedScene(FlightGlobals.ActiveVessel)) return null;
            Vessel vessel = FlightGlobals.ActiveVessel;
            Dictionary<uint, List<Part>> parts = new Dictionary<uint, List<Part>>();
            foreach (Part part in vessel.parts)
            {
                if (part == null) return null;
                List<Part> matches;
                if (!parts.TryGetValue(part.persistentId, out matches)) parts[part.persistentId] = matches = new List<Part>(1);
                matches.Add(part);
            }
            Dictionary<uint, Part> selected = new Dictionary<uint, Part>();
            foreach (uint id in endpoint.MemberPersistentIds)
            {
                List<Part> matches;
                if (!parts.TryGetValue(id, out matches) || matches.Count != 1 || matches[0].Resources == null) return null;
                selected.Add(id, matches[0]);
            }

            List<object> processors;
            try { processors = EnumerateMember(vessel, "vesselModules").Where(x => x != null && x.GetType().FullName == ProviderTypeName).ToList(); }
            catch { return null; }
            if (processors.Count != 1) return null;
            object collection;
            try { collection = ReadMember(processors[0], "Inventories"); }
            catch { return null; }
            IEnumerable inventories = collection as IEnumerable;
            if (inventories == null) return null;

            Dictionary<string, List<object>> providerByKey = new Dictionary<string, List<object>>(StringComparer.Ordinal);
            foreach (object inventory in inventories)
            {
                if (inventory == null) return null;
                try
                {
                    uint flightId = Convert.ToUInt32(ReadMember(inventory, "FlightId"), CultureInfo.InvariantCulture);
                    object moduleId = ReadMember(inventory, "ModuleId");
                    string name = Convert.ToString(ReadMember(inventory, "ResourceName"), CultureInfo.InvariantCulture);
                    if (moduleId != null || String.IsNullOrWhiteSpace(name)) continue;
                    string key = FlightResourceKey(flightId, name);
                    List<object> matches;
                    if (!providerByKey.TryGetValue(key, out matches)) providerByKey[key] = matches = new List<object>(1);
                    matches.Add(inventory);
                }
                catch { return null; }
            }

            Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
            foreach (KeyValuePair<uint, Part> selectedPart in selected)
            {
                Part part = selectedPart.Value;
                HashSet<string> resourceNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (PartResource resource in part.Resources)
                {
                    if (resource == null || String.IsNullOrWhiteSpace(resource.resourceName) || !resourceNames.Add(resource.resourceName)) return null;
                    List<object> matches;
                    if (!providerByKey.TryGetValue(FlightResourceKey(part.flightID, resource.resourceName), out matches) || matches.Count != 1) return null;
                    object inventory = matches[0];
                    FieldInfo amountField = FindField(inventory.GetType(), "Amount");
                    FieldInfo originalField = FindField(inventory.GetType(), "OriginalAmount");
                    if (amountField == null || originalField == null || amountField.FieldType != typeof(double) || originalField.FieldType != typeof(double)) return null;
                    object snapshot;
                    try { snapshot = ReadMember(inventory, "Snapshot"); }
                    catch { return null; }
                    FieldInfo snapshotField = snapshot == null ? null : FindField(snapshot.GetType(), "amount");
                    if (snapshot != null && (snapshotField == null || snapshotField.FieldType != typeof(double))) return null;
                    string key = PartResourceKey(part.persistentId, resource.resourceName);
                    entries.Add(key, new Entry { Part = part, Resource = resource, Inventory = inventory, Snapshot = snapshot, AmountField = amountField,
                        OriginalAmountField = originalField, SnapshotAmountField = snapshotField, PersistentId = part.persistentId, ResourceName = resource.resourceName });
                }
            }

            string token = Guid.NewGuid().ToString("N");
            Context context = new Context { Vessel = vessel, Endpoint = CloneEndpoint(endpoint), Entries = entries, Token = token };
            return new InventoryResolution(context.Endpoint, context, token);
        }

        public InventoryObservation Observe(InventoryResolution resolution)
        {
            Context context = GetContext(resolution);
            if (context == null) return null;
            List<InventoryStockRow> rows = new List<InventoryStockRow>();
            foreach (Entry entry in context.Entries.Values)
            {
                double amount = entry.Resource.amount, capacity = entry.Resource.maxAmount;
                if (!ValidNonnegative(amount) || !ValidNonnegative(capacity) || amount > capacity) return null;
                rows.Add(new InventoryStockRow { MemberPersistentId = entry.PersistentId, ResourceName = entry.ResourceName, Amount = amount, Capacity = capacity, DebitAllowed = entry.Resource.flowState });
            }
            rows.Sort((a, b) => { int c = a.MemberPersistentId.CompareTo(b.MemberPersistentId); return c == 0 ? String.CompareOrdinal(a.ResourceName, b.ResourceName) : c; });
            return new InventoryObservation { ContextToken = context.Token, ObservationVersion = String.Join("|", rows.Select(x => x.MemberPersistentId.ToString(CultureInfo.InvariantCulture) + ":" + x.ResourceName + ":" + x.Amount.ToString("R", CultureInfo.InvariantCulture) + ":" + x.Capacity.ToString("R", CultureInfo.InvariantCulture) + ":" + x.DebitAllowed)),
                ProviderId = ProviderName, ProviderVersion = ProviderVersion, MembershipRevision = context.Endpoint.MembershipRevision, Rows = rows.ToArray() };
        }

        public InventoryPreflightResult Preflight(InventoryResolution resolution, IList<InventoryDelta> deltas)
        {
            InventoryObservation observation = Observe(resolution);
            if (observation == null) return Fail("BRP selected resource mapping or active-vessel context is stale");
            if (!WorldBridgeAddon.PhysicalCandidateEnabled) return new InventoryPreflightResult { Accepted = false, Reason = ProductionHold, Observation = observation };
            if (deltas == null || deltas.Count == 0 || deltas.Count > MaxDeltas) return new InventoryPreflightResult { Accepted = false, Reason = "Physical intent exceeds the 16-row candidate bound", Observation = observation };
            Context context = GetContext(resolution);
            Dictionary<string, long> totals = new Dictionary<string, long>(StringComparer.Ordinal);
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            List<InventoryMutationRow> rows = new List<InventoryMutationRow>(deltas.Count);
            string membership = context.Endpoint.MembershipRevision.ToString(CultureInfo.InvariantCulture) + ":" +
                context.Endpoint.AnchorPersistentId.ToString(CultureInfo.InvariantCulture) + ":" + (context.Endpoint.MembershipHash ?? "") + ":" +
                (context.Endpoint.MemberSetHash ?? "") + ":" + String.Join(",", context.Endpoint.MemberPersistentIds.Select(x => x.ToString(CultureInfo.InvariantCulture)));
            string priorMembership;
            if (membershipScopes.TryGetValue(context.Endpoint.DepotId, out priorMembership) && !String.Equals(priorMembership, membership, StringComparison.Ordinal))
                InvalidateLineage();
            membershipScopes[context.Endpoint.DepotId] = membership;
            foreach (InventoryDelta delta in deltas)
            {
                if (delta == null || delta.MemberPersistentId == 0 || String.IsNullOrWhiteSpace(delta.ResourceName) || delta.DeltaMicroUnits == 0) return Fail("Physical intent contains an invalid or zero resource delta", observation);
                string key = PartResourceKey(delta.MemberPersistentId, delta.ResourceName);
                Entry entry;
                if (!seen.Add(key) || !context.Entries.TryGetValue(key, out entry)) return Fail("Physical intent repeats or names an unregistered member/resource", observation);
                double before = entry.Resource.amount;
                double provider = GetDouble(entry.AmountField, entry.Inventory);
                double original = GetDouble(entry.OriginalAmountField, entry.Inventory);
                double snapshot = entry.Snapshot == null ? before : GetDouble(entry.SnapshotAmountField, entry.Snapshot);
                double capacity = entry.Resource.maxAmount;
                string lineageKey = LineageKey(context.Endpoint, entry);
                Lineage priorLineage;
                bool hasLineage = lineage.TryGetValue(lineageKey, out priorLineage);
                bool coherentInitial = snapshot == before && provider == before && original == before;
                bool coherentOwned = hasLineage && Object.ReferenceEquals(priorLineage.Resource, entry.Resource) &&
                    Object.ReferenceEquals(priorLineage.Inventory, entry.Inventory) && Object.ReferenceEquals(priorLineage.Snapshot, entry.Snapshot) &&
                    priorLineage.SnapshotBaseline == snapshot && before == priorLineage.LastCommittedAmount &&
                    provider == priorLineage.LastCommittedAmount && original == priorLineage.LastCommittedAmount;
                if (!ValidNonnegative(before) || !ValidNonnegative(provider) || !ValidNonnegative(original) || !ValidNonnegative(snapshot) || !ValidNonnegative(capacity) || before > capacity || !(coherentInitial || coherentOwned))
                {
                    lineage.Clear();
                    return Fail("Provider baseline is neither initially coherent nor owned by this loaded-run witness", observation);
                }
                if (entry.Resource.maxAmount != Convert.ToDouble(ReadMember(entry.Inventory, "MaxAmount"), CultureInfo.InvariantCulture)) return Fail("KSP and BRP resource capacities disagree", observation);
                if (!entry.Resource.flowState) return Fail("Selected resource is flow-locked", observation);
                double requested = delta.DeltaMicroUnits / MicroUnits;
                double after = before + requested;
                if (!ValidNonnegative(after) || after > capacity || after == before || Math.Abs((after - before) - requested) > Tolerance(before, after, requested)) return Fail("Requested micro-unit delta is not exactly representable within capacity", observation);
                rows.Add(new InventoryMutationRow { Resource = entry.Resource, ProviderInventory = entry.Inventory, ProviderSnapshot = entry.Snapshot,
                    ProviderAmountField = entry.AmountField, ProviderOriginalAmountField = entry.OriginalAmountField, SnapshotAmountField = entry.SnapshotAmountField,
                    MemberPersistentId = delta.MemberPersistentId, ResourceName = delta.ResourceName, BeforeAmount = before, BeforeProviderAmount = provider,
                    BeforeOriginalAmount = original, BeforeSnapshotAmount = snapshot, HasProviderSnapshot = entry.Snapshot != null,
                    IntendedAfterAmount = after, Capacity = capacity, DeltaMicroUnits = delta.DeltaMicroUnits, LineageKey = lineageKey });
                long total; totals.TryGetValue(delta.ResourceName, out total);
                try { totals[delta.ResourceName] = checked(total + delta.DeltaMicroUnits); } catch { return Fail("Physical resource total overflowed", observation); }
            }
            // Dispatch/arrival can be non-zero in-vessel; the typed reducer balances it
            // against the accepted in-transit ledger. Do not silently impose tank net-zero.
            return new InventoryPreflightResult { Accepted = true, Observation = observation, Plan = new InventoryMutationPlan(resolution, context.Token, rows) };
        }

        public InventoryApplyResult Apply(InventoryMutationPlan plan)
        {
            Context context = plan == null ? null : GetContext(plan.Resolution);
            if (context == null || !WorldBridgeAddon.PhysicalCandidateEnabled || plan.AttemptedCount != 0)
                return new InventoryApplyResult { Succeeded = false, Reason = context == null ? "Depot inventory is unavailable or not supported for transfer." : !WorldBridgeAddon.PhysicalCandidateEnabled ? ProductionHold : "Physical plan was already attempted.", Plan = plan, Rows = new InventoryAppliedRow[0] };
            List<InventoryAppliedRow> rows = new List<InventoryAppliedRow>(plan.Rows.Count);
            try
            {
                foreach (InventoryMutationRow row in plan.Rows)
                {
                    Entry entry;
                    if (!context.Entries.TryGetValue(PartResourceKey(row.MemberPersistentId, row.ResourceName), out entry) || !Object.ReferenceEquals(entry.Resource, row.Resource) || !Object.ReferenceEquals(entry.Inventory, row.ProviderInventory) || row.Resource.amount != row.BeforeAmount || GetDouble(row.ProviderAmountField, row.ProviderInventory) != row.BeforeProviderAmount || GetDouble(row.ProviderOriginalAmountField, row.ProviderInventory) != row.BeforeOriginalAmount || SnapshotAmount(row) != row.BeforeSnapshotAmount)
                        throw new InvalidOperationException("Triple-field provider baseline changed after preflight");
                    plan.AttemptedCount++;
                    row.Resource.amount = row.IntendedAfterAmount;
                    row.ProviderAmountField.SetValue(row.ProviderInventory, row.IntendedAfterAmount);
                    row.ProviderOriginalAmountField.SetValue(row.ProviderInventory, row.IntendedAfterAmount);
                    double partAfter = row.Resource.amount, providerAfter = GetDouble(row.ProviderAmountField, row.ProviderInventory), originalAfter = GetDouble(row.ProviderOriginalAmountField, row.ProviderInventory);
                    if (partAfter != row.IntendedAfterAmount || providerAfter != row.IntendedAfterAmount || originalAfter != row.IntendedAfterAmount || partAfter == row.BeforeAmount)
                        throw new InvalidOperationException("PartResource/BRP triple-field readback differs from intended stock");
                    rows.Add(new InventoryAppliedRow { MemberPersistentId = row.MemberPersistentId, ResourceName = row.ResourceName, BeforeAmount = row.BeforeAmount, IntendedAfterAmount = row.IntendedAfterAmount, ObservedAfterAmount = partAfter, ActualDelta = partAfter - row.BeforeAmount });
                }
                return new InventoryApplyResult { Succeeded = true, Plan = plan, Rows = rows.ToArray() };
            }
            catch (Exception ex) { return new InventoryApplyResult { Succeeded = false, Reason = Bound(ex.Message), Plan = plan, Rows = rows.ToArray() }; }
        }

        public InventoryRollbackResult Rollback(InventoryMutationPlan plan)
        {
            // Even a confirmed rollback ends the ownership chain. A later attempt
            // must establish a new coherent provider baseline.
            lineage.Clear();
            if (plan == null) return new InventoryRollbackResult { Confirmed = false, Reason = "Mutation plan is missing", Rows = new InventoryAppliedRow[0] };
            List<InventoryAppliedRow> rows = new List<InventoryAppliedRow>(); bool confirmed = true; string reason = null;
            for (int i = Math.Min(plan.AttemptedCount, plan.Rows.Count) - 1; i >= 0; i--)
            {
                InventoryMutationRow row = plan.Rows[i];
                try
                {
                    row.Resource.amount = row.BeforeAmount;
                    row.ProviderAmountField.SetValue(row.ProviderInventory, row.BeforeProviderAmount);
                    row.ProviderOriginalAmountField.SetValue(row.ProviderInventory, row.BeforeOriginalAmount);
                    double observed = row.Resource.amount, provider = GetDouble(row.ProviderAmountField, row.ProviderInventory), original = GetDouble(row.ProviderOriginalAmountField, row.ProviderInventory);
                    if (observed != row.BeforeAmount || provider != row.BeforeProviderAmount || original != row.BeforeOriginalAmount || SnapshotAmount(row) != row.BeforeSnapshotAmount) throw new InvalidOperationException("Triple-field rollback readback failed");
                    rows.Add(new InventoryAppliedRow { MemberPersistentId = row.MemberPersistentId, ResourceName = row.ResourceName, BeforeAmount = row.IntendedAfterAmount, IntendedAfterAmount = row.BeforeAmount, ObservedAfterAmount = observed, ActualDelta = observed - row.IntendedAfterAmount });
                }
                catch (Exception ex) { confirmed = false; if (reason == null) reason = Bound(ex.Message); }
            }
            return new InventoryRollbackResult { Confirmed = confirmed, Reason = confirmed ? null : reason ?? "Rollback could not be confirmed", Rows = rows.ToArray() };
        }

        public InventorySyncResult Synchronize(InventoryMutationPlan plan)
        {
            Context context = plan == null ? null : GetContext(plan.Resolution);
            if (context == null || !WorldBridgeAddon.PhysicalCandidateEnabled) return new InventorySyncResult { Succeeded = false, Reason = context == null ? "Depot inventory is unavailable or not supported for transfer." : ProductionHold };
            foreach (InventoryMutationRow row in plan.Rows)
            {
                Entry entry;
                if (!context.Entries.TryGetValue(PartResourceKey(row.MemberPersistentId, row.ResourceName), out entry) || row.Resource.amount != row.IntendedAfterAmount || GetDouble(row.ProviderAmountField, row.ProviderInventory) != row.IntendedAfterAmount || GetDouble(row.ProviderOriginalAmountField, row.ProviderInventory) != row.IntendedAfterAmount)
                    return new InventorySyncResult { Succeeded = false, Reason = "PartResource/BRP triple-field post-write readback failed" };
                // Snapshot intentionally remains unchanged so the fixture can observe
                // whether BRP replays it on immediate unload; no hidden mirror write.
            }
            return new InventorySyncResult { Succeeded = true };
        }

        public void CommitLineage(InventoryMutationPlan plan)
        {
            Context context = plan == null ? null : GetContext(plan.Resolution);
            if (context == null || !WorldBridgeAddon.PhysicalCandidateEnabled)
            {
                lineage.Clear();
                return;
            }
            foreach (InventoryMutationRow row in plan.Rows)
            {
                if (row.Resource.amount != row.IntendedAfterAmount || GetDouble(row.ProviderAmountField, row.ProviderInventory) != row.IntendedAfterAmount ||
                    GetDouble(row.ProviderOriginalAmountField, row.ProviderInventory) != row.IntendedAfterAmount || SnapshotAmount(row) != row.BeforeSnapshotAmount)
                {
                    lineage.Clear();
                    return;
                }
                lineage[row.LineageKey] = new Lineage
                {
                    Resource = row.Resource, Inventory = row.ProviderInventory, Snapshot = row.ProviderSnapshot,
                    SnapshotBaseline = row.BeforeSnapshotAmount, LastCommittedAmount = row.IntendedAfterAmount
                };
            }
        }

        private static Context GetContext(InventoryResolution resolution)
        {
            Context context = resolution == null ? null : resolution.ProviderContext as Context;
            if (context == null || !Object.ReferenceEquals(context.Endpoint, resolution.Endpoint) || !String.Equals(context.Token, resolution.ContextToken, StringComparison.Ordinal) || !IsSupportedScene(context.Vessel) || context.Endpoint.MembershipRevision != resolution.Endpoint.MembershipRevision || context.Vessel.parts == null) return null;
            Dictionary<uint, int> counts = new Dictionary<uint, int>(); HashSet<Part> parts = new HashSet<Part>();
            foreach (Part part in context.Vessel.parts) { if (part == null) return null; parts.Add(part); int n; counts.TryGetValue(part.persistentId, out n); counts[part.persistentId] = n + 1; }
            foreach (uint id in context.Endpoint.MemberPersistentIds)
            {
                int count; if (!counts.TryGetValue(id, out count) || count != 1) return null;
                Part part = context.Vessel.parts.FirstOrDefault(x => x != null && x.persistentId == id);
                if (part == null || !parts.Contains(part)) return null;
            }
            foreach (Entry e in context.Entries.Values)
            {
                if (!parts.Contains(e.Part) || e.Part.persistentId != e.PersistentId || e.Part.Resources == null || !e.Part.Resources.Contains(e.Resource) || !Object.ReferenceEquals(e.Resource, context.Entries[PartResourceKey(e.PersistentId, e.ResourceName)].Resource)) return null;
            }
            return context;
        }

        private static bool ValidateEndpoint(InventoryEndpoint e) => e != null && !String.IsNullOrWhiteSpace(e.DepotId) && e.MembershipRevision > 0 && e.MemberPersistentIds != null && e.MemberPersistentIds.Length > 0 && e.MemberPersistentIds.Length <= MaxMembers && e.MemberPersistentIds.All(x => x != 0) && e.MemberPersistentIds.Distinct().Count() == e.MemberPersistentIds.Length;
        private static bool IsSupportedScene(Vessel vessel) => HighLogic.LoadedSceneIsFlight && vessel != null && vessel == FlightGlobals.ActiveVessel && vessel.loaded && !vessel.packed && vessel.vesselType != VesselType.EVA && vessel.parts != null;
        private static bool HasProcessor(Vessel vessel)
        {
            try { return EnumerateMember(vessel, "vesselModules").Any(x => x != null && x.GetType().FullName == ProviderTypeName); }
            catch { return false; }
        }
        private static string FlightResourceKey(uint id, string name) => id.ToString(CultureInfo.InvariantCulture) + "\0" + name;
        private static string PartResourceKey(uint id, string name) => id.ToString(CultureInfo.InvariantCulture) + "\0" + name;
        private static string LineageKey(InventoryEndpoint endpoint, Entry entry) => endpoint.DepotId + "\0" + endpoint.MembershipRevision.ToString(CultureInfo.InvariantCulture) + "\0" +
            endpoint.AnchorPersistentId.ToString(CultureInfo.InvariantCulture) + "\0" + (endpoint.MembershipHash ?? "") + "\0" + (endpoint.MemberSetHash ?? "") + "\0" +
            String.Join(",", endpoint.MemberPersistentIds.Select(x => x.ToString(CultureInfo.InvariantCulture))) + "\0" + PartResourceKey(entry.PersistentId, entry.ResourceName);
        private static InventoryEndpoint CloneEndpoint(InventoryEndpoint e) => new InventoryEndpoint { DepotId = e.DepotId, MembershipRevision = e.MembershipRevision,
            MemberPersistentIds = (uint[])e.MemberPersistentIds.Clone(), AnchorPersistentId = e.AnchorPersistentId, MembershipHash = e.MembershipHash,
            MemberSetHash = e.MemberSetHash, Scene = e.Scene };
        private static List<object> EnumerateMember(object instance, string name) { object value = ReadMember(instance, name); IEnumerable sequence = value as IEnumerable; if (sequence == null) throw new InvalidOperationException("Provider member is not enumerable"); return sequence.Cast<object>().ToList(); }
        private static object ReadMember(object instance, string name) { if (instance == null) throw new InvalidOperationException("Provider object is missing"); FieldInfo field = FindField(instance.GetType(), name); if (field != null) return field.GetValue(instance); PropertyInfo property = instance.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance); if (property != null) return property.GetValue(instance, null); throw new MissingMemberException(instance.GetType().FullName, name); }
        private static FieldInfo FindField(Type type, string name) { for (Type t = type; t != null; t = t.BaseType) { FieldInfo f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly); if (f != null) return f; } return null; }
        private static double GetDouble(FieldInfo field, object instance) => Convert.ToDouble(field.GetValue(instance), CultureInfo.InvariantCulture);
        private static double SnapshotAmount(InventoryMutationRow row) => !row.HasProviderSnapshot ? row.BeforeAmount : GetDouble(row.SnapshotAmountField, row.ProviderSnapshot);
        private static bool ValidNonnegative(double value) => value >= 0 && !Double.IsNaN(value) && !Double.IsInfinity(value);
        private static double Tolerance(double before, double after, double requested) => Math.Min(Math.Abs(requested) * 1e-6, Math.Max(1e-12, Math.Max(Math.Abs(before), Math.Abs(after)) * Epsilon * 2));
        private static InventoryPreflightResult Fail(string reason, InventoryObservation observation = null) => new InventoryPreflightResult { Accepted = false, Reason = reason, Observation = observation };
        private static string Bound(string value) { if (String.IsNullOrWhiteSpace(value)) return "BRP provider operation failed"; value = value.Replace('\r', ' ').Replace('\n', ' '); return value.Length <= 256 ? value : value.Substring(0, 256); }
    }
}
