using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Expanse.Domain;

namespace Expanse.WorldBridge
{
    // BRP 0.2.7 unloaded-vessel gateway. Every effect still requires fresh
    // selected-vessel resolution, provider-owned stock, and bounded preflight.
    internal sealed class RemoteBrpInventoryGateway : IInventoryGateway
    {
        internal const string ProviderId = "BackgroundResourceProcessing.Remote";
        private const string ProviderType = "BackgroundResourceProcessing.BackgroundResourceProcessor";
        private const string Hold = "A supported BRP 0.2.7 game context is unavailable";
        private const int MaxVessels = 512, MaxParts = 16384, MaxResources = 4096, MaxMembers = 64, MaxDeltas = 16;
        private const double MicroUnits = 1000000.0;
        private static readonly HashSet<string> LoggedDevResolutionFailures = new HashSet<string>(StringComparer.Ordinal);
        private static Assembly verifiedProvider;

        internal static bool SupportedProviderAvailable
        {
            get
            {
                if (!HighLogic.LoadedSceneIsGame || HighLogic.CurrentGame == null) return false;
                if (verifiedProvider != null) return true;
                try
                {
                    Assembly[] providers = AppDomain.CurrentDomain.GetAssemblies()
                        .Where(x => String.Equals(x.GetName().Name, "BackgroundResourceProcessing", StringComparison.Ordinal)).ToArray();
                    if (providers.Length != 1 || !new Version(0, 2, 7, 0).Equals(providers[0].GetName().Version) ||
                        providers[0].GetType(ProviderType, false) == null) return false;
                    verifiedProvider = providers[0];
                    return true;
                }
                catch { return false; }
            }
        }

        private static InventoryResolution Fail(InventoryEndpoint endpoint, string reason)
        {
            if (WorldBridgeAddon.RemotePhysicalDiagnosticsEnabled)
            {
                string depot = endpoint == null ? "null" : endpoint.DepotId ?? "null";
                string key = depot + "|" + reason;
                lock (LoggedDevResolutionFailures)
                    if (LoggedDevResolutionFailures.Count < 48 && LoggedDevResolutionFailures.Add(key))
                        UnityEngine.Debug.Log("[ExpanseRemoteBrp] dev resolve hold depot=" + depot + " reason=" + reason);
            }
            return null;
        }

        private sealed class Entry
        {
            internal ProtoPartSnapshot Part;
            internal ProtoPartResourceSnapshot Snapshot;
            internal object Inventory;
            internal FieldInfo Amount, Original;
            internal uint PersistentId, FlightId;
            internal string ResourceName;
        }
        private sealed class Context
        {
            internal Vessel Vessel;
            internal ProtoVessel Proto;
            internal object Processor;
            internal InventoryEndpoint Endpoint;
            internal Dictionary<string, Entry> Entries;
            internal string Token;
            internal double CaughtUpUt;
            internal string ProportionalRefreshHold;
        }

        public InventoryCapabilityEvidence Describe(InventoryEndpoint endpoint)
        {
            // Capability publication is frequent. It only inspects the last
            // provider-owned state; fresh catch-up occurs in stock observation
            // and again before every effect preflight.
            InventoryResolution resolution = Resolve(endpoint, false);
            bool available = resolution != null;
            bool write = available && WorldBridgeAddon.RemotePhysicalCandidateEnabled;
            return new InventoryCapabilityEvidence { ProviderId = ProviderId, ProviderVersion = "0.2.7", Scene = endpoint == null ? "unknown" : endpoint.Scene,
                ObservationAvailable = available, ReadSupported = available, WriteSupported = write,
                SynchronousRollbackSupported = write, PersistenceSyncSupported = write,
                HoldReason = available ? (write ? null : Hold) : "Selected unloaded BRP vessel or provider inventory is unavailable" };
        }

        public InventoryResolution Resolve(InventoryEndpoint endpoint) { return Resolve(endpoint, true); }

        private InventoryResolution Resolve(InventoryEndpoint endpoint, bool catchUp)
        {
            if (!ValidEndpoint(endpoint)) return Fail(endpoint, "invalid endpoint anchor/member-set/revision hash scene=" + (endpoint == null ? "null" : endpoint.Scene));
            if (!HighLogic.LoadedSceneIsGame || FlightGlobals.Vessels == null) return Fail(endpoint, "game scene or FlightGlobals.Vessels unavailable scene=" + (endpoint.Scene ?? "null"));
            try
            {
                Vessel owner = null; int vessels = 0, partScan = 0;
                HashSet<uint> selectedIds = new HashSet<uint>(endpoint.MemberPersistentIds);
                Dictionary<uint, int> globalSelectedCounts = selectedIds.ToDictionary(x => x, _ => 0);
                foreach (Vessel candidate in FlightGlobals.Vessels)
                {
                    if (++vessels > MaxVessels) return Fail(endpoint, "vessel scan bound");
                    if (candidate == null) continue;
                    // Loaded Part identities are authoritative even when a
                    // previous proto snapshot is absent or stale. Count each
                    // vessel through exactly one ownership source.
                    if (candidate.loaded)
                    {
                        if (candidate.parts == null || candidate.parts.Count > MaxParts) return Fail(endpoint, "loaded part scan bound/unavailable vessel=" + candidate.id);
                        foreach (Part part in candidate.parts)
                        {
                            if (++partScan > 32768 || part == null) return Fail(endpoint, "global loaded part scan bound/null row");
                            if (selectedIds.Contains(part.persistentId)) globalSelectedCounts[part.persistentId]++;
                        }
                        continue;
                    }
                    if (candidate.protoVessel == null || candidate.protoVessel.protoPartSnapshots == null) continue;
                    if (candidate.protoVessel.protoPartSnapshots.Count > MaxParts) return Fail(endpoint, "part scan bound on vessel=" + candidate.id);
                    partScan += candidate.protoVessel.protoPartSnapshots.Count;
                    if (partScan > 32768) return Fail(endpoint, "global part scan bound");
                    foreach (ProtoPartSnapshot part in candidate.protoVessel.protoPartSnapshots)
                        if (part != null && selectedIds.Contains(part.persistentId)) globalSelectedCounts[part.persistentId]++;
                    if (!candidate.protoVessel.protoPartSnapshots.Any(p => p != null && p.persistentId == endpoint.AnchorPersistentId)) continue;
                    if (owner != null) return Fail(endpoint, "duplicate anchor owner=" + candidate.id);
                    owner = candidate;
                }
                if (owner == null) return Fail(endpoint, "anchor owner absent anchor=" + endpoint.AnchorPersistentId + " members=" + endpoint.MemberPersistentIds.Length);
                if (owner.loaded || owner.vesselType == VesselType.EVA) return Fail(endpoint, "owner loaded/EVA id=" + owner.id + " loaded=" + owner.loaded + " type=" + owner.vesselType);
                if (globalSelectedCounts.Any(x => x.Value != 1)) return Fail(endpoint, "selected member global count !=1 ids=" + String.Join(",", globalSelectedCounts.Where(x => x.Value != 1).Select(x => x.Key + ":" + x.Value)));
                ProtoVessel proto = owner.protoVessel;
                Dictionary<uint, ProtoPartSnapshot> selected = new Dictionary<uint, ProtoPartSnapshot>();
                foreach (uint id in endpoint.MemberPersistentIds)
                {
                    ProtoPartSnapshot[] matches = proto.protoPartSnapshots.Where(p => p != null && p.persistentId == id).ToArray();
                    if (matches.Length != 1 || matches[0].flightID == 0 ||
                        proto.protoPartSnapshots.Count(p => p != null && p.flightID == matches[0].flightID) != 1) return Fail(endpoint, "selected part/flightId mismatch member=" + id);
                    selected.Add(id, matches[0]);
                }
                List<object> processors = Sequence(Read(owner, "vesselModules")).Where(x => x != null && x.GetType().FullName == ProviderType).ToList();
                if (processors.Count != 1) return Fail(endpoint, "BRP processor count=" + processors.Count + " owner=" + owner.id);
                object processor = processors[0];
                if (catchUp) Call(processor, "UpdateBackgroundState");
                if (owner.loaded || !Object.ReferenceEquals(owner.protoVessel, proto)) return Fail(endpoint, "owner/proto changed during catch-up id=" + owner.id);

                Dictionary<string, List<object>> provider = new Dictionary<string, List<object>>(StringComparer.Ordinal);
                int scanned = 0;
                foreach (object inventory in Sequence(Read(processor, "Inventories")))
                {
                    if (++scanned > MaxResources || inventory == null) return Fail(endpoint, "provider inventory scan bound/null row");
                    if (Read(inventory, "ModuleId") != null) continue;
                    uint id = UInt(Read(inventory, "FlightId"));
                    string name = Read(inventory, "ResourceName") as string;
                    if (id == 0 || String.IsNullOrWhiteSpace(name)) return Fail(endpoint, "provider inventory invalid flightId/resource");
                    string key = Key(id, name);
                    List<object> list;
                    if (!provider.TryGetValue(key, out list)) provider[key] = list = new List<object>(1);
                    list.Add(inventory);
                }
                Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
                foreach (var selectedPart in selected)
                {
                    HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
                    int resources = 0;
                    foreach (ProtoPartResourceSnapshot snapshot in selectedPart.Value.resources)
                    {
                        if (++resources > MaxResources || snapshot == null || String.IsNullOrWhiteSpace(snapshot.resourceName) || !names.Add(snapshot.resourceName)) return Fail(endpoint, "selected resource bound/null/duplicate member=" + selectedPart.Key);
                        List<object> matches;
                        if (!provider.TryGetValue(Key(selectedPart.Value.flightID, snapshot.resourceName), out matches) || matches.Count != 1) return Fail(endpoint, "provider row missing/duplicate member=" + selectedPart.Key + " flightId=" + selectedPart.Value.flightID + " resource=" + snapshot.resourceName);
                        object inventory = matches[0];
                        FieldInfo amount = Field(inventory.GetType(), "Amount"), original = Field(inventory.GetType(), "OriginalAmount");
                        if (amount == null || original == null || amount.FieldType != typeof(double) || original.FieldType != typeof(double) ||
                            !Object.ReferenceEquals(Read(inventory, "Snapshot"), snapshot)) return Fail(endpoint, "provider field type/snapshot identity member=" + selectedPart.Key + " resource=" + snapshot.resourceName);
                        if (!Stock(snapshot.amount, snapshot.maxAmount) || !Stock(Dbl(amount.GetValue(inventory)), snapshot.maxAmount) ||
                            Dbl(amount.GetValue(inventory)) != snapshot.amount || Dbl(original.GetValue(inventory)) != snapshot.amount ||
                            Dbl(Read(inventory, "MaxAmount")) != snapshot.maxAmount) return Fail(endpoint, "provider amount/Original/Snapshot/Max incoherent member=" + selectedPart.Key + " resource=" + snapshot.resourceName + " snapshot=" + snapshot.amount.ToString("R", CultureInfo.InvariantCulture) + " amount=" + Dbl(amount.GetValue(inventory)).ToString("R", CultureInfo.InvariantCulture) + " original=" + Dbl(original.GetValue(inventory)).ToString("R", CultureInfo.InvariantCulture) + " max=" + snapshot.maxAmount.ToString("R", CultureInfo.InvariantCulture) + " providerMax=" + Dbl(Read(inventory, "MaxAmount")).ToString("R", CultureInfo.InvariantCulture));
                        string configAmount = Single(Node(snapshot), "amount"), configCapacity = Single(Node(snapshot), "maxAmount");
                        double parsedAmount, parsedCapacity;
                        // BRP 0.2.7 updates the ConfigNode before assigning the new
                        // Snapshot/Original amount. A producing or consuming resource
                        // can therefore have one provider tick of ConfigNode lag.
                        // The provider Amount=Original=Snapshot triple above is the
                        // stock authority. We only bound the old ConfigNode value;
                        // mutation rows capture its exact token for rollback.
                        if (!Double.TryParse(configAmount, NumberStyles.Float, CultureInfo.InvariantCulture, out parsedAmount) ||
                            !Double.TryParse(configCapacity, NumberStyles.Float, CultureInfo.InvariantCulture, out parsedCapacity) ||
                            !Stock(parsedAmount, parsedCapacity) || parsedCapacity != snapshot.maxAmount)
                            return Fail(endpoint, "ConfigNode amount/max invalid member=" + selectedPart.Key + " resource=" + snapshot.resourceName + " configAmount=" + (configAmount ?? "null") + " snapshotAmount=" + snapshot.amount.ToString("R", CultureInfo.InvariantCulture) + " configMax=" + (configCapacity ?? "null") + " snapshotMax=" + snapshot.maxAmount.ToString("R", CultureInfo.InvariantCulture));
                        entries.Add(Key(selectedPart.Key, snapshot.resourceName), new Entry { Part = selectedPart.Value, Snapshot = snapshot,
                            Inventory = inventory, Amount = amount, Original = original, PersistentId = selectedPart.Key, FlightId = selectedPart.Value.flightID, ResourceName = snapshot.resourceName });
                    }
                }
                if (entries.Count == 0 || entries.Count > MaxResources) return Fail(endpoint, "selected entry count=" + entries.Count);
                InventoryEndpoint copy = Copy(endpoint);
                Context context = new Context { Vessel = owner, Proto = proto, Processor = processor, Endpoint = copy, Entries = entries, Token = Guid.NewGuid().ToString("N"), CaughtUpUt = Dbl(Read(processor,"LastChangepoint")) };
                return new InventoryResolution(copy, context, context.Token);
            }
            catch (Exception ex) { return Fail(endpoint, "exception=" + ex.GetType().Name + " message=" + ex.Message); }
        }

        public InventoryObservation Observe(InventoryResolution resolution)
        {
            Context context = ContextFor(resolution);
            if (context == null) return null;
            List<InventoryStockRow> rows = new List<InventoryStockRow>();
            foreach (Entry entry in context.Entries.Values)
            {
                if (!Coherent(entry)) return null;
                rows.Add(new InventoryStockRow { MemberPersistentId = entry.PersistentId, ResourceName = entry.ResourceName,
                    Amount = entry.Snapshot.amount, Capacity = entry.Snapshot.maxAmount, DebitAllowed = entry.Snapshot.flowState });
            }
            rows = rows.OrderBy(x => x.MemberPersistentId).ThenBy(x => x.ResourceName, StringComparer.Ordinal).ToList();
            return new InventoryObservation { ContextToken = context.Token, ProviderId = ProviderId, ProviderVersion = "0.2.7",
                MembershipRevision = context.Endpoint.MembershipRevision, Rows = rows.ToArray(),
                ObservationVersion = String.Join("|", rows.Select(x => x.MemberPersistentId + ":" + x.ResourceName + ":" + x.Amount.ToString("R", CultureInfo.InvariantCulture) + ":" + x.Capacity.ToString("R", CultureInfo.InvariantCulture) + ":" + x.DebitAllowed)) };
        }

        public InventoryPreflightResult Preflight(InventoryResolution resolution, IList<InventoryDelta> deltas)
        {
            InventoryObservation observation = Observe(resolution);
            if (observation == null) return Failure("Remote BRP selected inventory changed or became unavailable");
            if (!WorldBridgeAddon.RemotePhysicalCandidateEnabled) return Failure(Hold, observation);
            if (deltas == null || deltas.Count < 1 || deltas.Count > MaxDeltas) return Failure("Remote intent exceeds the 16-row bound", observation);
            Context context = ContextFor(resolution);
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            List<InventoryMutationRow> rows = new List<InventoryMutationRow>();
            foreach (InventoryDelta delta in deltas)
            {
                if (delta == null || delta.MemberPersistentId == 0 || String.IsNullOrWhiteSpace(delta.ResourceName) || delta.DeltaMicroUnits == 0) return Failure("Invalid remote intent row", observation);
                string key = Key(delta.MemberPersistentId, delta.ResourceName);
                Entry entry;
                if (!seen.Add(key) || !context.Entries.TryGetValue(key, out entry) || !Coherent(entry))
                    return Failure("Remote selected resource is duplicate, absent, or stale", observation);
                if (delta.DeltaMicroUnits < 0 && !entry.Snapshot.flowState)
                    return Failure("Locked selected source tank cannot be debited", observation);
                double before = entry.Snapshot.amount, change = delta.DeltaMicroUnits / MicroUnits, after = before + change;
                if (!Stock(after, entry.Snapshot.maxAmount) || after == before || Math.Abs((after - before) - change) > 1e-9)
                    return Failure("Remote stock/capacity or micro-unit delta is invalid", observation);
                rows.Add(new InventoryMutationRow { ProtoResource = entry.Snapshot, ProviderInventory = entry.Inventory, ProviderSnapshot = entry.Snapshot,
                    ProviderAmountField = entry.Amount, ProviderOriginalAmountField = entry.Original,
                    SnapshotAmountField = Field(entry.Snapshot.GetType(), "amount"), MemberPersistentId = entry.PersistentId,
                    ResourceName = entry.ResourceName, BeforeAmount = before, BeforeProviderAmount = before,
                    BeforeOriginalAmount = before, BeforeSnapshotAmount = before, HasProviderSnapshot = true,
                    IntendedAfterAmount = after, Capacity = entry.Snapshot.maxAmount, DeltaMicroUnits = delta.DeltaMicroUnits,
                    BeforeConfigAmount = Single(Node(entry.Snapshot), "amount"), BeforeConfigCapacity = Single(Node(entry.Snapshot), "maxAmount"),
                    BeforeFlowState = entry.Snapshot.flowState });
            }
            rows.Sort((a, b) => { int c = a.MemberPersistentId.CompareTo(b.MemberPersistentId); return c == 0 ? String.CompareOrdinal(a.ResourceName, b.ResourceName) : c; });
            return new InventoryPreflightResult { Accepted = true, Observation = observation,
                Plan = new InventoryMutationPlan(resolution, context.Token, rows) };
        }

        public InventoryApplyResult Apply(InventoryMutationPlan plan)
        {
            Context context = plan == null ? null : ContextFor(plan.Resolution);
            if (!WorldBridgeAddon.RemotePhysicalCandidateEnabled || context == null || plan.AttemptedCount != 0 || !RowCurrent(plan, context))
                return new InventoryApplyResult { Plan = plan, Reason = Hold, Rows = new InventoryAppliedRow[0] };
            List<InventoryAppliedRow> rows = new List<InventoryAppliedRow>();
            try
            {
                int visitorRows, selectedOtherRows;
                Dictionary<string, string> unaffectedBefore = UnaffectedCargo(context, plan, out visitorRows, out selectedOtherRows);
                foreach (InventoryMutationRow row in plan.Rows)
                {
                    if(!RowCurrent(plan, context))throw new InvalidOperationException("Selected authority or remote provider changed before row mutation.");
                    if (!context.Endpoint.MemberPersistentIds.Contains(row.MemberPersistentId))
                        throw new InvalidOperationException("Remote mutation member is outside the selected depot");
                    if (!CurrentRow(context, row, row.BeforeAmount, true)) throw new InvalidOperationException("Remote preflight became stale");
                    if(!RowCurrent(plan, context))throw new InvalidOperationException("Selected authority or remote provider changed during baseline readback.");
                    plan.AttemptedCount++;
                    Write(row, row.IntendedAfterAmount);
                    rows.Add(new InventoryAppliedRow { MemberPersistentId = row.MemberPersistentId, ResourceName = row.ResourceName,
                        BeforeAmount = row.BeforeAmount, IntendedAfterAmount = row.IntendedAfterAmount,
                        ObservedAfterAmount = row.ProtoResource.amount, ActualDelta = row.ProtoResource.amount - row.BeforeAmount });
                    if(!RowCurrent(plan, context))throw new InvalidOperationException("Selected authority or remote provider changed after row mutation.");
                }
                // Writes are complete and synchronously read back before the
                // optional owner is asked to refresh at the caught-up boundary.
                // A rate refresh hold does not pretend this completed debit or
                // credit never happened; the physical receipt remains truthful.
                if(plan.Rows.Any(row=>!CurrentRow(context,row,row.IntendedAfterAmount,false)))throw new InvalidOperationException("Remote mutation readback differs before proportional refresh.");
                context.ProportionalRefreshHold=ColonyRuntime.InvalidateColonyBrpAfterMutation(context.Processor,context.CaughtUpUt);
                Call(context.Processor, "MarkDirty");
                if(!RowCurrent(plan, context))throw new InvalidOperationException("Selected authority or remote provider changed during dirty callback.");
                int visitorAfter, selectedOtherAfter;
                Dictionary<string, string> unaffectedAfter = UnaffectedCargo(context, plan, out visitorAfter, out selectedOtherAfter);
                if(!RowCurrent(plan, context))throw new InvalidOperationException("Selected authority or remote provider changed during cargo readback.");
                if (visitorRows != visitorAfter || selectedOtherRows != selectedOtherAfter ||
                    unaffectedBefore.Count != unaffectedAfter.Count ||
                    unaffectedBefore.Any(x => !unaffectedAfter.ContainsKey(x.Key) || unaffectedAfter[x.Key] != x.Value))
                    throw new InvalidOperationException("Unselected cargo row changed inside remote BRP effect boundary");
                UnityEngine.Debug.Log("[ExpanseRemoteBrp] atomic selected write depot=" + context.Endpoint.DepotId +
                    " vessel=" + context.Vessel.id + " mutationRows=" + plan.Rows.Count +
                    " visitorCargoRows=" + visitorRows + " selectedOtherCargoRows=" + selectedOtherRows + " unchanged=true");
                return new InventoryApplyResult { Succeeded = true, Plan = plan, Rows = rows.ToArray(), Reason = context.ProportionalRefreshHold };
            }
            catch (Exception ex) { return new InventoryApplyResult { Plan = plan, Reason = "Remote BRP apply failed: " + ex.GetType().Name + ": " + ex.Message, Rows = rows.ToArray() }; }
        }

        public InventoryRollbackResult Rollback(InventoryMutationPlan plan)
        {
            Context context = plan == null ? null : ContextFor(plan.Resolution);
            if (context == null || !RowCurrent(plan, context)) return new InventoryRollbackResult { Reason = "Remote context vanished before rollback", Rows = new InventoryAppliedRow[0] };
            bool failed = false;
            for (int i = Math.Min(plan.AttemptedCount, plan.Rows.Count) - 1; i >= 0; --i)
            {
                if(!RowCurrent(plan, context)){failed=true;break;}
                InventoryMutationRow row = plan.Rows[i];
                try { Restore(plan, context, row); } catch { failed = true; }
                if(!RowCurrent(plan, context)){failed=true;break;}
            }
            try
            {
                if(RowCurrent(plan, context))
                {
                    if(plan.Rows.Any(row=>!CurrentRow(context,row,row.BeforeAmount,true)))failed=true;
                    context.ProportionalRefreshHold=ColonyRuntime.InvalidateColonyBrpAfterMutation(context.Processor,context.CaughtUpUt);
                    Call(context.Processor, "MarkDirty");
                }
                else failed=true;
            }
            catch { failed = true; }
            List<InventoryAppliedRow> observed = new List<InventoryAppliedRow>();
            foreach (InventoryMutationRow row in plan.Rows)
            {
                if (!RowCurrent(plan, context)||!CurrentRow(context, row, row.BeforeAmount, true)) failed = true;
                double actual = row.ProtoResource == null ? Double.NaN : row.ProtoResource.amount;
                observed.Add(new InventoryAppliedRow { MemberPersistentId = row.MemberPersistentId, ResourceName = row.ResourceName,
                    BeforeAmount = row.BeforeAmount, IntendedAfterAmount = row.IntendedAfterAmount,
                    ObservedAfterAmount = actual, ActualDelta = actual - row.BeforeAmount });
                if (!RowCurrent(plan, context)) { failed = true; break; }
            }
            failed|=!RowCurrent(plan, context);return new InventoryRollbackResult { Confirmed = !failed, Reason = failed ? "Remote BRP rollback could not be confirmed" : context.ProportionalRefreshHold, Rows = observed.ToArray() };
        }

        public InventorySyncResult Synchronize(InventoryMutationPlan plan)
        {
            Context context = plan == null ? null : ContextFor(plan.Resolution);
            if (context == null || !RowCurrent(plan, context) || !WorldBridgeAddon.RemotePhysicalCandidateEnabled) return new InventorySyncResult { Reason = Hold };
            foreach (InventoryMutationRow row in plan.Rows)
            {
                if (!RowCurrent(plan, context)||!CurrentRow(context, row, row.IntendedAfterAmount, false))
                    return new InventorySyncResult { Reason = "Remote BRP snapshot, inventory, or ConfigNode readback failed" };
                if (!RowCurrent(plan, context)) return new InventorySyncResult { Reason = "Selected authority or remote provider changed during readback." };
            }
            return new InventorySyncResult { Succeeded = true };
        }

        private static void Write(InventoryMutationRow row, double value)
        {
            row.ProviderAmountField.SetValue(row.ProviderInventory, value);
            row.ProviderOriginalAmountField.SetValue(row.ProviderInventory, value);
            row.ProtoResource.amount = value;
            row.ProtoResource.UpdateConfigNodeAmounts();
        }
        private static Dictionary<string, string> UnaffectedCargo(Context context, InventoryMutationPlan plan,
            out int visitorRows, out int selectedOtherRows)
        {
            visitorRows = 0; selectedOtherRows = 0;
            HashSet<uint> selected = new HashSet<uint>(context.Endpoint.MemberPersistentIds);
            HashSet<string> changed = new HashSet<string>(plan.Rows.Select(x => Key(x.MemberPersistentId, x.ResourceName)), StringComparer.Ordinal);
            Dictionary<string, string> snapshot = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (ProtoPartSnapshot part in context.Vessel.protoVessel.protoPartSnapshots)
            {
                if (part == null || part.resources == null) throw new InvalidOperationException("Missing proto part/resource list in remote effect boundary");
                foreach (ProtoPartResourceSnapshot resource in part.resources)
                {
                    if (resource == null) throw new InvalidOperationException("Missing proto resource in remote effect boundary");
                    if (resource.resourceName != "LiquidFuel" && resource.resourceName != "MonoPropellant" && resource.resourceName != "Oxidizer") continue;
                    string key = Key(part.persistentId, resource.resourceName);
                    if (changed.Contains(key)) continue;
                    if (snapshot.Count >= 512 || !Stock(resource.amount, resource.maxAmount) || snapshot.ContainsKey(key))
                        throw new InvalidOperationException("Unselected cargo boundary row invalid, duplicate, or exceeds 512 rows");
                    snapshot.Add(key, resource.amount.ToString("R", CultureInfo.InvariantCulture) + "|" +
                        resource.maxAmount.ToString("R", CultureInfo.InvariantCulture) + "|" + resource.flowState + "|" +
                        Single(Node(resource), "amount") + "|" + Single(Node(resource), "maxAmount"));
                    if (selected.Contains(part.persistentId)) selectedOtherRows++; else visitorRows++;
                }
            }
            return snapshot;
        }
        private static void Restore(InventoryMutationPlan plan, Context context, InventoryMutationRow row)
        {
            try { Write(row, row.BeforeAmount); }
            finally
            {
                if (!RowCurrent(plan, context)) throw new InvalidOperationException("Remote provider changed during rollback; remaining ConfigNode writes are held.");
                ConfigNode node = Node(row.ProtoResource);
                if (node == null || !node.SetValue("amount", row.BeforeConfigAmount, false))
                    throw new InvalidOperationException("Remote snapshot ConfigNode rollback failed");
                if (!RowCurrent(plan, context)) throw new InvalidOperationException("Remote provider changed during ConfigNode rollback.");
                if (!node.SetValue("maxAmount", row.BeforeConfigCapacity, false)) throw new InvalidOperationException("Remote snapshot ConfigNode rollback failed");
            }
        }
        private static bool CurrentRow(Context context, InventoryMutationRow row, double expected, bool checkOriginalConfig)
        {
            Entry entry;
            if (!context.Entries.TryGetValue(Key(row.MemberPersistentId, row.ResourceName), out entry) ||
                !Object.ReferenceEquals(entry.Snapshot, row.ProtoResource) || !Object.ReferenceEquals(entry.Inventory, row.ProviderInventory) ||
                row.ProtoResource == null || row.ProtoResource.flowState != row.BeforeFlowState || row.ProtoResource.maxAmount != row.Capacity ||
                row.ProtoResource.amount != expected || Dbl(row.ProviderAmountField.GetValue(row.ProviderInventory)) != expected ||
                Dbl(row.ProviderOriginalAmountField.GetValue(row.ProviderInventory)) != expected) return false;
            try
            {
                if (checkOriginalConfig)
                    return Single(Node(row.ProtoResource), "amount") == row.BeforeConfigAmount &&
                        Single(Node(row.ProtoResource), "maxAmount") == row.BeforeConfigCapacity;
                double amount, capacity;
                return Double.TryParse(Single(Node(row.ProtoResource), "amount"), NumberStyles.Float, CultureInfo.InvariantCulture, out amount) &&
                    Double.TryParse(Single(Node(row.ProtoResource), "maxAmount"), NumberStyles.Float, CultureInfo.InvariantCulture, out capacity) &&
                    amount == expected && capacity == row.Capacity;
            }
            catch { return false; }
        }
        private static bool Coherent(Entry entry)
        {
            ProtoPartResourceSnapshot s = entry.Snapshot;
            if (s == null || !Stock(s.amount, s.maxAmount) || Dbl(entry.Amount.GetValue(entry.Inventory)) != s.amount ||
                Dbl(entry.Original.GetValue(entry.Inventory)) != s.amount || Dbl(Read(entry.Inventory, "MaxAmount")) != s.maxAmount) return false;
            try
            {
                double amount, capacity;
                return Double.TryParse(Single(Node(s), "amount"), NumberStyles.Float, CultureInfo.InvariantCulture, out amount) &&
                    Double.TryParse(Single(Node(s), "maxAmount"), NumberStyles.Float, CultureInfo.InvariantCulture, out capacity) &&
                    Stock(amount, capacity) && capacity == s.maxAmount;
            }
            catch { return false; }
        }
        // The optional selected-save fence adds exact retained BRP ownership
        // checks at each row boundary. No catch-up or global vessel scan occurs
        // here, and the legacy null-callback path keeps its prior semantics.
        private static bool RowCurrent(InventoryMutationPlan plan, Context context)
        {
            if (!plan.AuthorityCurrent()) return false;
            return plan.AuthorityIsCurrent == null || SelectedCurrent(plan.Resolution, context);
        }
        internal bool SelectedContextCurrent(InventoryResolution resolution) => SelectedCurrent(resolution, resolution == null ? null : resolution.ProviderContext as Context);
        private static bool SelectedCurrent(InventoryResolution resolution, Context context)
        {
            try
            {
                if (context == null || !SupportedProviderAvailable || !Object.ReferenceEquals(ContextFor(resolution), context) ||
                    !Object.ReferenceEquals(context.Vessel.protoVessel, context.Proto)) return false;
                object[] processors = Sequence(Read(context.Vessel, "vesselModules")).Where(x => x != null && x.GetType().FullName == ProviderType).Take(2).ToArray();
                if (processors.Length != 1 || !Object.ReferenceEquals(processors[0], context.Processor)) return false;
                var current = new Dictionary<string, List<object>>(StringComparer.Ordinal);
                int count = 0;
                foreach (object inventory in Sequence(Read(context.Processor, "Inventories")))
                {
                    if (++count > MaxResources || inventory == null) return false;
                    if (Read(inventory, "ModuleId") != null) continue;
                    uint flight = UInt(Read(inventory, "FlightId"));
                    string resource = Read(inventory, "ResourceName") as string;
                    if (flight == 0 || String.IsNullOrWhiteSpace(resource)) return false;
                    string key = Key(flight, resource);
                    List<object> matches;
                    if (!current.TryGetValue(key, out matches)) current.Add(key, matches = new List<object>(1));
                    matches.Add(inventory);
                }
                foreach (Entry entry in context.Entries.Values)
                {
                    List<object> matches;
                    if (entry.Part.flightID != entry.FlightId || context.Proto.protoPartSnapshots.Count(p => p != null && p.flightID == entry.FlightId) != 1 || entry.Snapshot.resourceName != entry.ResourceName ||
                        !current.TryGetValue(Key(entry.Part.flightID, entry.ResourceName), out matches) || matches.Count != 1 ||
                        !Object.ReferenceEquals(matches[0], entry.Inventory) ||
                        !Object.ReferenceEquals(Read(entry.Inventory, "Snapshot"), entry.Snapshot) ||
                        Dbl(Read(entry.Inventory, "MaxAmount")) != entry.Snapshot.maxAmount) return false;
                }
                return true;
            }
            catch { return false; }
        }

        private static Context ContextFor(InventoryResolution resolution)
        {
            Context context = resolution == null ? null : resolution.ProviderContext as Context;
            if (context == null || !Object.ReferenceEquals(context.Endpoint, resolution.Endpoint) || context.Token != resolution.ContextToken ||
                context.Vessel == null || context.Vessel.loaded || context.Vessel.protoVessel == null ||
                FlightGlobals.Vessels == null || !FlightGlobals.Vessels.Contains(context.Vessel)) return null;
            ProtoVessel proto = context.Vessel.protoVessel;
            if (proto.protoPartSnapshots == null || proto.protoPartSnapshots.Count > MaxParts) return null;
            foreach (uint id in context.Endpoint.MemberPersistentIds)
                if (proto.protoPartSnapshots.Count(p => p != null && p.persistentId == id) != 1) return null;
            foreach (Entry entry in context.Entries.Values)
            {
                if (!proto.protoPartSnapshots.Contains(entry.Part) || entry.Part.resources == null || !entry.Part.resources.Contains(entry.Snapshot) ||
                    entry.Part.persistentId != entry.PersistentId || entry.Part.flightID == 0) return null;
            }
            return context;
        }
        private static bool ValidEndpoint(InventoryEndpoint e) => e != null && !String.IsNullOrWhiteSpace(e.DepotId) && e.MembershipRevision > 0 &&
            e.AnchorPersistentId != 0 && e.MemberPersistentIds != null && e.MemberPersistentIds.Length > 0 && e.MemberPersistentIds.Length <= MaxMembers &&
            e.MemberPersistentIds.All(x => x != 0) && e.MemberPersistentIds.Distinct().Count() == e.MemberPersistentIds.Length && e.MemberPersistentIds.Contains(e.AnchorPersistentId) &&
            e.MemberSetHash == OperationIdentity.ComputeMemberSetHash(e.AnchorPersistentId, e.MemberPersistentIds);
        private static InventoryEndpoint Copy(InventoryEndpoint e) => new InventoryEndpoint { DepotId = e.DepotId, MembershipRevision = e.MembershipRevision,
            MemberPersistentIds = (uint[])e.MemberPersistentIds.Clone(), AnchorPersistentId = e.AnchorPersistentId,
            MembershipHash = e.MembershipHash, MemberSetHash = e.MemberSetHash, Scene = e.Scene };
        private static bool Stock(double a, double c) => !Double.IsNaN(a) && !Double.IsInfinity(a) && !Double.IsNaN(c) && !Double.IsInfinity(c) && a >= 0 && c >= 0 && a <= c;
        private static double Dbl(object value) => Convert.ToDouble(value, CultureInfo.InvariantCulture);
        private static uint UInt(object value) => Convert.ToUInt32(value, CultureInfo.InvariantCulture);
        private static string Key(uint id, string name) => id.ToString(CultureInfo.InvariantCulture) + "\0" + name;
        private static InventoryPreflightResult Failure(string reason, InventoryObservation observation = null) => new InventoryPreflightResult { Reason = reason, Observation = observation };
        private static string Single(ConfigNode node, string name)
        {
            string[] values = node == null ? null : node.GetValues(name);
            if (values == null || values.Length != 1 || values[0] == null) throw new InvalidOperationException("Ambiguous remote resource ConfigNode");
            return values[0];
        }
        private static ConfigNode Node(ProtoPartResourceSnapshot snapshot) => Read(snapshot, "resourceValues") as ConfigNode;
        private static FieldInfo Field(Type type, string name) => type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        private static object Read(object value, string name)
        {
            FieldInfo field = Field(value.GetType(), name);
            if (field != null) return field.GetValue(value);
            PropertyInfo property = value.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (property != null) return property.GetValue(value, null);
            throw new MissingMemberException(value.GetType().FullName, name);
        }
        private static IEnumerable<object> Sequence(object value)
        {
            IEnumerable items = value as IEnumerable;
            if (items == null) throw new InvalidOperationException("BRP collection is missing");
            return items.Cast<object>();
        }
        private static void Call(object value, string name)
        {
            MethodInfo method = value.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, Type.EmptyTypes, null);
            if (method == null) throw new MissingMethodException(value.GetType().FullName, name);
            method.Invoke(value, null);
        }
    }
}
