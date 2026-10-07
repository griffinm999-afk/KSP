using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Collections;
using System.Reflection;
using UnityEngine;
using Expanse.Domain;

namespace Expanse.WorldBridge
{
    internal sealed class DepotSnapshot
    {
        public string SessionId, LoadEpoch, WorldId, DepotId;
        public int MembershipRevision;
        public long Revision;
        public double StartedUt, CompletedUt, AgeSeconds;
        public List<DepotResourceRow> Resources;
    }
    internal sealed class DepotResourceRow
    {
        public string Name, DisplayName;
        public double Amount, MaxAmount;
    }
    internal sealed class DepotView
    {
        public int SchemaVersion = 1;
        public string RegistryState, Reason, WorldId, DepotId, Label, CurrentVesselName;
        public int? MembershipRevision, MemberCount;
        public uint? AnchorPartId;
        public string ObservationState, ObservationReason;
        public DepotSnapshot Snapshot;
    }
    internal sealed class DepotSummary
    {
        public string DepotId, Label, MembershipHash, Status, Reason;
        public long MembershipRevision;
        public double? StockAgeSeconds;
        public int MemberCount;
        public DepotResourceRow[] Resources = new DepotResourceRow[0];
    }

    internal sealed class DepotRegistration
    {
        public string DepotId, Label;
        public string OwnerKind="legacy",OwnerColonyId="",OwnerFacilityId="",ApprovedSpecHash="";
        public uint Anchor;
        public int MembershipRevision;
        public readonly List<uint> MemberIds = new List<uint>();
    }

    // All calls except CloneView are made on KSP's main thread. CloneView creates a worker-safe copy.
    [KSPScenario(ScenarioCreationOptions.AddToAllGames, GameScenes.FLIGHT, GameScenes.SPACECENTER, GameScenes.TRACKSTATION)]
    public sealed partial class DepotRegistryModule : ScenarioModule
    {
        private const int Schema = 3, MaxMembers = 4096, MaxNewDepotMembers = 64, MaxDepots = 8, MaxVesselParts = 16384;
        private static DepotRegistryModule instance;
        private string worldId, depotId, label;
        private ConfigNode rawUnknown;
        private readonly List<ConfigNode> rawUnknownRegistryRoots=new List<ConfigNode>();
        private uint anchor;
        private int membershipRevision;
        private readonly List<uint> memberIds = new List<uint>();
        private readonly List<DepotRegistration> registrations = new List<DepotRegistration>();
        private bool ready, corrupt;
        private DepotSnapshot lastSnapshot;
        private string observationState = "none", observationReason;
        private long revision;
        private double nextObservationAt;
        private double observationStartedAt;
        private double lastObservationAt;
        private List<Part> resolvedParts;
        private Vessel resolvedVessel;
        private Vessel resolvingVessel;
        private List<Part> resolvingSource;
        private int resolvingIndex;
        private Dictionary<uint, List<Part>> resolvingMap;
        private string currentVesselName;
        private string collectionReason;
        private int collectIndex;
        private double collectStartUt;
        private readonly Dictionary<string, DepotResourceRow> collecting = new Dictionary<string, DepotResourceRow>(StringComparer.Ordinal);
        private DepotRegistrySnapshot cachedRegistrySnapshot;
        private Dictionary<uint, List<Part>> lastVesselMap;
        private Vessel lastVesselMapOwner;
        private StockObservation[] cachedInventoryObservations = new StockObservation[0];
        private double inventoryObservationCapturedAt;
        private string inventoryObservationContextKey;
        private readonly Dictionary<string, long> inventoryObservationRevisions = new Dictionary<string, long>(StringComparer.Ordinal);
        private DepotSummary[] cachedDisplaySummaries = new DepotSummary[0];
        private double displayCapturedAt = -1;
        private string displayContextKey;

        public static DepotRegistryModule Instance { get { return instance; } }
        public bool IsReady { get { return ready; } }
        public bool IsCorrupt { get { return corrupt; } }
        public IList<uint> MemberIds { get { return memberIds.AsReadOnly(); } }
        public string Label { get { return label; } }
        public uint AnchorPartId { get { return anchor; } }
        public string WorldId { get { return worldId; } }
        public string DepotId { get { return depotId; } }
        public int RegisteredDepotCount { get { return LegacyRegistrations.Count(); } }
        public string[] RegisteredDepotIds { get { return LegacyRegistrations.Select(x => x.DepotId).ToArray(); } }
        internal IList<DepotRegistration> Registrations { get { return registrations.AsReadOnly(); } }

        // Immutable worker-safe metadata only. Rebuilt on registry/load mutations, never from stock observations.
        internal DepotRegistrySnapshot CreateEffectRegistrySnapshot()
        {
            if (!ready || corrupt || string.IsNullOrWhiteSpace(worldId)) return null;
            if (cachedRegistrySnapshot == null)
            {
                List<DepotRecord> active = new List<DepotRecord>();
                foreach (DepotRegistration registration in LegacyRegistrations.OrderBy(x => x.DepotId, StringComparer.Ordinal))
                    active.Add(new DepotRecord { DepotId = registration.DepotId, MembershipRevision = registration.MembershipRevision, MembershipHash = ComputeMembershipHash(worldId, 2, registration.DepotId, registration.MembershipRevision, registration.Anchor, registration.MemberIds.OrderBy(x => x).ToArray()), Active = true });
                cachedRegistrySnapshot = new DepotRegistrySnapshot
                {
                    RegistryVersion = "depot-registry-v2",
                    Depots = active.ToArray()
                };
                cachedRegistrySnapshot.RegistryHash = OperationIdentity.ComputeDepotRegistryHash(cachedRegistrySnapshot.Depots);
            }
            return new DepotRegistrySnapshot
            {
                RegistryVersion = cachedRegistrySnapshot.RegistryVersion,
                RegistryHash = cachedRegistrySnapshot.RegistryHash,
                Depots = cachedRegistrySnapshot.Depots.Select(x => new DepotRecord { DepotId = x.DepotId, MembershipRevision = x.MembershipRevision, MembershipHash = x.MembershipHash, Active = x.Active }).ToArray()
            };
        }

        private static string ComputeMembershipHash(string world, int schema, string id, int revision, uint anchorId, uint[] members)
        {
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream, new UTF8Encoding(false, true), true))
            {
                WriteHashText(writer, "depotMembership"); WriteHashText(writer, world); writer.Write(schema); WriteHashText(writer, id); writer.Write(revision); writer.Write(anchorId); writer.Write(members.Length);
                foreach (uint member in members) writer.Write(member);
                writer.Flush();
                using (SHA256 sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream.ToArray())).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static void WriteHashText(BinaryWriter writer, string value)
        {
            byte[] bytes = new UTF8Encoding(false, true).GetBytes(value ?? string.Empty);
            writer.Write(bytes.Length); writer.Write(bytes);
        }

        public override void OnAwake()
        {
            base.OnAwake(); instance = this;
            GameEvents.onVesselWasModified.Add(OnTopologyChanged);
            GameEvents.onVesselChange.Add(OnVesselChange);
            ready = false;
        }
        public override void OnLoad(ConfigNode node)
        {
            base.OnLoad(node); ClearRegistration(); ready = false; registryMutationRevision++;
            ConfigNode[] registryRoots=node.GetNodes("DEPOT_REGISTRY");
            if(registryRoots.Length>1){rawUnknownRegistryRoots.AddRange(registryRoots.Select(r=>r.CreateCopy()));corrupt=true;ClearObservation("Multiple saved depot registry authority nodes; original nodes preserved.");return;}
            ConfigNode saved = registryRoots.SingleOrDefault();
            if (saved == null) { worldId = Guid.NewGuid().ToString("D"); ready = true; ClearObservation("New save without depot registration"); return; }
            try
            {
                int schema = int.Parse(saved.GetValue("schemaVersion"), CultureInfo.InvariantCulture);
                if (schema != 1 && schema != 2 && schema != Schema) throw new FormatException("Unsupported registry schema " + schema);
                if(schema==Schema)RequireSingleAuthority(saved,"schemaVersion","worldId");
                Guid parsedWorld = Guid.Parse(saved.GetValue("worldId"));
                if (parsedWorld == Guid.Empty) throw new FormatException("Empty world identifier");
                worldId = parsedWorld.ToString("D");
                if (schema == 1 && saved.HasValue("depotId"))
                {
                    Guid parsedDepot = Guid.Parse(saved.GetValue("depotId"));
                    if (parsedDepot == Guid.Empty) throw new FormatException("Empty depot identifier");
                    depotId = parsedDepot.ToString("D");
                    label = Bound(saved.GetValue("label"), 256);
                    anchor = uint.Parse(saved.GetValue("anchorPartId"), CultureInfo.InvariantCulture);
                    membershipRevision = int.Parse(saved.GetValue("membershipRevision"), CultureInfo.InvariantCulture);
                    ConfigNode[] members = saved.GetNodes("member");
                    if (members.Length < 1 || members.Length > MaxMembers || anchor == 0 || membershipRevision < 1) throw new FormatException("Invalid membership bounds");
                    foreach (ConfigNode m in members) memberIds.Add(uint.Parse(m.GetValue("id"), CultureInfo.InvariantCulture));
                    if (memberIds.Any(x => x == 0) || memberIds.Distinct().Count() != memberIds.Count || !memberIds.Contains(anchor)) throw new FormatException("Duplicate or invalid member identity");
                    DepotRegistration migrated = new DepotRegistration { DepotId = depotId, Label = label, Anchor = anchor, MembershipRevision = membershipRevision }; migrated.MemberIds.AddRange(memberIds); registrations.Add(migrated);
                }
                else if (schema == 2 || schema == Schema)
                {
                    ConfigNode[] depots = saved.GetNodes("DEPOT");
                    if (depots.Length > (schema==2 ? MaxDepots : MaxDepots+MaxColonyDepots)) throw new FormatException("Depot count exceeds supported bound");
                    HashSet<string> depotIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    HashSet<uint> allMembers = new HashSet<uint>();
                    int totalMembers = 0;
                    foreach (ConfigNode d in depots)
                    {
                        if(schema==Schema)RequireSingleAuthority(d,"depotId","anchorPartId","membershipRevision");
                        Guid parsedDepot = Guid.Parse(d.GetValue("depotId"));
                        if (parsedDepot == Guid.Empty) throw new FormatException("Empty depot identifier");
                        DepotRegistration registration = new DepotRegistration { DepotId = parsedDepot.ToString("D"), Label = Bound(d.GetValue("label"), 256), Anchor = uint.Parse(d.GetValue("anchorPartId"), CultureInfo.InvariantCulture), MembershipRevision = int.Parse(d.GetValue("membershipRevision"), CultureInfo.InvariantCulture) };
                        ReadRegistrationOwner(d,registration,schema);
                        ConfigNode[] members = d.GetNodes("member");
                        if (!depotIds.Add(registration.DepotId) || registration.MembershipRevision < 1 || registration.Anchor == 0 || members.Length < 1 || members.Length > (registration.OwnerKind=="colony" ? MaxNewDepotMembers : MaxMembers)) throw new FormatException("Invalid depot identity or membership bounds");
                        foreach (ConfigNode m in members)
                        {
                            if(schema==Schema)RequireSingleAuthority(m,"id");
                            uint id = uint.Parse(m.GetValue("id"), CultureInfo.InvariantCulture);
                            if (id == 0 || !allMembers.Add(id)) throw new FormatException("Duplicate or invalid part membership across depots");
                            registration.MemberIds.Add(id);
                        }
                        totalMembers += members.Length;
                        if (totalMembers > MaxMembers || !registration.MemberIds.Contains(registration.Anchor)) throw new FormatException("Membership total exceeds bound or anchor is not a member");
                        registrations.Add(registration);
                    }
                    if(LegacyRegistrations.Count()>MaxDepots||registrations.Count(r=>r.OwnerKind=="colony")>MaxColonyDepots)throw new FormatException("Scoped registration capacity exceeded.");
                    var primary=LegacyRegistrations.FirstOrDefault();if(primary!=null)SetPrimary(primary);
                }
                ready = true;
            }
            catch (Exception ex) { rawUnknown = saved.CreateCopy(); corrupt = true; observationReason = Bound("Saved depot data is corrupt or unsupported: " + ex.Message, 256); }
            string preservedRegistryError = corrupt ? observationReason : null;
            ClearObservation("Save loaded");
            if (corrupt) observationReason = preservedRegistryError;
        }
        // Bridge samples advertise loading while a GameEvents load transition is unresolved.
        // KSP may deliver OnLoad before that event; keep registry readiness here and let OnLoad
        // atomically replace the fields when KSP supplies the selected save node.
        public void MarkLoading() { ClearObservation("Save load in progress"); }
        public override void OnSave(ConfigNode node)
        {
            base.OnSave(node);
            if(corrupt&&rawUnknownRegistryRoots.Count>0){foreach(var raw in rawUnknownRegistryRoots)node.AddNode(raw.CreateCopy());return;}
            if (corrupt && rawUnknown != null) { node.AddNode(rawUnknown.CreateCopy()); return; }
            ConfigNode saved = node.AddNode("DEPOT_REGISTRY"); saved.AddValue("schemaVersion", Schema);
            if (string.IsNullOrEmpty(worldId)) worldId = Guid.NewGuid().ToString("D");
            saved.AddValue("worldId", worldId);
            foreach (DepotRegistration registration in registrations)
            {
                ConfigNode depot = saved.AddNode("DEPOT"); depot.AddValue("depotId", registration.DepotId); depot.AddValue("label", registration.Label ?? "Depot"); depot.AddValue("anchorPartId", registration.Anchor);
                depot.AddValue("membershipRevision", registration.MembershipRevision);
                WriteRegistrationOwner(depot,registration);
                foreach (uint id in registration.MemberIds) { ConfigNode m = depot.AddNode("member"); m.AddValue("id", id); }
            }
        }
        public bool Register(string depotLabel, uint anchorId, IEnumerable<uint> ids)
        {
            if (!ready || corrupt || ids == null || LegacyRegistrations.Count() >= MaxDepots) return false;
            List<uint> chosen = ids.ToList();
            if (chosen.Count < 1 || chosen.Count > MaxNewDepotMembers || chosen.Contains(0) || chosen.Distinct().Count() != chosen.Count || !chosen.Contains(anchorId)) return false;
            if (registrations.Sum(r => r.MemberIds.Count) + chosen.Count > MaxMembers) return false;
            HashSet<uint> alreadyRegistered = new HashSet<uint>(registrations.SelectMany(r => r.MemberIds));
            if (chosen.Any(alreadyRegistered.Contains)) return false;
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (!FoundationRegistrationContext.CanEnumerate(vessel) || vessel.vesselType == VesselType.EVA || vessel.parts.Count > MaxVesselParts) return false;
            Dictionary<uint, List<Part>> available = new Dictionary<uint, List<Part>>();
            foreach (Part p in vessel.parts)
            {
                List<Part> matches;
                if (!available.TryGetValue(p.persistentId, out matches)) available[p.persistentId] = matches = new List<Part>();
                matches.Add(p);
            }
            foreach (uint id in chosen)
            {
                List<Part> matches;
                if (!available.TryGetValue(id, out matches)) return false;
                if (matches.Count != 1 || matches[0].Resources == null || matches[0].Resources.Count == 0) return false;
            }
            DepotRegistration registration = new DepotRegistration { DepotId = Guid.NewGuid().ToString("D"), Label = Bound(depotLabel, 256), Anchor = anchorId, MembershipRevision = 1 };
            registration.MemberIds.AddRange(chosen); registrations.Add(registration);
            if (depotId == null) SetPrimary(registration);
            registryMutationRevision++;cachedRegistrySnapshot = null; ClearObservation("Registration changed"); return true;
        }
        public bool Unregister()
        {
            if (!ready || corrupt || depotId == null) return false;
            return Unregister(depotId);
        }
        public bool Unregister(string id)
        {
            if (!ready || corrupt || string.IsNullOrEmpty(id)) return false;
            int index = registrations.FindIndex(r => string.Equals(r.DepotId, id, StringComparison.OrdinalIgnoreCase));
            if (index < 0) return false;
            if(registrations[index].OwnerKind=="colony")return false; // Paid ownership needs explicit colony review; PAW cannot silently erase its receipt.
            registrations.RemoveAt(index);
            if (string.Equals(depotId, id, StringComparison.OrdinalIgnoreCase))
            {
                depotId = label = null; anchor = 0; membershipRevision = 0; memberIds.Clear();
                var primary=LegacyRegistrations.FirstOrDefault();if(primary!=null)SetPrimary(primary);
            }
            registryMutationRevision++;cachedRegistrySnapshot = null; ClearObservation("Depot unregistered"); return true;
        }
        public void Tick(double realTime, string session, string epoch, bool activeWorld)
        {
            if (!ready || corrupt || depotId == null) return;
            if (!activeWorld)
            {
                lastSnapshot = null; revision = 0; resolvedParts = null; resolvedVessel = null; lastVesselMap = null; lastVesselMapOwner = null; cachedInventoryObservations = new StockObservation[0]; inventoryObservationCapturedAt = 0; ClearResolver(); collecting.Clear(); collectIndex = 0;
                observationState = "unavailable"; observationReason = "No active save world"; currentVesselName = null; return;
            }
            if (lastSnapshot != null && (!string.Equals(lastSnapshot.SessionId, session, StringComparison.Ordinal) || !string.Equals(lastSnapshot.LoadEpoch, epoch, StringComparison.Ordinal) || !string.Equals(lastSnapshot.WorldId, worldId, StringComparison.Ordinal) || !string.Equals(lastSnapshot.DepotId, depotId, StringComparison.Ordinal) || lastSnapshot.MembershipRevision != membershipRevision))
            {
                lastSnapshot = null; observationState = "unavailable"; observationReason = "Clock/save context changed; collecting a fresh observation"; revision = 0; collectIndex = 0; resolvedParts = null; resolvedVessel = null; lastVesselMap = null; lastVesselMapOwner = null; cachedInventoryObservations = new StockObservation[0]; inventoryObservationCapturedAt = 0; ClearResolver(); collecting.Clear(); nextObservationAt = 0;
            }
            if (collectIndex == 0 && resolvingSource == null && resolvedParts == null && realTime < nextObservationAt) return;
            if (collectIndex == 0 && resolvedParts == null)
            {
                if (resolvingSource == null)
                {
                    string reason = AvailabilityReason(activeWorld);
                    if (reason != null) { observationState = "unavailable"; observationReason = reason; nextObservationAt = realTime + 2.0; return; }
                    Vessel vessel = FlightGlobals.ActiveVessel;
                    if (vessel.parts == null || vessel.parts.Count > MaxVesselParts) { observationState = "unavailable"; observationReason = "Active vessel exceeds the supported part bound"; nextObservationAt = realTime + 2.0; return; }
                    resolvingVessel = vessel; resolvingSource = vessel.parts; resolvingIndex = 0; resolvingMap = new Dictionary<uint, List<Part>>();
                    currentVesselName = vessel.vesselName; observationState = "collecting"; observationReason = "Resolving registered part identities";
                }
                if (resolvingVessel != FlightGlobals.ActiveVessel || !HighLogic.LoadedSceneIsFlight || !resolvingVessel.loaded || resolvingVessel.packed || resolvingVessel.parts != resolvingSource) { ClearResolver(); observationState = "unavailable"; observationReason = "Depot context changed while resolving members"; nextObservationAt = realTime + 2.0; return; }
                int resolveStop = Math.Min(resolvingSource.Count, resolvingIndex + 256);
                for (; resolvingIndex < resolveStop; resolvingIndex++)
                {
                    Part p = resolvingSource[resolvingIndex];
                    List<Part> members;
                    if (!resolvingMap.TryGetValue(p.persistentId, out members)) resolvingMap[p.persistentId] = members = new List<Part>();
                    members.Add(p);
                }
                if (resolvingIndex < resolvingSource.Count) return;
                lastVesselMap = resolvingMap;
                lastVesselMapOwner = resolvingVessel;
                List<Part> selected = new List<Part>();
                foreach (uint id in memberIds) { List<Part> l; if (!resolvingMap.TryGetValue(id, out l) || l.Count != 1) { ClearResolver(); observationState = "unavailable"; observationReason = id == anchor ? "Visit the vessel containing the depot anchor" : "A registered part is missing or separated from the depot"; nextObservationAt = realTime + 2.0; return; } selected.Add(l[0]); }
                resolvedParts = selected; resolvedVessel = resolvingVessel; ClearResolver(); collectIndex = 0;
                collecting.Clear(); collectionReason = null; collectStartUt = Planetarium.GetUniversalTime(); observationStartedAt = realTime; observationState = "collecting"; observationReason = null;
                if (double.IsNaN(collectStartUt) || double.IsInfinity(collectStartUt)) { collectionReason = "Universal time was unavailable at observation start"; }
            }
            if (resolvedVessel == null || resolvedVessel != FlightGlobals.ActiveVessel || !activeWorld || !HighLogic.LoadedSceneIsFlight || !resolvedVessel.loaded || resolvedVessel.packed || resolvedParts == null) { AbortCollection("Depot context changed during observation"); return; }
            int stop = Math.Min(resolvedParts.Count, collectIndex + 64);
            for (; collectIndex < stop; collectIndex++)
            {
                Part p = resolvedParts[collectIndex];
                foreach (PartResource resource in p.Resources)
                {
                    string name = resource.resourceName;
                    if (string.IsNullOrEmpty(name) || double.IsNaN(resource.amount) || double.IsInfinity(resource.amount) || double.IsNaN(resource.maxAmount) || double.IsInfinity(resource.maxAmount) || resource.amount < 0 || resource.maxAmount < 0) { collectionReason = "A registered resource has invalid stock values"; continue; }
                    if (resource.amount > resource.maxAmount + Math.Max(1, Math.Abs(resource.maxAmount)) * 1e-9) collectionReason = "A registered resource amount exceeds its capacity";
                    DepotResourceRow row; if (!collecting.TryGetValue(name, out row)) collecting[name] = row = new DepotResourceRow { Name = name, DisplayName = ResourceDisplay(name) };
                    row.Amount += resource.amount; row.MaxAmount += resource.maxAmount;
                    if (double.IsNaN(row.Amount) || double.IsInfinity(row.Amount) || double.IsNaN(row.MaxAmount) || double.IsInfinity(row.MaxAmount)) collectionReason = "Aggregated resource values exceeded numeric bounds";
                }
            }
            if (collectIndex >= resolvedParts.Count)
            {
                bool coherent = resolvedVessel == FlightGlobals.ActiveVessel && HighLogic.LoadedSceneIsFlight && resolvedVessel != null && resolvedVessel.loaded && !resolvedVessel.packed && resolvedParts.Count == memberIds.Count;
                if (coherent)
                {
                    Dictionary<uint, int> currentCounts = new Dictionary<uint, int>();
                    HashSet<Part> currentParts = new HashSet<Part>(resolvedVessel.parts);
                    foreach (Part currentPart in resolvedVessel.parts)
                    {
                        int count;
                        currentCounts.TryGetValue(currentPart.persistentId, out count);
                        currentCounts[currentPart.persistentId] = count + 1;
                    }
                    for (int i = 0; i < memberIds.Count; i++)
                    {
                        Part p = resolvedParts[i];
                        int count;
                        if (p == null || p.persistentId != memberIds[i] || !currentParts.Contains(p) || !currentCounts.TryGetValue(memberIds[i], out count) || count != 1) { coherent = false; break; }
                    }
                }
                if (!coherent) { observationState = "unavailable"; observationReason = "Vessel topology changed during observation"; }
                else if (collectionReason != null || collecting.Count < 1 || collecting.Count > 128) { observationState = "unavailable"; observationReason = collectionReason ?? "Resource row count is outside supported bounds"; }
                else if (collecting.Values.Any(r => System.Text.Encoding.UTF8.GetByteCount(r.Name) > 128 || (r.DisplayName != null && System.Text.Encoding.UTF8.GetByteCount(r.DisplayName) > 128) || r.Amount > r.MaxAmount + Math.Max(1, Math.Abs(r.MaxAmount)) * 1e-9)) { observationState = "unavailable"; observationReason = "Resource aggregate is invalid or exceeds protocol bounds"; }
                else
                {
                    double completedUt = Planetarium.GetUniversalTime();
                    if (double.IsNaN(completedUt) || double.IsInfinity(completedUt)) { observationState = "unavailable"; observationReason = "Universal time was unavailable at observation completion"; collectIndex = 0; resolvedParts = null; nextObservationAt = observationStartedAt + 2.0; return; }
                    revision++; lastSnapshot = new DepotSnapshot { SessionId = session, LoadEpoch = epoch, WorldId = worldId, DepotId = depotId, MembershipRevision = membershipRevision,
                        Revision = revision, StartedUt = collectStartUt, CompletedUt = completedUt, AgeSeconds = 0, Resources = collecting.Values.OrderBy(x => x.Name, StringComparer.Ordinal).ToList() };
                    observationState = "complete"; observationReason = null; lastObservationAt = realTime;
                }
                collectIndex = 0; resolvedParts = null; nextObservationAt = observationStartedAt + 2.0;
            }
        }
        internal DepotView CloneView(bool activeWorld, bool loading, double realTime)
        {
            if (loading) return new DepotView { RegistryState = "loading", ObservationState = "none" };
            if (!activeWorld) return new DepotView { RegistryState = "noWorld", ObservationState = "none" };
            if (corrupt) return new DepotView { RegistryState = "unavailable", Reason = observationReason ?? "Saved depot data is corrupt or unsupported", ObservationState = "unavailable", ObservationReason = observationReason };
            if (!ready) return new DepotView { RegistryState = "loading", ObservationState = "none" };
            if (depotId == null) return new DepotView { RegistryState = activeWorld ? "none" : "noWorld", WorldId = activeWorld ? worldId : null, ObservationState = "none" };
            DepotSnapshot copy = null;
            if (lastSnapshot != null) copy = new DepotSnapshot { SessionId = lastSnapshot.SessionId, LoadEpoch = lastSnapshot.LoadEpoch, WorldId = lastSnapshot.WorldId, DepotId = lastSnapshot.DepotId, MembershipRevision = lastSnapshot.MembershipRevision, Revision = lastSnapshot.Revision, StartedUt = lastSnapshot.StartedUt, CompletedUt = lastSnapshot.CompletedUt, AgeSeconds = Math.Max(0, realTime - lastObservationAt), Resources = lastSnapshot.Resources.Select(r => new DepotResourceRow { Name = r.Name, DisplayName = r.DisplayName, Amount = r.Amount, MaxAmount = r.MaxAmount }).ToList() };
            return new DepotView { RegistryState = "registered", WorldId = worldId, DepotId = depotId, MembershipRevision = membershipRevision, Label = label, AnchorPartId = anchor, MemberCount = memberIds.Count, CurrentVesselName = activeWorld ? currentVesselName : null, ObservationState = observationState, ObservationReason = observationReason, Snapshot = copy };
        }

        internal DepotSummary[] CloneSummaries(bool activeWorld, bool loading, double realTime)
        {
            if (!activeWorld || loading || !ready || corrupt) return new DepotSummary[0];
            // This is a read-only game-state projection. It is deliberately separate
            // from the effect observations, which must remain loaded-vessel only.
            string context = worldId + "\n" + LegacyRegistrations.Count().ToString(CultureInfo.InvariantCulture) + "\n" +
                String.Join("|", LegacyRegistrations.Select(x => x.DepotId + ":" + x.MembershipRevision.ToString(CultureInfo.InvariantCulture)));
            if (displayContextKey != context || displayCapturedAt < 0 || realTime - displayCapturedAt >= 3.0)
            {
                displayContextKey = context;
                cachedDisplaySummaries = LegacyRegistrations.OrderBy(x => x.DepotId, StringComparer.Ordinal).Select(CaptureDisplaySummary).ToArray();
                displayCapturedAt = realTime;
            }
            return cachedDisplaySummaries.Select(x => new DepotSummary
            {
                DepotId = x.DepotId, Label = x.Label, MembershipRevision = x.MembershipRevision,
                MembershipHash = x.MembershipHash, MemberCount = x.MemberCount,
                Status = x.Status, Reason = x.Reason,
                StockAgeSeconds = x.StockAgeSeconds.HasValue ? (double?)Math.Max(0, realTime - displayCapturedAt) : null,
                Resources = x.Resources
            }).ToArray();
        }

        private DepotSummary CaptureDisplaySummary(DepotRegistration registration)
        {
            DepotSummary result = new DepotSummary
            {
                DepotId = registration.DepotId,
                Label = string.IsNullOrWhiteSpace(registration.Label) ? "Depot" : registration.Label,
                MembershipRevision = registration.MembershipRevision,
                MembershipHash = RegistrationMembershipHash(registration),
                MemberCount = registration.MemberIds.Count,
                Status = "unavailable", Reason = "Depot vessel is not present in KSP's current flight state"
            };
            if (registration.MemberIds.Count == 0 || registration.MemberIds.Count > MaxNewDepotMembers)
            { result.Reason = "Registered member count is outside the supported stock-view bound"; return result; }
            try
            {
                IList<Vessel> vessels = FlightGlobals.Vessels;
                if (vessels == null) { result.Reason = "KSP vessel list is unavailable"; return result; }
                Vessel owner = null;
                bool loaded = false;
                int inspected = 0, inspectedParts = 0;
                foreach (Vessel candidate in vessels)
                {
                    if (candidate == null) continue;
                    if (++inspected > 512) { result.Reason = "KSP vessel list exceeds the stock-view bound"; return result; }
                    bool hasAnchor = false;
                    if (candidate.loaded && candidate.parts != null && candidate.parts.Count <= MaxVesselParts)
                    {
                        inspectedParts += candidate.parts.Count;
                        if (inspectedParts > 32768) { result.Reason = "KSP part list exceeds the stock-view scan bound"; return result; }
                        hasAnchor = candidate.parts.Any(p => p != null && p.persistentId == registration.Anchor);
                    }
                    else if (candidate.protoVessel != null && candidate.protoVessel.protoPartSnapshots != null && candidate.protoVessel.protoPartSnapshots.Count <= MaxVesselParts)
                    {
                        inspectedParts += candidate.protoVessel.protoPartSnapshots.Count;
                        if (inspectedParts > 32768) { result.Reason = "KSP part list exceeds the stock-view scan bound"; return result; }
                        hasAnchor = candidate.protoVessel.protoPartSnapshots.Any(p => p != null && p.persistentId == registration.Anchor);
                    }
                    if (!hasAnchor) continue;
                    if (owner != null) { result.Reason = "Depot anchor appears on more than one vessel"; return result; }
                    owner = candidate;
                    loaded = candidate.loaded && candidate.parts != null;
                }
                if (owner == null) return result;
                HashSet<uint> selected = new HashSet<uint>(registration.MemberIds);
                Dictionary<uint, int> counts = new Dictionary<uint, int>();
                Dictionary<string, DepotResourceRow> totals = new Dictionary<string, DepotResourceRow>(StringComparer.Ordinal);
                int rowCount = 0;
                if (loaded)
                {
                    if (owner.parts.Count > MaxVesselParts) { result.Reason = "Depot vessel exceeds the part bound"; return result; }
                    foreach (Part part in owner.parts)
                    {
                        if (part == null || !selected.Contains(part.persistentId)) continue;
                        int count; counts.TryGetValue(part.persistentId, out count); counts[part.persistentId] = count + 1;
                        foreach (PartResource resource in part.Resources)
                        {
                            if (++rowCount > 512 || resource == null || !AddDisplayResource(totals, resource.resourceName, resource.amount, resource.maxAmount))
                            { result.Reason = "Depot resource data is invalid or exceeds the view bound"; return result; }
                        }
                    }
                }
                else
                {
                    // Unloaded ProtoVessel amounts are KSP's current stored snapshot.
                    // They are useful for display but are not authoritative for effects
                    // or proof of background production catch-up.
                    if (owner.protoVessel == null || owner.protoVessel.protoPartSnapshots == null || owner.protoVessel.protoPartSnapshots.Count > MaxVesselParts)
                    { result.Reason = "Unloaded vessel snapshot is unavailable"; return result; }
                    Dictionary<string, double> projected = TryReadBrpProjection(owner, selected);
                    foreach (ProtoPartSnapshot part in owner.protoVessel.protoPartSnapshots)
                    {
                        if (part == null || !selected.Contains(part.persistentId)) continue;
                        int count; counts.TryGetValue(part.persistentId, out count); counts[part.persistentId] = count + 1;
                        foreach (ProtoPartResourceSnapshot resource in part.resources)
                        {
                            double amount = resource == null ? 0 : resource.amount;
                            if (projected != null && resource != null) projected.TryGetValue(part.flightID.ToString(CultureInfo.InvariantCulture) + ":" + resource.resourceName, out amount);
                            if (++rowCount > 512 || resource == null || !AddDisplayResource(totals, resource.resourceName, amount, resource.maxAmount))
                            { result.Reason = "Unloaded resource snapshot is invalid or exceeds the view bound"; return result; }
                        }
                    }
                    if (projected != null)
                    {
                        result.Status = "live";
                        result.Reason = "Background Resource Processing projected stock";
                    }
                }
                if (registration.MemberIds.Any(id => !counts.ContainsKey(id) || counts[id] != 1))
                { result.Reason = "A registered member is missing or separated from its anchor vessel"; return result; }
                if (totals.Count == 0 || totals.Count > 128)
                { result.Reason = "Depot has no bounded resource stock to display"; return result; }
                result.Resources = totals.Values.OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
                if (loaded) { result.Status = "live"; result.Reason = null; }
                else if (result.Status != "live")
                { result.Status = "lastObserved"; result.Reason = "KSP unloaded-vessel snapshot; background production may update on vessel load"; }
                result.StockAgeSeconds = 0;
            }
            catch (Exception) { result.Reason = "KSP depot stock could not be read safely"; }
            return result;
        }

        private static bool AddDisplayResource(Dictionary<string, DepotResourceRow> totals, string name, double amount, double capacity)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || !FiniteNonNegative(amount) || !FiniteNonNegative(capacity) ||
                amount > capacity + Math.Max(1, Math.Abs(capacity)) * 1e-9) return false;
            DepotResourceRow row;
            if (!totals.TryGetValue(name, out row)) totals[name] = row = new DepotResourceRow { Name = name };
            row.Amount += amount; row.MaxAmount += capacity;
            return FiniteNonNegative(row.Amount) && FiniteNonNegative(row.MaxAmount);
        }

        // BRP 0.2.7's GetCurrentResourceStates projects each inventory as
        // Clamp(Amount + Rate * (UT - LastChangepoint), 0, MaxAmount). Recreate
        // that read-only projection for only the registered part flight IDs;
        // the aggregate API itself includes visitors and cannot be used here.
        // Any missing/ambiguous mapping falls back to KSP's proto snapshot.
        private static Dictionary<string, double> TryReadBrpProjection(Vessel vessel, HashSet<uint> selected)
        {
            try
            {
                if (vessel == null || vessel.vesselModules == null || vessel.protoVessel == null) return null;
                List<ProtoPartSnapshot> parts = vessel.protoVessel.protoPartSnapshots.Where(p => p != null && selected.Contains(p.persistentId)).ToList();
                if (parts.Count != selected.Count) return null;
                Dictionary<string, double> capacities = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (ProtoPartSnapshot part in parts)
                    foreach (ProtoPartResourceSnapshot resource in part.resources)
                    {
                        string key = part.flightID.ToString(CultureInfo.InvariantCulture) + ":" + resource.resourceName;
                        if (capacities.ContainsKey(key)) return null;
                        capacities.Add(key, resource.maxAmount);
                    }
                object processor = null;
                foreach (object module in vessel.vesselModules)
                    if (module != null && module.GetType().FullName == "BackgroundResourceProcessing.BackgroundResourceProcessor")
                    { if (processor != null) return null; processor = module; }
                if (processor == null) return null;
                Type type = processor.GetType();
                PropertyInfo inventoryProperty = type.GetProperty("Inventories", BindingFlags.Instance | BindingFlags.Public);
                PropertyInfo changepointProperty = type.GetProperty("LastChangepoint", BindingFlags.Instance | BindingFlags.Public);
                if (inventoryProperty == null || changepointProperty == null) return null;
                IEnumerable inventories = inventoryProperty.GetValue(processor, null) as IEnumerable;
                if (inventories == null) return null;
                double ut = Planetarium.GetUniversalTime();
                double changepoint = Convert.ToDouble(changepointProperty.GetValue(processor, null), CultureInfo.InvariantCulture);
                double dt = ut - changepoint;
                if (!FiniteNonNegative(dt) || dt > 1e12) return null;
                Dictionary<string, double> projected = new Dictionary<string, double>(StringComparer.Ordinal);
                int scanned = 0;
                foreach (object inventory in inventories)
                {
                    if (++scanned > 4096) return null;
                    if (inventory == null) return null;
                    Type inventoryType = inventory.GetType();
                    FieldInfo flightField = inventoryType.GetField("FlightId"), moduleField = inventoryType.GetField("ModuleId"),
                        amountField = inventoryType.GetField("Amount"), maxField = inventoryType.GetField("MaxAmount"), rateField = inventoryType.GetField("Rate");
                    PropertyInfo resourceProperty = inventoryType.GetProperty("ResourceName");
                    if (flightField == null || moduleField == null || amountField == null || maxField == null || rateField == null || resourceProperty == null) return null;
                    if (moduleField.GetValue(inventory) != null) continue;
                    string resourceName = resourceProperty.GetValue(inventory, null) as string;
                    string key = Convert.ToUInt32(flightField.GetValue(inventory), CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) + ":" + resourceName;
                    double capacity;
                    if (!capacities.TryGetValue(key, out capacity)) continue;
                    double amount = Convert.ToDouble(amountField.GetValue(inventory), CultureInfo.InvariantCulture);
                    double max = Convert.ToDouble(maxField.GetValue(inventory), CultureInfo.InvariantCulture);
                    double rate = Convert.ToDouble(rateField.GetValue(inventory), CultureInfo.InvariantCulture);
                    double value = Math.Max(0, Math.Min(max, amount + rate * dt));
                    if (!FiniteNonNegative(amount) || !FiniteNonNegative(max) || double.IsNaN(rate) || double.IsInfinity(rate) ||
                        !FiniteNonNegative(value) || Math.Abs(max - capacity) > Math.Max(1, capacity) * 1e-6 || projected.ContainsKey(key)) return null;
                    projected.Add(key, value);
                }
                return projected.Count == capacities.Count ? projected : null;
            }
            catch (Exception) { return null; }
        }

        internal StockObservation[] CloneInventoryObservations(string session, string epoch, bool activeWorld, string registryHash, double realTime)
        {
            string contextKey = session + "\n" + epoch + "\n" + worldId + "\n" + registryHash;
            if (!string.Equals(inventoryObservationContextKey, contextKey, StringComparison.Ordinal))
            {
                inventoryObservationContextKey = contextKey; inventoryObservationRevisions.Clear();
                cachedInventoryObservations = new StockObservation[0]; inventoryObservationCapturedAt = 0;
            }
            if (realTime - inventoryObservationCapturedAt >= 2.0)
            {
                long remoteStarted = WorldBridgeAddon.RemotePhysicalDiagnosticsEnabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                cachedInventoryObservations = CaptureInventoryObservations(session, epoch, activeWorld, registryHash, realTime);
                inventoryObservationCapturedAt = realTime;
                if (remoteStarted != 0)
                {
                    double elapsedMs = (System.Diagnostics.Stopwatch.GetTimestamp() - remoteStarted) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    UnityEngine.Debug.Log("[ExpanseRemoteBrp] effect stock scan ms=" + elapsedMs.ToString("F3", CultureInfo.InvariantCulture) +
                        " depots=" + cachedInventoryObservations.Length + " warpIndex=" + TimeWarp.CurrentRateIndex);
                }
            }
            return cachedInventoryObservations.Select(x => CloneStockObservation(x, realTime)).ToArray();
        }

        private StockObservation[] CaptureInventoryObservations(string session, string epoch, bool activeWorld, string registryHash, double realTime)
        {
            if (registrations.Count == 0) return new StockObservation[0];
            // The remote fixture uses BRP's current, selected provider inventory.
            // It never promotes the older M2 display projection into effect stock.
            if (activeWorld && HighLogic.LoadedSceneIsGame && WorldBridgeAddon.RemotePhysicalCandidateEnabled)
                return CaptureRemoteInventoryObservations(session, epoch, registryHash);
            string commonHold = null;
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (!activeWorld || !HighLogic.LoadedSceneIsFlight) commonHold = "Loaded Flight world is unavailable";
            else if (!FoundationRegistrationContext.CanEnumerate(vessel) || vessel.vesselType == VesselType.EVA) commonHold = "Only a loaded active vessel or anchored surface base can be observed";
            else if (vessel.parts.Count > MaxVesselParts) commonHold = "Active vessel exceeds the supported part bound";
            // The primary depot's incremental resolver may belong to a different vessel.
            // Build a bounded read-only map for the active base so its selected tanks
            // can be displayed while Expanse Foundations deliberately holds it packed.
            Dictionary<uint, List<Part>> observationMap = null;
            if (commonHold == null)
            {
                if (vessel == lastVesselMapOwner && lastVesselMap != null) observationMap = lastVesselMap;
                else
                {
                    observationMap = new Dictionary<uint, List<Part>>();
                    foreach (Part part in vessel.parts)
                    {
                        if (part == null) { commonHold = "Active vessel contains a missing part"; break; }
                        List<Part> matches;
                        if (!observationMap.TryGetValue(part.persistentId, out matches)) observationMap[part.persistentId] = matches = new List<Part>();
                        matches.Add(part);
                    }
                }
            }
            double observedUt = 0;
            try { observedUt = Planetarium.GetUniversalTime(); } catch { commonHold = "Universal time is unavailable"; }
            if (double.IsNaN(observedUt) || double.IsInfinity(observedUt) || observedUt < 0) commonHold = "Universal time is unavailable";
            List<StockObservation> output = new List<StockObservation>(registrations.Count);
            int allRows = 0;
            foreach (DepotRegistration registration in LegacyRegistrations.OrderBy(x => x.DepotId, StringComparer.Ordinal))
            {
                string hold = commonHold;
                if (hold == null && registration.MemberIds.Count > 64)
                    hold = "This depot exceeds the 64-member observation and write limit";
                List<Part> parts = new List<Part>();
                if (hold == null)
                {
                    foreach (uint memberId in registration.MemberIds)
                    {
                        List<Part> matches;
                        if (!observationMap.TryGetValue(memberId, out matches) || matches.Count != 1) { hold = memberId == registration.Anchor ? "Visit the depot anchor vessel to observe this endpoint" : "A registered member is missing, separated or ambiguous"; break; }
                        parts.Add(matches[0]);
                    }
                }
                List<MemberStockObservation> memberStocks = new List<MemberStockObservation>();
                Dictionary<string, StockAmount> aggregate = new Dictionary<string, StockAmount>(StringComparer.Ordinal);
                bool projectionSafe = hold == null;
                if (hold == null)
                {
                    foreach (Part part in parts.OrderBy(x => x.persistentId))
                    {
                        List<StockAmount> resources = new List<StockAmount>();
                        HashSet<string> uniqueNames = new HashSet<string>(StringComparer.Ordinal);
                        foreach (PartResource resource in part.Resources)
                        {
                            if (++allRows > 512) { hold = "Registered inventory exceeds 512 resource-row observation bound"; projectionSafe = false; break; }
                            if (resource == null || string.IsNullOrWhiteSpace(resource.resourceName) || !uniqueNames.Add(resource.resourceName)) { hold = "A member contains a missing or duplicate resource identity"; projectionSafe = false; break; }
                            if (!FiniteNonNegative(resource.amount) || !FiniteNonNegative(resource.maxAmount) || resource.amount > resource.maxAmount + Math.Max(1, Math.Abs(resource.maxAmount)) * 1e-9) { hold = "A registered resource has invalid stock or capacity"; projectionSafe = false; break; }
                            long amount, capacity;
                            if (!TryMicroUnits(resource.amount, out amount) || !TryMicroUnits(resource.maxAmount, out capacity)) { hold = "Stock exceeds safe micro-unit projection bounds"; projectionSafe = false; break; }
                            resources.Add(new StockAmount { ResourceName = resource.resourceName, AmountMicroUnits = amount, CapacityMicroUnits = capacity, DebitAllowed = resource.flowState });
                            StockAmount row;
                            if (!aggregate.TryGetValue(resource.resourceName, out row)) aggregate[resource.resourceName] = row = new StockAmount { ResourceName = resource.resourceName, DebitAllowed = true };
                            row.DebitAllowed = row.DebitAllowed == true && resource.flowState;
                            try { row.AmountMicroUnits = checked(row.AmountMicroUnits + amount); row.CapacityMicroUnits = checked(row.CapacityMicroUnits + capacity); }
                            catch (OverflowException) { hold = "Aggregated stock exceeds safe micro-unit bounds"; projectionSafe = false; break; }
                        }
                        if (hold != null) break;
                        memberStocks.Add(new MemberStockObservation { MemberPersistentId = part.persistentId, Resources = resources.OrderBy(x => x.ResourceName, StringComparer.Ordinal).ToArray() });
                    }
                }
                bool available = hold == null && memberStocks.Count == registration.MemberIds.Count;
                StockAmount[] resourcesOut = available ? aggregate.Values.OrderBy(x => x.ResourceName, StringComparer.Ordinal).ToArray() : new StockAmount[0];
                MemberStockObservation[] membersOut = available ? memberStocks.ToArray() : new MemberStockObservation[0];
                long revisionValue;
                if (!inventoryObservationRevisions.TryGetValue(registration.DepotId, out revisionValue)) revisionValue = 0;
                // This method creates a new completed bounded scan. Advance even when
                // stock is unchanged; Host's monotonic age floor treats the new revision
                // as a fresh sighting. CloneInventoryObservations reuses the cached
                // immutable rows between scans and therefore retains this revision.
                revisionValue = revisionValue == long.MaxValue ? 1 : revisionValue + 1;
                inventoryObservationRevisions[registration.DepotId] = revisionValue;
                output.Add(new StockObservation { DepotId = registration.DepotId, SessionId = Guid.Parse(session), LoadEpoch = Guid.Parse(epoch), WorldId = worldId,
                    RegistryHash = registryHash, MembershipRevision = registration.MembershipRevision,
                    MembershipHash = RegistrationMembershipHash(registration),
                    ObservationRevision = revisionValue, ObservedUt = observedUt, AgeSeconds = 0, Available = available, MicroUnitProjectionSafe = available && projectionSafe,
                    UnavailableReason = available ? string.Empty : (hold ?? "Registered inventory observation is incomplete"), Resources = resourcesOut, MemberStocks = membersOut });
            }
            return output.ToArray();
        }

        private StockObservation[] CaptureRemoteInventoryObservations(string session, string epoch, string registryHash)
        {
            double ut;
            try { ut = Planetarium.GetUniversalTime(); }
            catch { ut = Double.NaN; }
            RemoteBrpInventoryGateway gateway = new RemoteBrpInventoryGateway();
            LoadedBrpInventoryGateway loadedGateway = new LoadedBrpInventoryGateway();
            List<StockObservation> output = new List<StockObservation>(registrations.Count);
            foreach (DepotRegistration registration in LegacyRegistrations.OrderBy(x => x.DepotId, StringComparer.Ordinal))
            {
                string hold = null;
                if (Double.IsNaN(ut) || Double.IsInfinity(ut) || ut < 0) hold = "Universal time is unavailable";
                if (registration.MemberIds.Count == 0 || registration.MemberIds.Count > 64) hold = "Selected remote depot exceeds the 64-member bound";
                InventoryObservation observation = null;
                if (hold == null)
                {
                    uint[] members = registration.MemberIds.OrderBy(x => x).ToArray();
                    InventoryEndpoint endpoint = new InventoryEndpoint { DepotId = registration.DepotId, MembershipRevision = registration.MembershipRevision,
                        AnchorPersistentId = registration.Anchor, MemberPersistentIds = members,
                        MembershipHash = RegistrationMembershipHash(registration),
                        MemberSetHash = OperationIdentity.ComputeMemberSetHash(registration.Anchor, members), Scene = HighLogic.LoadedScene.ToString() };
                    observation = loadedGateway.Observe(loadedGateway.Resolve(endpoint));
                    if (observation == null) observation = gateway.Observe(gateway.Resolve(endpoint));
                    if (observation == null) hold = "Selected BRP inventory is unavailable or incoherent";
                }
                List<MemberStockObservation> selected = new List<MemberStockObservation>();
                Dictionary<string, StockAmount> totals = new Dictionary<string, StockAmount>(StringComparer.Ordinal);
                if (hold == null)
                {
                    if (observation.Rows == null || observation.Rows.Length > 512) hold = "Selected remote resource rows exceed the 512-row bound";
                    else foreach (uint member in registration.MemberIds.OrderBy(x => x))
                    {
                        List<StockAmount> rows = new List<StockAmount>();
                        foreach (InventoryStockRow row in observation.Rows.Where(x => x.MemberPersistentId == member))
                        {
                            long amount, capacity;
                            if (String.IsNullOrWhiteSpace(row.ResourceName) || !TryMicroUnits(row.Amount, out amount) ||
                                !TryMicroUnits(row.Capacity, out capacity) || amount > capacity)
                            { hold = "Remote BRP stock cannot be projected in bounded micro-units"; break; }
                            rows.Add(new StockAmount { ResourceName = row.ResourceName, AmountMicroUnits = amount, CapacityMicroUnits = capacity, DebitAllowed = row.DebitAllowed });
                            StockAmount total;
                            if (!totals.TryGetValue(row.ResourceName, out total)) totals[row.ResourceName] = total = new StockAmount { ResourceName = row.ResourceName, DebitAllowed = true };
                            total.DebitAllowed = total.DebitAllowed == true && row.DebitAllowed == true;
                            try { total.AmountMicroUnits = checked(total.AmountMicroUnits + amount); total.CapacityMicroUnits = checked(total.CapacityMicroUnits + capacity); }
                            catch (OverflowException) { hold = "Remote BRP stock aggregate exceeds the micro-unit bound"; break; }
                        }
                        if (hold != null) break;
                        selected.Add(new MemberStockObservation { MemberPersistentId = member, Resources = rows.OrderBy(x => x.ResourceName, StringComparer.Ordinal).ToArray() });
                    }
                }
                bool available = hold == null && selected.Count == registration.MemberIds.Count && totals.Count > 0;
                long revisionValue;
                if (!inventoryObservationRevisions.TryGetValue(registration.DepotId, out revisionValue)) revisionValue = 0;
                revisionValue = revisionValue == long.MaxValue ? 1 : revisionValue + 1;
                inventoryObservationRevisions[registration.DepotId] = revisionValue;
                output.Add(new StockObservation { DepotId = registration.DepotId, SessionId = Guid.Parse(session), LoadEpoch = Guid.Parse(epoch),
                    WorldId = worldId, RegistryHash = registryHash, MembershipRevision = registration.MembershipRevision,
                    MembershipHash = RegistrationMembershipHash(registration),
                    ObservationRevision = revisionValue, ObservedUt = Double.IsNaN(ut) ? 0 : ut, AgeSeconds = 0,
                    Available = available, MicroUnitProjectionSafe = available, UnavailableReason = available ? String.Empty : (hold ?? "Remote selected inventory is empty"),
                    Resources = available ? totals.Values.OrderBy(x => x.ResourceName, StringComparer.Ordinal).ToArray() : new StockAmount[0],
                    MemberStocks = available ? selected.ToArray() : new MemberStockObservation[0] });
            }
            return output.ToArray();
        }

        internal void InvalidateEffectInventoryObservations()
        {
            cachedInventoryObservations = new StockObservation[0];
            inventoryObservationCapturedAt = -1000;
        }

        private StockObservation CloneStockObservation(StockObservation x, double realTime)
        { return new StockObservation { DepotId = x.DepotId, SessionId = x.SessionId, LoadEpoch = x.LoadEpoch, WorldId = x.WorldId, RegistryHash = x.RegistryHash, MembershipRevision = x.MembershipRevision, MembershipHash = x.MembershipHash, ObservationRevision = x.ObservationRevision, ObservedUt = x.ObservedUt, AgeSeconds = Math.Max(0, realTime - inventoryObservationCapturedAt), Available = x.Available, MicroUnitProjectionSafe = x.MicroUnitProjectionSafe, UnavailableReason = x.UnavailableReason, Resources = x.Resources.Select(r => new StockAmount { ResourceName = r.ResourceName, AmountMicroUnits = r.AmountMicroUnits, CapacityMicroUnits = r.CapacityMicroUnits, DebitAllowed = r.DebitAllowed }).ToArray(), MemberStocks = x.MemberStocks.Select(m => new MemberStockObservation { MemberPersistentId = m.MemberPersistentId, Resources = m.Resources.Select(r => new StockAmount { ResourceName = r.ResourceName, AmountMicroUnits = r.AmountMicroUnits, CapacityMicroUnits = r.CapacityMicroUnits, DebitAllowed = r.DebitAllowed }).ToArray() }).ToArray() }; }

        private static bool TryMicroUnits(double value, out long units)
        {
            units = 0; double scaled = value * 1000000.0;
            if (double.IsNaN(scaled) || double.IsInfinity(scaled) || scaled < 0 || scaled > long.MaxValue) return false;
            units = (long)Math.Floor(scaled); return true;
        }

        private static bool FiniteNonNegative(double value) { return value >= 0 && !double.IsNaN(value) && !double.IsInfinity(value); }

        private string AvailabilityReason(bool activeWorld)
        {
            if (!activeWorld) return "Visit this depot to refresh stock";
            if (!HighLogic.LoadedSceneIsFlight) return "Visit this depot in Flight to refresh stock";
            Vessel v = FlightGlobals.ActiveVessel; if (v == null || !v.loaded) return "No loaded active vessel"; if (v.packed) return "Vessel is packed during warp";
            return null;
        }
        private static string ResourceDisplay(string name) { try { PartResourceDefinition d = PartResourceLibrary.Instance.GetDefinition(name); return d == null ? null : d.displayName; } catch { return null; } }
        private void OnTopologyChanged(Vessel ignored) { AbortCollection("Vessel topology changed"); }
        private void OnVesselChange(Vessel ignored) { AbortCollection("Active vessel changed"); }
        private void AbortCollection(string why) { collectIndex = 0; resolvedParts = null; resolvedVessel = null; lastVesselMap = null; lastVesselMapOwner = null; cachedInventoryObservations = new StockObservation[0]; inventoryObservationCapturedAt = 0; collecting.Clear(); ClearResolver(); observationState = "unavailable"; observationReason = why; nextObservationAt = 0; }
        private void ClearResolver() { resolvingSource = null; resolvingVessel = null; resolvingMap = null; resolvingIndex = 0; }
        private void ClearObservation(string why) { lastSnapshot = null; revision = 0; resolvedVessel = null; currentVesselName = null; AbortCollection(why); cachedDisplaySummaries = new DepotSummary[0]; displayCapturedAt = -1; displayContextKey = null; observationState = depotId == null ? "none" : "unavailable"; }
        private void SetPrimary(DepotRegistration registration)
        {
            depotId = registration.DepotId; label = registration.Label; anchor = registration.Anchor; membershipRevision = registration.MembershipRevision;
            memberIds.Clear(); memberIds.AddRange(registration.MemberIds);
        }
        private void ClearRegistration() { worldId = depotId = label = null; rawUnknown = null; rawUnknownRegistryRoots.Clear(); anchor = 0; membershipRevision = 0; memberIds.Clear(); registrations.Clear(); corrupt = false; lastSnapshot = null; cachedRegistrySnapshot = null; resolvedParts = null; resolvedVessel = null; ClearResolver(); currentVesselName = null; collectIndex = 0; collecting.Clear(); nextObservationAt = 0; revision = 0; }
        private static string Bound(string s, int max) { if (s == null) return "Depot"; s = s.Trim(); if (s.Length > max) s = s.Substring(0, max); return s; }
        public void OnDestroy() { GameEvents.onVesselWasModified.Remove(OnTopologyChanged); GameEvents.onVesselChange.Remove(OnVesselChange); if (instance == this) instance = null; }
    }
}
