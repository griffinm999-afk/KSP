using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Expanse.WorldBridge
{
    /// <summary>
    /// Candidate gateway for explicitly registered parts in the loaded, unpacked active
    /// Flight vessel. It deliberately advertises persistence synchronization as unavailable;
    /// until the 3b save-cut/provider gate passes, preflight refuses every mutation.
    /// </summary>
    internal sealed class VanillaLoadedInventoryGateway : IInventoryGateway
    {
        private const int MaxMembers = 64;
        private const int MaxDeltas = 16;
        private const double MicroUnitsPerUnit = 1000000.0;
        private const double MachineEpsilon = 2.2204460492503131e-16;
        private const string GateHold = "Physical writes are disabled pending provider synchronization and save-capture proof";

        private sealed class Context
        {
            public Vessel Vessel;
            public Part[] Parts;
            public Dictionary<string, PartResource> Resources;
            public Dictionary<string, uint> ResourceOwners;
            public string Token;
            public int MembershipRevision;
        }

        public InventoryCapabilityEvidence Describe(InventoryEndpoint endpoint)
        {
            string reason = ValidateEndpoint(endpoint);
            bool scene = HighLogic.LoadedSceneIsFlight && endpoint != null && endpoint.Scene == "Flight";
            Vessel vessel = FlightGlobals.ActiveVessel;
            bool available = reason == null && scene && vessel != null && vessel.loaded && !vessel.packed && vessel.vesselType != VesselType.EVA;
            if (!available && reason == null)
                reason = !scene ? "Only loaded Flight endpoints are supported" : "The registered endpoint is not the loaded active vessel";

            return new InventoryCapabilityEvidence
            {
                ProviderId = "KSP.PartResource.loaded-vessel",
                ProviderVersion = "KSP-runtime",
                Scene = endpoint == null ? "unknown" : endpoint.Scene,
                ObservationAvailable = available,
                ReadSupported = available,
                // Amount is settable on loaded PartResource, but a general lock/provider
                // contract and persistence sync have not yet passed the runtime gate.
                WriteSupported = false,
                SynchronousRollbackSupported = false,
                PersistenceSyncSupported = false,
                HoldReason = available ? GateHold : Bound(reason, 256)
            };
        }

        public InventoryResolution Resolve(InventoryEndpoint endpoint)
        {
            InventoryCapabilityEvidence capability = Describe(endpoint);
            if (!capability.ObservationAvailable) return null;
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (!StillSupported(vessel)) return null;

            Dictionary<uint, List<Part>> byId = new Dictionary<uint, List<Part>>();
            foreach (Part part in vessel.parts)
            {
                List<Part> list;
                if (!byId.TryGetValue(part.persistentId, out list)) byId[part.persistentId] = list = new List<Part>(1);
                list.Add(part);
            }

            Part[] selected = new Part[endpoint.MemberPersistentIds.Length];
            Dictionary<string, PartResource> resources = new Dictionary<string, PartResource>(StringComparer.Ordinal);
            Dictionary<string, uint> resourceOwners = new Dictionary<string, uint>(StringComparer.Ordinal);
            for (int i = 0; i < endpoint.MemberPersistentIds.Length; i++)
            {
                List<Part> matches;
                uint id = endpoint.MemberPersistentIds[i];
                if (!byId.TryGetValue(id, out matches) || matches.Count != 1) return null;
                Part part = matches[0];
                if (part == null || part.persistentId != id || part.Resources == null || part.Resources.Count == 0) return null;
                selected[i] = part;
                foreach (PartResource resource in part.Resources)
                {
                    if (resource == null || String.IsNullOrEmpty(resource.resourceName)) return null;
                    string key = ResourceKey(id, resource.resourceName);
                    if (resources.ContainsKey(key)) return null;
                    resources.Add(key, resource);
                    resourceOwners.Add(key, id);
                }
            }

            string token = Guid.NewGuid().ToString("N");
            Context context = new Context { Vessel = vessel, Parts = selected, Resources = resources, ResourceOwners = resourceOwners, Token = token, MembershipRevision = endpoint.MembershipRevision };
            return new InventoryResolution(CloneEndpoint(endpoint), context, token);
        }

        public InventoryObservation Observe(InventoryResolution resolution)
        {
            Context context = GetContext(resolution);
            if (context == null) return null;
            List<InventoryStockRow> rows = new List<InventoryStockRow>(context.Resources.Count);
            foreach (KeyValuePair<string, PartResource> entry in context.Resources)
            {
                PartResource resource = entry.Value;
                if (!ValidStock(resource)) return null;
                rows.Add(new InventoryStockRow { MemberPersistentId = context.ResourceOwners[entry.Key], ResourceName = resource.resourceName, Amount = resource.amount, Capacity = resource.maxAmount, DebitAllowed = resource.flowState });
            }
            rows.Sort((a, b) => { int c = a.MemberPersistentId.CompareTo(b.MemberPersistentId); return c != 0 ? c : String.CompareOrdinal(a.ResourceName, b.ResourceName); });
            return new InventoryObservation
            {
                ContextToken = context.Token, ObservationVersion = ComputeObservationVersion(rows), ProviderId = "KSP.PartResource.loaded-vessel", ProviderVersion = "KSP-runtime",
                MembershipRevision = context.MembershipRevision, Rows = rows.ToArray()
            };
        }

        public InventoryPreflightResult Preflight(InventoryResolution resolution, IList<InventoryDelta> deltas)
        {
            InventoryCapabilityEvidence capability = Describe(resolution == null ? null : resolution.Endpoint);
            InventoryObservation observation = Observe(resolution);
            if (observation == null) return Failed("Selected loaded inventory is no longer coherent");
            if (!capability.CanApply) return new InventoryPreflightResult { Accepted = false, Reason = capability.HoldReason, Observation = observation };
            if (deltas == null || deltas.Count < 1 || deltas.Count > MaxDeltas) return Failed("Physical intent exceeds the bounded resource-delta count", observation);

            Context context = GetContext(resolution);
            if (context == null) return Failed("Selected member resolution is stale", observation);
            Dictionary<string, long> conservation = new Dictionary<string, long>(StringComparer.Ordinal);
            List<InventoryMutationRow> rows = new List<InventoryMutationRow>(deltas.Count);
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (InventoryDelta delta in deltas)
            {
                if (delta == null || delta.MemberPersistentId == 0 || String.IsNullOrEmpty(delta.ResourceName) || delta.DeltaMicroUnits == 0)
                    return Failed("Physical intent contains an invalid member, resource or zero delta", observation);
                string key = ResourceKey(delta.MemberPersistentId, delta.ResourceName);
                if (!seen.Add(key)) return Failed("Physical intent repeats a member/resource pair", observation);
                PartResource resource;
                if (!context.Resources.TryGetValue(key, out resource) || !ValidStock(resource)) return Failed("A selected member resource is missing or invalid", observation);
                // A disabled flow resource is treated as a lock. Other provider locks are
                // intentionally unsupported until their KSP semantics are proven.
                if (!resource.flowState) return Failed("A selected resource is flow-locked", observation);
                double requested = delta.DeltaMicroUnits / MicroUnitsPerUnit;
                double after = resource.amount + requested;
                if (!Finite(after) || after < 0 || after > resource.maxAmount) return Failed("Current stock or capacity cannot satisfy the exact intended change", observation);
                double actual = after - resource.amount;
                double tolerance = RepresentabilityTolerance(resource.amount, after, requested);
                if (Math.Abs(actual - requested) > tolerance) return Failed("The requested micro-unit change is not representable at this stock magnitude", observation);
                rows.Add(new InventoryMutationRow { Resource = resource, MemberPersistentId = delta.MemberPersistentId, ResourceName = delta.ResourceName, BeforeAmount = resource.amount, IntendedAfterAmount = after, Capacity = resource.maxAmount, DeltaMicroUnits = delta.DeltaMicroUnits });
                long sum;
                conservation.TryGetValue(delta.ResourceName, out sum);
                try { conservation[delta.ResourceName] = checked(sum + delta.DeltaMicroUnits); }
                catch (OverflowException) { return Failed("Resource conservation total overflowed", observation); }
            }
            if (conservation.Values.Any(x => x != 0)) return Failed("Transfer intent does not conserve each resource exactly", observation);
            return new InventoryPreflightResult { Accepted = true, Observation = observation, Plan = new InventoryMutationPlan(resolution, context.Token, rows) };
        }

        public InventoryApplyResult Apply(InventoryMutationPlan plan)
        {
            if (plan == null || !Describe(plan.Resolution.Endpoint).CanApply || GetContext(plan.Resolution) == null)
                return new InventoryApplyResult { Succeeded = false, Reason = GateHold, Plan = plan, Rows = new InventoryAppliedRow[0] };
            if (plan.AttemptedCount != 0) return new InventoryApplyResult { Succeeded = false, Reason = "Mutation plan has already been attempted", Plan = plan, Rows = new InventoryAppliedRow[0] };
            Context context = GetContext(plan.Resolution);
            if (context == null) return new InventoryApplyResult { Succeeded = false, Reason = "Selected member resolution is stale", Plan = plan, Rows = new InventoryAppliedRow[0] };
            List<InventoryAppliedRow> applied = new List<InventoryAppliedRow>(plan.Rows.Count);
            try
            {
                foreach (InventoryMutationRow row in plan.Rows)
                {
                    PartResource resolved;
                    if (row.Resource == null || !ValidStock(row.Resource) || !context.Resources.TryGetValue(ResourceKey(row.MemberPersistentId, row.ResourceName), out resolved) || !Object.ReferenceEquals(resolved, row.Resource) || row.Resource.amount != row.BeforeAmount || row.Resource.maxAmount != row.Capacity)
                        throw new InvalidOperationException("Inventory changed after preflight");
                    plan.AttemptedCount++;
                    row.Resource.amount = row.IntendedAfterAmount;
                    double after = row.Resource.amount;
                    double intendedDelta = row.DeltaMicroUnits / MicroUnitsPerUnit;
                    if (!Finite(after) || after < 0 || after > row.Resource.maxAmount || after == row.BeforeAmount || Math.Abs(after - row.IntendedAfterAmount) > RepresentabilityTolerance(row.BeforeAmount, row.IntendedAfterAmount, intendedDelta) || Math.Abs((after - row.BeforeAmount) - intendedDelta) > RepresentabilityTolerance(row.BeforeAmount, after, intendedDelta))
                        throw new InvalidOperationException("Provider amount readback differs from the exact intended change");
                    applied.Add(new InventoryAppliedRow { MemberPersistentId = row.MemberPersistentId, ResourceName = row.ResourceName, BeforeAmount = row.BeforeAmount, IntendedAfterAmount = row.IntendedAfterAmount, ObservedAfterAmount = after, ActualDelta = after - row.BeforeAmount });
                }
                foreach (IGrouping<string, InventoryAppliedRow> group in applied.GroupBy(x => x.ResourceName, StringComparer.Ordinal))
                {
                    double net = group.Sum(x => x.ActualDelta);
                    double tolerance = 0;
                    foreach (InventoryMutationRow row in plan.Rows.Where(x => x.ResourceName == group.Key))
                        tolerance += RepresentabilityTolerance(row.BeforeAmount, row.IntendedAfterAmount, row.DeltaMicroUnits / MicroUnitsPerUnit);
                    if (!Finite(net) || Math.Abs(net) > tolerance)
                        throw new InvalidOperationException("Observed per-resource transfer does not conserve stock within representability tolerance");
                }
                return new InventoryApplyResult { Succeeded = true, Plan = plan, Rows = applied.ToArray() };
            }
            catch (Exception ex)
            {
                return new InventoryApplyResult { Succeeded = false, Reason = Bound(ex.Message, 256), Plan = plan, Rows = applied.ToArray() };
            }
        }

        public InventoryRollbackResult Rollback(InventoryMutationPlan plan)
        {
            if (plan == null) return new InventoryRollbackResult { Confirmed = false, Reason = "Mutation plan is missing", Rows = new InventoryAppliedRow[0] };
            List<InventoryAppliedRow> restored = new List<InventoryAppliedRow>();
            bool allConfirmed = true;
            string reason = null;
            // Restore only attempted resources, including one whose setter threw after partially applying.
            for (int i = Math.Min(plan.AttemptedCount, plan.Rows.Count) - 1; i >= 0; i--)
            {
                InventoryMutationRow row = plan.Rows[i];
                try
                {
                    if (row.Resource == null) throw new InvalidOperationException("Resource reference was lost");
                    row.Resource.amount = row.BeforeAmount;
                    double observed = row.Resource.amount;
                    if (!Finite(observed) || observed != row.BeforeAmount) throw new InvalidOperationException("Restored amount did not read back exactly");
                    restored.Add(new InventoryAppliedRow { MemberPersistentId = row.MemberPersistentId, ResourceName = row.ResourceName, BeforeAmount = row.IntendedAfterAmount, IntendedAfterAmount = row.BeforeAmount, ObservedAfterAmount = observed, ActualDelta = observed - row.IntendedAfterAmount });
                }
                catch (Exception ex) { allConfirmed = false; if (reason == null) reason = Bound(ex.Message, 192); }
            }
            return new InventoryRollbackResult { Confirmed = allConfirmed, Reason = allConfirmed ? null : reason ?? "Rollback could not be confirmed", Rows = restored.ToArray() };
        }

        public InventorySyncResult Synchronize(InventoryMutationPlan plan)
        {
            return new InventorySyncResult { Succeeded = false, Reason = GateHold };
        }

        private static InventoryPreflightResult Failed(string reason, InventoryObservation observation = null)
        { return new InventoryPreflightResult { Accepted = false, Reason = reason, Observation = observation }; }

        private static Context GetContext(InventoryResolution resolution)
        {
            Context context = resolution == null ? null : resolution.ProviderContext as Context;
            if (context == null || !String.Equals(context.Token, resolution.ContextToken, StringComparison.Ordinal) || context.MembershipRevision != resolution.Endpoint.MembershipRevision || !StillSupported(context.Vessel)) return null;
            if (context.Parts.Length != resolution.Endpoint.MemberPersistentIds.Length) return null;
            HashSet<Part> currentParts = new HashSet<Part>();
            Dictionary<uint, int> currentCounts = new Dictionary<uint, int>();
            foreach (Part part in context.Vessel.parts)
            {
                if (part == null || !currentParts.Add(part)) continue;
                int count;
                currentCounts.TryGetValue(part.persistentId, out count);
                currentCounts[part.persistentId] = count + 1;
            }
            for (int i = 0; i < context.Parts.Length; i++)
            {
                Part p = context.Parts[i];
                int count;
                if (p == null || p.persistentId != resolution.Endpoint.MemberPersistentIds[i] || !currentParts.Contains(p) || !currentCounts.TryGetValue(p.persistentId, out count) || count != 1) return null;
            }
            HashSet<string> currentResourceKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (Part part in context.Parts)
            {
                if (part.Resources == null) return null;
                foreach (PartResource resource in part.Resources)
                {
                    if (resource == null || String.IsNullOrEmpty(resource.resourceName)) return null;
                    string key = ResourceKey(part.persistentId, resource.resourceName);
                    PartResource cached;
                    if (!currentResourceKeys.Add(key) || !context.Resources.TryGetValue(key, out cached) || !Object.ReferenceEquals(cached, resource)) return null;
                }
            }
            if (currentResourceKeys.Count != context.Resources.Count) return null;
            return context;
        }

        private static bool StillSupported(Vessel vessel)
        { return HighLogic.LoadedSceneIsFlight && vessel != null && vessel == FlightGlobals.ActiveVessel && vessel.loaded && !vessel.packed && vessel.vesselType != VesselType.EVA && vessel.parts != null; }

        private static string ValidateEndpoint(InventoryEndpoint endpoint)
        {
            if (endpoint == null || String.IsNullOrWhiteSpace(endpoint.DepotId) || endpoint.MembershipRevision <= 0 || endpoint.MemberPersistentIds == null || endpoint.MemberPersistentIds.Length == 0 || endpoint.MemberPersistentIds.Length > MaxMembers)
                return "Depot identity or selected membership is invalid or outside bounds";
            if (endpoint.MemberPersistentIds.Any(x => x == 0) || endpoint.MemberPersistentIds.Distinct().Count() != endpoint.MemberPersistentIds.Length)
                return "Depot membership contains a zero or duplicate part identity";
            return null;
        }

        private static InventoryEndpoint CloneEndpoint(InventoryEndpoint e)
        { return new InventoryEndpoint { DepotId = e.DepotId, MembershipRevision = e.MembershipRevision, MemberPersistentIds = (uint[])e.MemberPersistentIds.Clone(), Scene = e.Scene }; }
        private static string ComputeObservationVersion(IList<InventoryStockRow> rows)
        {
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8))
            using (SHA256 sha = SHA256.Create())
            {
                foreach (InventoryStockRow row in rows)
                {
                    byte[] name = Encoding.UTF8.GetBytes(row.ResourceName);
                    writer.Write(row.MemberPersistentId);
                    writer.Write(name.Length);
                    writer.Write(name);
                    writer.Write(BitConverter.DoubleToInt64Bits(row.Amount));
                    writer.Write(BitConverter.DoubleToInt64Bits(row.Capacity));
                    writer.Write(row.DebitAllowed.HasValue && row.DebitAllowed.Value);
                }
                writer.Flush();
                return BitConverter.ToString(sha.ComputeHash(stream.ToArray())).Replace("-", String.Empty).ToLowerInvariant();
            }
        }
        private static bool ValidStock(PartResource r)
        { return r != null && !String.IsNullOrEmpty(r.resourceName) && Finite(r.amount) && Finite(r.maxAmount) && r.amount >= 0 && r.maxAmount >= 0 && r.amount <= r.maxAmount; }
        private static bool Finite(double d) { return !Double.IsNaN(d) && !Double.IsInfinity(d); }
        private static double RepresentabilityTolerance(double before, double after, double requested)
        {
            double magnitudeTolerance = Math.Max(1e-12, Math.Max(Math.Abs(before), Math.Abs(after)) * MachineEpsilon * 2.0);
            return Math.Min(Math.Abs(requested) * 1e-6, magnitudeTolerance);
        }
        private static string ResourceKey(uint id, string name) { return id.ToString(CultureInfo.InvariantCulture) + "\0" + name; }
        private static string Bound(string value, int max)
        { if (String.IsNullOrEmpty(value)) return "Inventory unavailable"; value = value.Replace('\r', ' ').Replace('\n', ' '); return value.Length <= max ? value : value.Substring(0, max); }
    }
}
