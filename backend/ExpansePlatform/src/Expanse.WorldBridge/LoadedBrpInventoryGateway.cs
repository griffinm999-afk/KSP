using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Expanse.Domain;

namespace Expanse.WorldBridge
{
    // BRP 0.2.7 explicitly stops background simulation while Vessel.loaded, even
    // when a Foundations base is packed. PartResource is the sole loaded authority.
    // BRP may retain a prior SaveVessel mirror or may have cleared its inventories
    // in LoadVessel. Mirror rows are synchronized when present; absence is normal.
    // This adapter never calls SaveVessel, RecordVesselState, or another producer.
    internal sealed class LoadedBrpInventoryGateway : IInventoryGateway
    {
        internal const string ProviderId = "BackgroundResourceProcessing.Loaded";
        private const string ProcessorType = "BackgroundResourceProcessing.BackgroundResourceProcessor";
        private const int MaxVessels = 512, MaxParts = 32768, MaxRows = 4096;
        private sealed class Entry
        {
            internal Part Part;
            internal uint FlightId;
            internal PartResource Resource;
            internal object Inventory;
            internal ProtoPartResourceSnapshot Snapshot;
            internal FieldInfo Amount, Original;
        }
        private sealed class Context
        {
            internal Vessel Vessel;
            internal ProtoVessel Proto;
            internal bool Packed;
            internal object Processor;
            internal InventoryEndpoint Endpoint;
            internal Dictionary<string, Entry> Entries;
            internal string Token;
        }

        public InventoryCapabilityEvidence Describe(InventoryEndpoint endpoint)
        {
            bool available = Resolve(endpoint) != null;
            return new InventoryCapabilityEvidence { ProviderId = ProviderId, ProviderVersion = "0.2.7", Scene = endpoint == null ? "unknown" : endpoint.Scene,
                ObservationAvailable = available, ReadSupported = available, WriteSupported = available,
                SynchronousRollbackSupported = available, PersistenceSyncSupported = available,
                HoldReason = available ? null : "Selected loaded BRP inventory is unavailable, ambiguous, or outside the supported bounds" };
        }

        public InventoryResolution Resolve(InventoryEndpoint endpoint)
        {
            if (!RemoteBrpInventoryGateway.SupportedProviderAvailable || !HighLogic.LoadedSceneIsFlight || FlightGlobals.Vessels == null || !ValidEndpoint(endpoint)) return null;
            try
            {
                Vessel owner = null; int vessels = 0, parts = 0;
                var ids = new HashSet<uint>(endpoint.MemberPersistentIds);
                var counts = ids.ToDictionary(x => x, _ => 0);
                foreach (Vessel candidate in FlightGlobals.Vessels)
                {
                    if (++vessels > MaxVessels) return null;
                    if (candidate == null) continue;
                    if (candidate.loaded)
                    {
                        if (candidate.parts == null) return null;
                        foreach (Part part in candidate.parts)
                        {
                            if (++parts > MaxParts || part == null) return null;
                            if (ids.Contains(part.persistentId)) counts[part.persistentId]++;
                            if (part.persistentId == endpoint.AnchorPersistentId) { if (owner != null && owner != candidate) return null; owner = candidate; }
                        }
                    }
                    else if (candidate.protoVessel != null && candidate.protoVessel.protoPartSnapshots != null)
                    {
                        foreach (ProtoPartSnapshot part in candidate.protoVessel.protoPartSnapshots)
                        { if (++parts > MaxParts || part == null) return null; if (ids.Contains(part.persistentId)) counts[part.persistentId]++; }
                    }
                }
                if (owner == null || !owner.loaded || owner.vesselType == VesselType.EVA || counts.Any(x => x.Value != 1)) return null;
                var processors = Sequence(Read(owner, "vesselModules")).Where(x => x != null && x.GetType().FullName == ProcessorType).ToArray();
                if (processors.Length != 1) return null;
                var mirrors = new Dictionary<string, object>(StringComparer.Ordinal);
                int rows = 0;
                foreach (object inventory in Sequence(Read(processors[0], "Inventories")))
                {
                    if (++rows > MaxRows || inventory == null) return null;
                    if (Read(inventory, "ModuleId") != null) continue;
                    uint flight = Convert.ToUInt32(Read(inventory, "FlightId"), CultureInfo.InvariantCulture);
                    var name = Read(inventory, "ResourceName") as string;
                    if (flight == 0 || String.IsNullOrWhiteSpace(name) || mirrors.ContainsKey(Key(flight, name))) return null;
                    mirrors.Add(Key(flight, name), inventory);
                }
                var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
                rows = 0;
                foreach (uint id in endpoint.MemberPersistentIds)
                {
                    Part part = owner.parts.SingleOrDefault(x => x.persistentId == id);
                    if (part == null || part.Resources == null || part.flightID == 0 || owner.parts.Count(x => x.flightID == part.flightID) != 1) return null;
                    foreach (PartResource resource in part.Resources)
                    {
                        if (++rows > 512 || resource == null || String.IsNullOrWhiteSpace(resource.resourceName) || !Stock(resource.amount, resource.maxAmount)) return null;
                        object mirror; mirrors.TryGetValue(Key(part.flightID, resource.resourceName), out mirror);
                        var entry = new Entry { Part = part, FlightId = part.flightID, Resource = resource, Inventory = mirror };
                        if (mirror != null)
                        {
                            entry.Amount = Field(mirror.GetType(), "Amount"); entry.Original = Field(mirror.GetType(), "OriginalAmount");
                            if (entry.Amount == null || entry.Original == null || entry.Amount.FieldType != typeof(double) || entry.Original.FieldType != typeof(double) ||
                                !Stock(Dbl(entry.Amount.GetValue(mirror)), resource.maxAmount) || !Stock(Dbl(entry.Original.GetValue(mirror)), resource.maxAmount) || Dbl(Read(mirror, "MaxAmount")) != resource.maxAmount) return null;
                            object snapshot = Read(mirror, "Snapshot");
                            if (snapshot != null)
                            {
                                entry.Snapshot = snapshot as ProtoPartResourceSnapshot;
                                if (entry.Snapshot == null || entry.Snapshot.resourceName != resource.resourceName || entry.Snapshot.maxAmount != resource.maxAmount ||
                                    !Stock(entry.Snapshot.amount, entry.Snapshot.maxAmount) || !ValidConfig(entry.Snapshot)) return null;
                            }
                        }
                        var key = Key(id, resource.resourceName); if (entries.ContainsKey(key)) return null; entries.Add(key, entry);
                    }
                }
                var copy = new InventoryEndpoint { DepotId = endpoint.DepotId, MembershipRevision = endpoint.MembershipRevision,
                    MemberPersistentIds = (uint[])endpoint.MemberPersistentIds.Clone(), AnchorPersistentId = endpoint.AnchorPersistentId,
                    MembershipHash = endpoint.MembershipHash, MemberSetHash = endpoint.MemberSetHash, Scene = endpoint.Scene };
                var context = new Context { Vessel = owner, Proto = owner.protoVessel, Packed = owner.packed, Processor = processors[0], Endpoint = copy, Entries = entries, Token = Guid.NewGuid().ToString("N") };
                return new InventoryResolution(copy, context, context.Token);
            }
            catch { return null; }
        }

        public InventoryObservation Observe(InventoryResolution resolution)
        {
            var context = Current(resolution); if (context == null) return null;
            var rows = new List<InventoryStockRow>();
            foreach (var entry in context.Entries.Values)
            {
                if (!Stock(entry.Resource.amount, entry.Resource.maxAmount)) return null;
                rows.Add(new InventoryStockRow { MemberPersistentId = entry.Part.persistentId, ResourceName = entry.Resource.resourceName,
                    Amount = entry.Resource.amount, Capacity = entry.Resource.maxAmount, DebitAllowed = entry.Resource.flowState });
            }
            var sorted = rows.OrderBy(x => x.MemberPersistentId).ThenBy(x => x.ResourceName, StringComparer.Ordinal).ToArray();
            return new InventoryObservation { ContextToken = context.Token, ProviderId = ProviderId, ProviderVersion = "0.2.7", MembershipRevision = context.Endpoint.MembershipRevision,
                Rows = sorted, ObservationVersion = String.Join("|", sorted.Select(x => x.MemberPersistentId + ":" + x.ResourceName + ":" + x.Amount.ToString("R", CultureInfo.InvariantCulture) + ":" + x.Capacity.ToString("R", CultureInfo.InvariantCulture) + ":" + x.DebitAllowed)) };
        }

        public InventoryPreflightResult Preflight(InventoryResolution resolution, IList<InventoryDelta> deltas)
        {
            var context = Current(resolution); var observed = Observe(resolution);
            if (context == null || observed == null) return Fail("Loaded inventory context changed.");
            if (deltas == null || deltas.Count == 0 || deltas.Count > 16) return Fail("Loaded transfer requires 1–16 selected resource rows.", observed);
            var seen = new HashSet<string>(StringComparer.Ordinal); var rows = new List<InventoryMutationRow>();
            foreach (var delta in deltas)
            {
                Entry entry;
                if (delta == null || delta.MemberPersistentId == 0 || String.IsNullOrWhiteSpace(delta.ResourceName) || delta.DeltaMicroUnits == 0 ||
                    !seen.Add(Key(delta.MemberPersistentId, delta.ResourceName)) || !context.Entries.TryGetValue(Key(delta.MemberPersistentId, delta.ResourceName), out entry)) return Fail("Loaded transfer names a duplicate or unregistered resource.", observed);
                PartResource resource = entry.Resource;
                if (delta.DeltaMicroUnits < 0 && !resource.flowState) return Fail("Selected source resource is flow locked.", observed);
                double change = delta.DeltaMicroUnits / 1000000d, after = resource.amount + change;
                if (!Stock(after, resource.maxAmount) || after == resource.amount || Math.Abs((after - resource.amount) - change) > Math.Min(Math.Abs(change) * 1e-6, Math.Max(1e-12, Math.Max(Math.Abs(resource.amount), Math.Abs(after)) * 4.440892098500626e-16))) return Fail("Loaded resource delta cannot be represented exactly within stock/capacity.", observed);
                rows.Add(new InventoryMutationRow { Resource = resource, ProtoResource = entry.Snapshot, ProviderInventory = entry.Inventory,
                    ProviderSnapshot = entry.Snapshot, ProviderAmountField = entry.Amount, ProviderOriginalAmountField = entry.Original,
                    MemberPersistentId = delta.MemberPersistentId, ResourceName = delta.ResourceName, BeforeAmount = resource.amount,
                    BeforeProviderAmount = entry.Inventory == null ? 0 : Dbl(entry.Amount.GetValue(entry.Inventory)),
                    BeforeOriginalAmount = entry.Inventory == null ? 0 : Dbl(entry.Original.GetValue(entry.Inventory)),
                    BeforeSnapshotAmount = entry.Snapshot == null ? 0 : entry.Snapshot.amount, HasProviderSnapshot = entry.Snapshot != null,
                    BeforeConfigAmount = entry.Snapshot == null ? null : Single(Node(entry.Snapshot), "amount"),
                    BeforeConfigCapacity = entry.Snapshot == null ? null : Single(Node(entry.Snapshot), "maxAmount"),
                    BeforeFlowState = resource.flowState, IntendedAfterAmount = after, Capacity = resource.maxAmount, DeltaMicroUnits = delta.DeltaMicroUnits });
            }
            rows.Sort((a,b) => { int c = a.MemberPersistentId.CompareTo(b.MemberPersistentId); return c == 0 ? String.CompareOrdinal(a.ResourceName, b.ResourceName) : c; });
            return new InventoryPreflightResult { Accepted = true, Observation = observed, Plan = new InventoryMutationPlan(resolution, context.Token, rows) };
        }

        public InventoryApplyResult Apply(InventoryMutationPlan plan)
        {
            var context = plan == null ? null : Current(plan.Resolution);
            if (context == null || plan.AttemptedCount != 0 || !RowCurrent(plan, context)) return new InventoryApplyResult { Plan = plan, Reason = "Loaded context changed or plan was already attempted.", Rows = new InventoryAppliedRow[0] };
            var actual = new List<InventoryAppliedRow>();
            try
            {
                foreach (var row in plan.Rows)
                {
                    if(!RowCurrent(plan, context))throw new InvalidOperationException("Selected authority or loaded provider changed before row mutation.");
                    if (!BeforeMatches(row)) throw new InvalidOperationException("Loaded baseline changed after preflight.");
                    if(!RowCurrent(plan, context))throw new InvalidOperationException("Selected authority or loaded provider changed during baseline readback.");
                    plan.AttemptedCount++; Write(row, row.IntendedAfterAmount);
                    actual.Add(Applied(row));
                    if(!RowCurrent(plan, context))throw new InvalidOperationException("Selected authority or loaded provider changed after row mutation.");
                }
                Call(context.Processor, "MarkDirty");
                if(!RowCurrent(plan, context))throw new InvalidOperationException("Selected authority or loaded provider changed during dirty callback.");
                return new InventoryApplyResult { Succeeded = true, Plan = plan, Rows = actual.ToArray() };
            }
            catch (Exception ex) { return new InventoryApplyResult { Plan = plan, Rows = actual.ToArray(), Reason = "Loaded write failed: " + ex.GetType().Name + ": " + ex.Message }; }
        }

        public InventorySyncResult Synchronize(InventoryMutationPlan plan)
        {
            var context = plan == null ? null : Current(plan.Resolution);
            if (context == null || !RowCurrent(plan, context)) return new InventorySyncResult { Reason = "Loaded provider context changed before readback." };
            foreach (var row in plan.Rows)
            {
                if (!RowCurrent(plan, context) || row.Resource.amount != row.IntendedAfterAmount || row.Resource.maxAmount != row.Capacity || row.Resource.flowState != row.BeforeFlowState ||
                    (row.ProviderInventory != null && (Dbl(row.ProviderAmountField.GetValue(row.ProviderInventory)) != row.IntendedAfterAmount || Dbl(row.ProviderOriginalAmountField.GetValue(row.ProviderInventory)) != row.IntendedAfterAmount)) ||
                    (row.ProtoResource != null && (row.ProtoResource.amount != row.IntendedAfterAmount || !ValidConfig(row.ProtoResource))))
                    return new InventorySyncResult { Reason = "Loaded physical/provider/config readback differs from intended stock." };
                if (!RowCurrent(plan, context)) return new InventorySyncResult { Reason = "Selected authority or loaded provider changed during readback." };
            }
            return new InventorySyncResult { Succeeded = true };
        }

        public InventoryRollbackResult Rollback(InventoryMutationPlan plan)
        {
            var context = plan == null ? null : Current(plan.Resolution);
            if (context == null || !RowCurrent(plan, context)) return new InventoryRollbackResult { Reason = "Loaded provider context vanished; rollback is held.", Rows = new InventoryAppliedRow[0] };
            bool confirmed = true;
            for (int i = Math.Min(plan.AttemptedCount, plan.Rows.Count) - 1; i >= 0; --i)
            {
                if(!RowCurrent(plan, context)){confirmed=false;break;}
                var row = plan.Rows[i];
                try
                {
                    row.Resource.amount = row.BeforeAmount;
                    if (row.ProviderInventory != null) { row.ProviderAmountField.SetValue(row.ProviderInventory, row.BeforeProviderAmount); row.ProviderOriginalAmountField.SetValue(row.ProviderInventory, row.BeforeOriginalAmount); }
                    if (row.ProtoResource != null)
                    {
                        row.ProtoResource.amount = row.BeforeSnapshotAmount;
                        if (!Node(row.ProtoResource).SetValue("amount", row.BeforeConfigAmount, false)) confirmed = false;
                        if (!RowCurrent(plan, context)) { confirmed = false; break; }
                        if (!Node(row.ProtoResource).SetValue("maxAmount", row.BeforeConfigCapacity, false)) confirmed = false;
                    }
                }
                catch { confirmed = false; }
                if(!RowCurrent(plan, context)){confirmed=false;break;}
            }
            foreach (var row in plan.Rows)
            {
                if (!RowCurrent(plan, context) || !BeforeMatches(row)) confirmed = false;
                if (!RowCurrent(plan, context)) { confirmed = false; break; }
            }
            confirmed &= RowCurrent(plan, context);
            return new InventoryRollbackResult { Confirmed = confirmed, Rows = plan.Rows.Select(Applied).ToArray(), Reason = confirmed ? null : "Loaded rollback readback could not be confirmed." };
        }

        // Selected colony callers fence every callback-bearing row against the
        // retained native topology as well as registry/save authority. Legacy
        // callers without the optional callback retain their existing behavior.
        static bool RowCurrent(InventoryMutationPlan plan, Context context) => plan.AuthorityCurrent() &&
            (plan.AuthorityIsCurrent == null || SelectedCurrent(plan.Resolution, context));

        internal bool SelectedContextCurrent(InventoryResolution resolution) => SelectedCurrent(resolution, resolution == null ? null : resolution.ProviderContext as Context);
        static bool SelectedCurrent(InventoryResolution resolution, Context context)
        {
            try { return context != null && Object.ReferenceEquals(Current(resolution), context) &&
                Object.ReferenceEquals(context.Vessel.protoVessel, context.Proto) && context.Entries.Values.All(e =>
                    e.Part.flightID == e.FlightId && context.Vessel.parts.Count(p => p != null && p.flightID == e.FlightId) == 1); }
            catch { return false; }
        }

        static Context Current(InventoryResolution resolution)
        {
            var context = resolution == null ? null : resolution.ProviderContext as Context;
            if (context == null || context.Endpoint != resolution.Endpoint || context.Token != resolution.ContextToken || !HighLogic.LoadedSceneIsFlight ||
                !RemoteBrpInventoryGateway.SupportedProviderAvailable || context.Vessel == null || !context.Vessel.loaded || context.Vessel.packed != context.Packed || context.Vessel.parts == null || FlightGlobals.Vessels == null || !FlightGlobals.Vessels.Contains(context.Vessel)) return null;
            try
            {
                var processors = Sequence(Read(context.Vessel, "vesselModules")).Where(x => x != null && x.GetType().FullName == ProcessorType).ToArray();
                if (processors.Length != 1 || processors[0] != context.Processor) return null;
                var mirrors = Sequence(Read(context.Processor, "Inventories")).ToArray();
                if (mirrors.Length > MaxRows) return null;
                foreach (var pair in context.Entries)
                {
                    var entry = pair.Value;
                    if (!context.Vessel.parts.Contains(entry.Part) || !context.Endpoint.MemberPersistentIds.Contains(entry.Part.persistentId) ||
                        Key(entry.Part.persistentId, entry.Resource.resourceName) != pair.Key || context.Vessel.parts.Count(x => x.persistentId == entry.Part.persistentId) != 1 ||
                        entry.Part.Resources == null || !entry.Part.Resources.Contains(entry.Resource)) return null;
                    var current = mirrors.Where(x => x != null && Read(x,"ModuleId") == null &&
                        Convert.ToUInt32(Read(x,"FlightId"),CultureInfo.InvariantCulture) == entry.Part.flightID &&
                        String.Equals(Read(x,"ResourceName") as string,entry.Resource.resourceName,StringComparison.Ordinal)).ToArray();
                    if (current.Length != (entry.Inventory == null ? 0 : 1) || (entry.Inventory != null &&
                        (!Object.ReferenceEquals(current[0],entry.Inventory) || !Object.ReferenceEquals(Read(entry.Inventory,"Snapshot"),entry.Snapshot) || Dbl(Read(entry.Inventory,"MaxAmount")) != entry.Resource.maxAmount))) return null;
                }
                return context;
            }
            catch { return null; }
        }
        static bool BeforeMatches(InventoryMutationRow row)
        {
            try { return row.Resource != null && row.Resource.amount == row.BeforeAmount && row.Resource.maxAmount == row.Capacity && row.Resource.flowState == row.BeforeFlowState &&
                (row.ProviderInventory == null || (Dbl(row.ProviderAmountField.GetValue(row.ProviderInventory)) == row.BeforeProviderAmount && Dbl(row.ProviderOriginalAmountField.GetValue(row.ProviderInventory)) == row.BeforeOriginalAmount)) &&
                (row.ProtoResource == null || (row.ProtoResource.amount == row.BeforeSnapshotAmount && Single(Node(row.ProtoResource), "amount") == row.BeforeConfigAmount && Single(Node(row.ProtoResource), "maxAmount") == row.BeforeConfigCapacity)); }
            catch { return false; }
        }
        static void Write(InventoryMutationRow row, double amount)
        {
            row.Resource.amount = amount;
            if (row.ProviderInventory != null) { row.ProviderAmountField.SetValue(row.ProviderInventory, amount); row.ProviderOriginalAmountField.SetValue(row.ProviderInventory, amount); }
            if (row.ProtoResource != null) { row.ProtoResource.amount = amount; row.ProtoResource.UpdateConfigNodeAmounts(); }
        }
        static InventoryAppliedRow Applied(InventoryMutationRow row) => new InventoryAppliedRow { MemberPersistentId = row.MemberPersistentId, ResourceName = row.ResourceName, BeforeAmount = row.BeforeAmount, IntendedAfterAmount = row.IntendedAfterAmount, ObservedAfterAmount = row.Resource.amount, ActualDelta = row.Resource.amount - row.BeforeAmount };
        static bool ValidEndpoint(InventoryEndpoint e) => e != null && !String.IsNullOrWhiteSpace(e.DepotId) && e.MembershipRevision > 0 && e.AnchorPersistentId != 0 && e.MemberPersistentIds != null && e.MemberPersistentIds.Length > 0 && e.MemberPersistentIds.Length <= 64 && e.MemberPersistentIds.All(x => x != 0) && e.MemberPersistentIds.Distinct().Count() == e.MemberPersistentIds.Length && e.MemberPersistentIds.Contains(e.AnchorPersistentId) && e.MemberSetHash == OperationIdentity.ComputeMemberSetHash(e.AnchorPersistentId, e.MemberPersistentIds);
        static bool Stock(double a, double c) => !Double.IsNaN(a) && !Double.IsInfinity(a) && !Double.IsNaN(c) && !Double.IsInfinity(c) && a >= 0 && c >= 0 && a <= c;
        static bool ValidConfig(ProtoPartResourceSnapshot snapshot) { double amount, capacity; return Double.TryParse(Single(Node(snapshot), "amount"), NumberStyles.Float, CultureInfo.InvariantCulture, out amount) && Double.TryParse(Single(Node(snapshot), "maxAmount"), NumberStyles.Float, CultureInfo.InvariantCulture, out capacity) && amount == snapshot.amount && capacity == snapshot.maxAmount; }
        static string Single(ConfigNode node, string key) { var values = node == null ? null : node.GetValues(key); if (values == null || values.Length != 1) throw new InvalidOperationException("Ambiguous resource config."); return values[0]; }
        static ConfigNode Node(ProtoPartResourceSnapshot snapshot) => Read(snapshot, "resourceValues") as ConfigNode;
        static double Dbl(object value) => Convert.ToDouble(value, CultureInfo.InvariantCulture);
        static string Key(uint id, string name) => id.ToString(CultureInfo.InvariantCulture) + "\0" + name;
        static InventoryPreflightResult Fail(string reason, InventoryObservation observed = null) => new InventoryPreflightResult { Reason = reason, Observation = observed };
        static FieldInfo Field(Type type, string name) => type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        static object Read(object value, string name) { var field = Field(value.GetType(), name); if (field != null) return field.GetValue(value); var property = value.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance); if (property == null) throw new MissingMemberException(name); return property.GetValue(value, null); }
        static IEnumerable<object> Sequence(object value) { var values = value as IEnumerable; if (values == null) throw new InvalidOperationException("Missing provider collection."); return values.Cast<object>(); }
        static void Call(object value, string name) { var method = value.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null); if (method == null) throw new MissingMethodException(name); method.Invoke(value, null); }
    }
}
