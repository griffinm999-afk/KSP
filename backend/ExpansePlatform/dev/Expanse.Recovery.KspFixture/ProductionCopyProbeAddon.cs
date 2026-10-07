using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Expanse.Domain;
using Expanse.WorldBridge;
using UnityEngine;

namespace Expanse.Recovery.KspFixture
{
    // Read-only, one-shot probe for a separately verified copy of the user's
    // saved game. The original save/install are never opened by this addon.
    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public sealed class ProductionCopyProbeAddon : MonoBehaviour
    {
        private const string ReducedDevRoot = @"C:\Users\griff\Documents\KSP-RMM-Dev";
        private const string FullDevRoot = @"C:\Users\griff\Documents\KSP-RMM-Dev-Full";
        private const string RequestName = "ExpanseProductionCopyProbe.request";
        private const string RequestPrefix = "PROBE_EXPANSE_PRODUCTION_COPY=";
        private static readonly string[] Names = { "LiquidFuel", "MonoPropellant", "Oxidizer" };
        private static readonly double[] Cargo = { 1000, 500, 1400 };
        private static string claimedToken;
        private string root, folder, token, logPath, copiedSave, expectedHash;
        private float startedAt;

        private sealed class Depot
        {
            internal string Id, Label;
            internal uint Anchor;
            internal uint[] Members;
        }
        private sealed class Row
        {
            internal uint Member;
            internal string Name;
            internal double Amount, Capacity;
            internal bool Flow;
        }

        private void Start()
        {
            try
            {
                root = Path.GetFullPath(KSPUtil.ApplicationRootPath).TrimEnd('\\', '/');
                if (!String.Equals(root, ReducedDevRoot, StringComparison.OrdinalIgnoreCase) &&
                    !String.Equals(root, FullDevRoot, StringComparison.OrdinalIgnoreCase)) return;
                CheckPath(root);
                string requestPath = Path.Combine(root, RequestName);
                if (!File.Exists(requestPath)) return;
                CheckPath(requestPath);
                if (new FileInfo(requestPath).Length > 128) throw new InvalidDataException("Probe request too large");
                string request = File.ReadAllText(requestPath).Trim();
                Guid parsed;
                if (!request.StartsWith(RequestPrefix, StringComparison.Ordinal) || request.Length != RequestPrefix.Length + 32 ||
                    !Guid.TryParseExact(request.Substring(RequestPrefix.Length), "N", out parsed) || parsed == Guid.Empty) return;
                token = parsed.ToString("N"); folder = "ExpanseProductionCopyProbe-" + token;
                string saveRoot = Path.GetFullPath(Path.Combine(root, "saves"));
                string saveFolder = Path.GetFullPath(Path.Combine(saveRoot, folder));
                copiedSave = Path.GetFullPath(Path.Combine(saveFolder, "persistent.sfs"));
                string manifest = Path.GetFullPath(Path.Combine(saveFolder, "copy-manifest.json"));
                logPath = Path.GetFullPath(Path.Combine(root, folder + ".log"));
                if (!saveFolder.StartsWith(saveRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(copiedSave) || !File.Exists(manifest) || Directory.Exists(logPath) ||
                    (File.Exists(logPath) && new FileInfo(logPath).Length > 65536))
                    throw new InvalidDataException("Token-scoped copied save, manifest, or log path is invalid");
                CheckPath(saveRoot); CheckPath(saveFolder); CheckPath(copiedSave); CheckPath(manifest); CheckPath(logPath);
                if (new FileInfo(manifest).Length > 4096) throw new InvalidDataException("Copy manifest too large");
                string manifestText = File.ReadAllText(manifest);
                if (ManifestField(manifestText, "Token") != token || ManifestField(manifestText, "Folder") != folder ||
                    !String.Equals(Path.GetFullPath(ManifestField(manifestText, "Target")), copiedSave, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Copy manifest identity/path differs from token-scoped save");
                expectedHash = ManifestField(manifestText, "Sha256");
                if (!Regex.IsMatch(expectedHash, "^[A-Fa-f0-9]{64}$") ||
                    !String.Equals(Hash(copiedSave), expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Copied save hash differs from independent copy manifest");
                if (claimedToken != null) return;
                // Consume the exact token before changing scenes. Otherwise a
                // second KSPAddon instance can start the same probe at KSC.
                File.Delete(requestPath);
                claimedToken = token;
                Line("REQUEST_CLAIMED token=" + token + " copySha256=" + expectedHash);
                DontDestroyOnLoad(gameObject);
                startedAt = Time.realtimeSinceStartup;
                StartCoroutine(Supervise());
            }
            catch (Exception ex) { UnityEngine.Debug.LogError("[ExpanseProductionCopyProbe] setup rejected " + ex); }
        }

        private IEnumerator Supervise()
        {
            IEnumerator body = Run();
            while (Time.realtimeSinceStartup - startedAt < 900f)
            {
                bool more; object current;
                try { more = body.MoveNext(); current = more ? body.Current : null; }
                catch (Exception ex) { Line("FAIL " + ex); Application.Quit(); yield break; }
                if (!more) yield break;
                yield return current;
            }
            Line("FAIL global 900s watchdog"); Application.Quit();
        }

        private IEnumerator Run()
        {
            HighLogic.SaveFolder = folder;
            Game loaded = GamePersistence.LoadGame("persistent", folder, true, false);
            if (loaded == null) throw new InvalidDataException("Token-scoped production copy did not load");
            if (loaded.flightState == null || loaded.flightState.protoVessels == null ||
                loaded.flightState.protoVessels.Count == 0 || loaded.flightState.protoVessels.Count > 512)
                throw new InvalidDataException("Copied FlightState has no bounded proto vessel list");
            int activeIndex = Convert.ToInt32(Read(loaded.flightState, "activeVesselIdx"), CultureInfo.InvariantCulture);
            if (activeIndex < 0 || activeIndex >= loaded.flightState.protoVessels.Count ||
                loaded.flightState.protoVessels[activeIndex] == null)
                throw new InvalidDataException("Copied FlightState active vessel index is invalid");
            Line("SAVED_FLIGHTSTATE protoCount=" + loaded.flightState.protoVessels.Count +
                " activeIndex=" + activeIndex + " activeId=" + loaded.flightState.protoVessels[activeIndex].vesselID +
                " activeName=" + loaded.flightState.protoVessels[activeIndex].vesselName +
                " copySha256=" + Hash(copiedSave));
            // A directly started Space Center game leaves FlightGlobals.Vessels empty
            // for this copied save. Flight reconstructs KSP Vessel wrappers first.
            HighLogic.CurrentGame = loaded; loaded.startScene = GameScenes.FLIGHT; loaded.Start();
            Line("FLIGHT_START elapsed=" + (Time.realtimeSinceStartup - startedAt).ToString("F1", CultureInfo.InvariantCulture));
            float flightDeadline = Time.realtimeSinceStartup + 360f, nextFlightLog = 0f;
            while (Time.realtimeSinceStartup < flightDeadline)
            {
                if (HighLogic.LoadedScene == GameScenes.FLIGHT && HighLogic.SaveFolder == folder &&
                    FlightGlobals.ActiveVessel != null && FlightGlobals.Vessels != null && FlightGlobals.Vessels.Count > 0)
                    break;
                if (Time.realtimeSinceStartup >= nextFlightLog)
                {
                    Line("FLIGHT_WAIT elapsed=" + (Time.realtimeSinceStartup - startedAt).ToString("F1", CultureInfo.InvariantCulture) +
                        " scene=" + HighLogic.LoadedScene + " folder=" + HighLogic.SaveFolder +
                        " active=" + (FlightGlobals.ActiveVessel == null ? "none" : FlightGlobals.ActiveVessel.id.ToString("D")) +
                        " vesselCount=" + (FlightGlobals.Vessels == null ? -1 : FlightGlobals.Vessels.Count));
                    nextFlightLog = Time.realtimeSinceStartup + 15f;
                }
                yield return new WaitForSecondsRealtime(0.25f);
            }
            if (HighLogic.LoadedScene != GameScenes.FLIGHT || HighLogic.SaveFolder != folder ||
                FlightGlobals.ActiveVessel == null || FlightGlobals.Vessels == null || FlightGlobals.Vessels.Count == 0)
                throw new TimeoutException("Copied FlightState did not reconstruct vessels in exact disposable folder");
            if (FlightGlobals.ActiveVessel.id != loaded.flightState.protoVessels[activeIndex].vesselID)
                throw new InvalidDataException("Flight active vessel differs from copied FlightState active index");
            Line("FLIGHT_RECONSTRUCTED active=" + FlightGlobals.ActiveVessel.id + ":" + FlightGlobals.ActiveVessel.vesselName +
                " vesselCount=" + FlightGlobals.Vessels.Count + " copySha256=" + Hash(copiedSave));
            if (!String.Equals(Hash(copiedSave), expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Copied persistent changed during Flight reconstruction");
            HighLogic.LoadScene(GameScenes.SPACECENTER);
            Line("KSC_START elapsed=" + (Time.realtimeSinceStartup - startedAt).ToString("F1", CultureInfo.InvariantCulture));
            float deadline = Time.realtimeSinceStartup + 240f, nextKscLog = 0f;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (HighLogic.LoadedScene == GameScenes.SPACECENTER && HighLogic.SaveFolder == folder &&
                    HighLogic.CurrentGame != null && DepotRegistryModule.Instance != null && DepotRegistryModule.Instance.IsReady &&
                    RecoveryCapsuleModule.Instance != null && RecoveryCapsuleModule.Instance.IsLoaded && FlightGlobals.Vessels != null) break;
                if (Time.realtimeSinceStartup >= nextKscLog)
                {
                    Line("KSC_WAIT elapsed=" + (Time.realtimeSinceStartup - startedAt).ToString("F1", CultureInfo.InvariantCulture) +
                        " scene=" + HighLogic.LoadedScene + " folder=" + HighLogic.SaveFolder +
                        " vesselCount=" + (FlightGlobals.Vessels == null ? -1 : FlightGlobals.Vessels.Count));
                    nextKscLog = Time.realtimeSinceStartup + 15f;
                }
                yield return new WaitForSecondsRealtime(0.25f);
            }
            if (HighLogic.LoadedScene != GameScenes.SPACECENTER || HighLogic.SaveFolder != folder ||
                DepotRegistryModule.Instance == null || !DepotRegistryModule.Instance.IsReady ||
                RecoveryCapsuleModule.Instance == null || !RecoveryCapsuleModule.Instance.IsLoaded || FlightGlobals.Vessels == null)
                throw new TimeoutException("Copied save did not become ready in exact disposable folder");
            if (!String.Equals(Hash(copiedSave), expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Copied persistent changed during game load");
            Line("START token=" + token + " root=" + root + " folder=" + HighLogic.SaveFolder +
                " copySha256=" + Hash(copiedSave) + " sameGame=" + (HighLogic.CurrentGame == loaded) +
                " protoCount=" + (HighLogic.CurrentGame == null || HighLogic.CurrentGame.flightState == null ||
                    HighLogic.CurrentGame.flightState.protoVessels == null ? -1 : HighLogic.CurrentGame.flightState.protoVessels.Count));
            ConfigNode wrapper = ConfigNode.Load(copiedSave);
            ConfigNode game = wrapper == null ? null : wrapper.name == "GAME" ? wrapper :
                wrapper.GetNodes("GAME").Length == 1 ? wrapper.GetNodes("GAME")[0] : null;
            if (game == null) throw new InvalidDataException("Copied SFS lacks unique GAME root");
            ConfigNode[] registryNodes = game.GetNodes("SCENARIO").Where(x => x.GetValue("name") == "DepotRegistryModule").ToArray();
            if (registryNodes.Length != 1 || registryNodes[0].GetNodes("DEPOT_REGISTRY").Length != 1)
                throw new InvalidDataException("Copied SFS lacks unique depot registry");
            ConfigNode[] nodes = registryNodes[0].GetNodes("DEPOT_REGISTRY")[0].GetNodes("DEPOT");
            Depot[] depots = nodes.Select(ParseDepot).ToArray();
            Depot source = depots.Single(x => x.Label == "Minmus Mining");
            Depot destination = depots.Single(x => x.Label == "Fuel Depot");
            if (source.Id == destination.Id || source.Members.Intersect(destination.Members).Any() ||
                DepotRegistryModule.Instance.WorldId != registryNodes[0].GetNodes("DEPOT_REGISTRY")[0].GetValue("worldId"))
                throw new InvalidDataException("Copied source/destination registry identity differs from loaded game");
            AcceptedState state = AcceptedStateCodec.Deserialize(RecoveryCapsuleModule.Instance.GetAcceptedStateBytes());
            RouteVersionRecord[] routes = state.RouteVersions.Where(x => !x.LegacyOpaque &&
                x.SourceDepotId == source.Id && x.DestinationDepotId == destination.Id &&
                Names.Select((name, i) => new { name, amount = (long)(Cargo[i] * 1000000) })
                    .All(want => x.Resources.Any(r => r.ResourceName == want.name && r.AmountMicroUnits == want.amount))).ToArray();
            if (routes.Length != 1) throw new InvalidDataException("Copied accepted capsule lacks one exact 1000/500/1400 route");
            Line("ROUTE_IDENTIFIED world=" + state.WorldId + " routeId=" + routes[0].RouteId + " version=" + routes[0].Version +
                " sourceDepot=" + source.Id + " sourceMembers=" + source.Members.Length + " destinationDepot=" + destination.Id +
                " destinationMembers=" + destination.Members.Length + " capsuleHash=" + AcceptedStateCodec.ComputeHash(state));
            float ownerDeadline = Time.realtimeSinceStartup + 180f, nextOwnerLog = 0f;
            while (Time.realtimeSinceStartup < ownerDeadline && (!OwnerReady(source) || !OwnerReady(destination)))
            {
                if (Time.realtimeSinceStartup >= nextOwnerLog)
                {
                    Line("OWNER_WAIT scene=" + HighLogic.LoadedScene + " folder=" + HighLogic.SaveFolder +
                        " active=" + (FlightGlobals.ActiveVessel == null ? "none" : FlightGlobals.ActiveVessel.id + ":" + FlightGlobals.ActiveVessel.vesselName) +
                        " vesselCount=" + (FlightGlobals.Vessels == null ? -1 : FlightGlobals.Vessels.Count) +
                        " protoCount=" + (HighLogic.CurrentGame == null || HighLogic.CurrentGame.flightState == null ||
                            HighLogic.CurrentGame.flightState.protoVessels == null ? -1 : HighLogic.CurrentGame.flightState.protoVessels.Count) +
                        " source=" + OwnerDescription(source) + " destination=" + OwnerDescription(destination));
                    nextOwnerLog = Time.realtimeSinceStartup + 5f;
                }
                yield return new WaitForSecondsRealtime(0.25f);
            }
            if (!OwnerReady(source) || !OwnerReady(destination))
                throw new InvalidOperationException("Selected copied owners did not become unique unloaded vessels: source=" +
                    OwnerDescription(source) + " destination=" + OwnerDescription(destination));
            for (int pass = 0; pass < 5; pass++)
            {
                if (pass == 1) TimeWarp.SetRate(2, true);
                long started = Stopwatch.GetTimestamp();
                Guid sourceVessel, destinationVessel;
                Row[] sourceRows = Probe(source, out sourceVessel), destinationRows = Probe(destination, out destinationVessel);
                double elapsedMs = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
                if (sourceVessel == destinationVessel) throw new InvalidOperationException("Copied source and destination resolve to same vessel");
                int sourceDeltas = AllocationCount(sourceRows, true, false), destinationDeltas = AllocationCount(destinationRows, false, false);
                int sourceFlowDeltas = AllocationCount(sourceRows, true, true), destinationFlowDeltas = AllocationCount(destinationRows, false, true);
                Line("PASS_READ pass=" + pass + " warpIndex=" + TimeWarp.CurrentRateIndex + " scanMs=" + elapsedMs.ToString("F3", CultureInfo.InvariantCulture) +
                    " sourceVessel=" + sourceVessel.ToString("D") + " destinationVessel=" + destinationVessel.ToString("D") +
                    " source=" + Totals(sourceRows) + " destination=" + Totals(destinationRows) +
                    " sourceDeltaRowsAll=" + sourceDeltas + " destinationDeltaRowsAll=" + destinationDeltas +
                    " sourceDeltaRowsFlowEnabled=" + sourceFlowDeltas + " destinationDeltaRowsFlowEnabled=" + destinationFlowDeltas +
                    " bound=16 allEligible=" + (sourceDeltas > 0 && sourceDeltas <= 16 && destinationDeltas > 0 && destinationDeltas <= 16) +
                    " flowEnabledEligible=" + (sourceFlowDeltas > 0 && sourceFlowDeltas <= 16 && destinationFlowDeltas > 0 && destinationFlowDeltas <= 16) +
                    " policyEligible=" + (sourceFlowDeltas > 0 && sourceFlowDeltas <= 16 && destinationDeltas > 0 && destinationDeltas <= 16));
                if (pass == 0)
                {
                    Line("FLOW_ROWS source=" + FlowRows(sourceRows) + " destination=" + FlowRows(destinationRows));
                    Line("ALLOCATION sourceAll=" + AllocationRows(sourceRows, true, false) +
                        " sourceFlowEnabled=" + AllocationRows(sourceRows, true, true) +
                        " destinationAll=" + AllocationRows(destinationRows, false, false) +
                        " destinationFlowEnabled=" + AllocationRows(destinationRows, false, true));
                }
                if (pass < 4) yield return new WaitForSecondsRealtime(2f);
            }
            TimeWarp.SetRate(0, true);
            Line("PROBE_COMPLETE no delivery effects or save writes; production-copy manifest=" + expectedHash);
            Application.Quit();
        }

        private static Depot ParseDepot(ConfigNode node)
        {
            uint anchor;
            if (!UInt32.TryParse(node.GetValue("anchorPartId"), NumberStyles.None, CultureInfo.InvariantCulture, out anchor) || anchor == 0)
                throw new InvalidDataException("Depot anchor invalid");
            uint[] members = node.GetNodes("member").Select(x => UInt32.Parse(x.GetValue("id"), CultureInfo.InvariantCulture)).ToArray();
            if (members.Length == 0 || members.Length > 64 || members.Any(x => x == 0) || members.Distinct().Count() != members.Length || !members.Contains(anchor))
                throw new InvalidDataException("Depot selected member set invalid");
            return new Depot { Id = node.GetValue("depotId"), Label = node.GetValue("label"), Anchor = anchor, Members = members };
        }

        private static bool OwnerReady(Depot depot)
        {
            if (FlightGlobals.Vessels == null) return false;
            Vessel[] matches = FlightGlobals.Vessels.Where(v => v != null && v.protoVessel != null &&
                v.protoVessel.protoPartSnapshots.Any(p => p != null && p.persistentId == depot.Anchor)).ToArray();
            return matches.Length == 1 && !matches[0].loaded;
        }
        private static string OwnerDescription(Depot depot)
        {
            if (FlightGlobals.Vessels == null) return "vessels-null";
            Vessel[] matches = FlightGlobals.Vessels.Where(v => v != null && v.protoVessel != null &&
                v.protoVessel.protoPartSnapshots.Any(p => p != null && p.persistentId == depot.Anchor)).ToArray();
            string exact = String.Join(",", matches.Select(v => v.id + ":" + v.vesselName + ":loaded=" + v.loaded +
                ":packed=" + v.packed + ":type=" + v.vesselType + ":parts=" + v.protoVessel.protoPartSnapshots.Count).ToArray());
            string named = String.Join(",", FlightGlobals.Vessels.Where(v => v != null && v.vesselName == depot.Label).Select(v =>
                v.id + ":loaded=" + v.loaded + ":packed=" + v.packed + ":proto=" + (v.protoVessel != null) +
                ":anchorCount=" + (v.protoVessel == null ? -1 : v.protoVessel.protoPartSnapshots.Count(p => p != null && p.persistentId == depot.Anchor))).ToArray());
            return "anchor=" + depot.Anchor + ":matches=" + matches.Length + "[" + exact + "]:named=[" + named + "]";
        }

        private static Row[] Probe(Depot depot, out Guid vesselId)
        {
            if (FlightGlobals.Vessels.Count > 512) throw new InvalidOperationException("Vessel scan bound exceeded");
            HashSet<uint> selected = new HashSet<uint>(depot.Members);
            Vessel[] owners = FlightGlobals.Vessels.Where(v => v != null && v.protoVessel != null &&
                v.protoVessel.protoPartSnapshots.Any(p => p != null && p.persistentId == depot.Anchor)).ToArray();
            if (owners.Length != 1 || owners[0].loaded) throw new InvalidOperationException("Selected copied depot owner is missing, ambiguous, or loaded: " + depot.Label);
            Vessel owner = owners[0]; vesselId = owner.id;
            foreach (uint id in selected)
                if (FlightGlobals.Vessels.Sum(v => v == null || v.protoVessel == null ? 0 :
                    v.protoVessel.protoPartSnapshots.Count(p => p != null && p.persistentId == id)) != 1)
                    throw new InvalidOperationException("Selected copied member identity is missing or duplicated");
            object[] processors = ((IEnumerable)Read(owner, "vesselModules")).Cast<object>().Where(x => x != null &&
                x.GetType().FullName == "BackgroundResourceProcessing.BackgroundResourceProcessor").ToArray();
            if (processors.Length != 1) throw new InvalidOperationException("Copied vessel lacks one BRP processor");
            object processor = processors[0];
            Invoke(processor, "UpdateBackgroundState");
            object[] provider = ((IEnumerable)Read(processor, "Inventories")).Cast<object>().Where(x => x != null).ToArray();
            if (provider.Length > 4096) throw new InvalidOperationException("BRP inventory bound exceeded");
            List<Row> rows = new List<Row>();
            foreach (uint id in selected)
            {
                ProtoPartSnapshot[] parts = owner.protoVessel.protoPartSnapshots.Where(p => p != null && p.persistentId == id).ToArray();
                if (parts.Length != 1 || parts[0].flightID == 0) throw new InvalidOperationException("Copied selected part identity changed");
                foreach (ProtoPartResourceSnapshot snapshot in parts[0].resources)
                {
                    if (snapshot == null || !Names.Contains(snapshot.resourceName)) continue;
                    object[] matches = provider.Where(x => Read(x, "ModuleId") == null &&
                        Convert.ToUInt32(Read(x, "FlightId"), CultureInfo.InvariantCulture) == parts[0].flightID &&
                        Convert.ToString(Read(x, "ResourceName"), CultureInfo.InvariantCulture) == snapshot.resourceName).ToArray();
                    if (matches.Length != 1 || !System.Object.ReferenceEquals(Read(matches[0], "Snapshot"), snapshot))
                        throw new InvalidOperationException("Copied selected BRP snapshot ownership mismatch");
                    double amount = Convert.ToDouble(Read(matches[0], "Amount"), CultureInfo.InvariantCulture);
                    double original = Convert.ToDouble(Read(matches[0], "OriginalAmount"), CultureInfo.InvariantCulture);
                    double capacity = Convert.ToDouble(Read(matches[0], "MaxAmount"), CultureInfo.InvariantCulture);
                    if (Double.IsNaN(amount) || Double.IsInfinity(amount) || amount < 0 || amount > capacity ||
                        original != amount || snapshot.amount != amount || snapshot.maxAmount != capacity)
                        throw new InvalidOperationException("Copied selected BRP stock/capacity is stale or invalid depot=" + depot.Label +
                            " vessel=" + owner.id.ToString("D") + " member=" + id.ToString(CultureInfo.InvariantCulture) +
                            " resource=" + snapshot.resourceName + " providerAmount=" + amount.ToString("R", CultureInfo.InvariantCulture) +
                            " providerOriginal=" + original.ToString("R", CultureInfo.InvariantCulture) +
                            " providerMax=" + capacity.ToString("R", CultureInfo.InvariantCulture) +
                            " snapshotAmount=" + snapshot.amount.ToString("R", CultureInfo.InvariantCulture) +
                            " snapshotMax=" + snapshot.maxAmount.ToString("R", CultureInfo.InvariantCulture) +
                            " flow=" + snapshot.flowState);
                    rows.Add(new Row { Member = id, Name = snapshot.resourceName, Amount = amount, Capacity = capacity,
                        Flow = snapshot.flowState });
                }
            }
            if (rows.GroupBy(x => x.Member.ToString(CultureInfo.InvariantCulture) + "/" + x.Name).Any(x => x.Count() != 1))
                throw new InvalidOperationException("Copied selected resource row duplicated");
            return rows.ToArray();
        }

        private static int AllocationCount(Row[] rows, bool debit, bool flowEnabledOnly)
        {
            int count = 0;
            for (int i = 0; i < Names.Length; i++)
            {
                double remain = Cargo[i];
                foreach (Row row in rows.Where(x => x.Name == Names[i] && (!flowEnabledOnly || x.Flow)).OrderBy(x => x.Member))
                {
                    double available = debit ? row.Amount : row.Capacity - row.Amount;
                    double allocation = Math.Min(remain, available);
                    if (allocation > 0) { count++; remain -= allocation; }
                    if (remain == 0) break;
                }
                if (remain != 0) return -1;
            }
            return count;
        }
        private static string Totals(Row[] rows) => String.Join(",", Names.Select(name => name + ":" +
            rows.Where(x => x.Name == name).Sum(x => x.Amount).ToString("R", CultureInfo.InvariantCulture) + "/" +
            rows.Where(x => x.Name == name).Sum(x => x.Capacity).ToString("R", CultureInfo.InvariantCulture)).ToArray());
        private static string FlowRows(Row[] rows) => String.Join(",", rows.OrderBy(x => x.Member).ThenBy(x => x.Name, StringComparer.Ordinal)
            .Select(x => x.Member.ToString(CultureInfo.InvariantCulture) + "/" + x.Name + "=" + (x.Flow ? "on" : "off")).ToArray());
        private static string AllocationRows(Row[] rows, bool debit, bool flowEnabledOnly)
        {
            List<string> selected = new List<string>();
            for (int i = 0; i < Names.Length; i++)
            {
                double remain = Cargo[i];
                foreach (Row row in rows.Where(x => x.Name == Names[i] && (!flowEnabledOnly || x.Flow)).OrderBy(x => x.Member))
                {
                    double available = debit ? row.Amount : row.Capacity - row.Amount;
                    double allocation = Math.Min(remain, available);
                    if (allocation > 0)
                    {
                        selected.Add(row.Member.ToString(CultureInfo.InvariantCulture) + "/" + row.Name + ":" +
                            allocation.ToString("R", CultureInfo.InvariantCulture) + ":flow=" + (row.Flow ? "on" : "off"));
                        remain -= allocation;
                    }
                    if (remain == 0) break;
                }
                if (remain != 0) selected.Add(Names[i] + ":SHORT=" + remain.ToString("R", CultureInfo.InvariantCulture));
            }
            return String.Join(",", selected.ToArray());
        }
        private static object Read(object value, string name)
        {
            FieldInfo field = value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null) return field.GetValue(value);
            PropertyInfo property = value.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null) return property.GetValue(value, null);
            throw new MissingMemberException(value.GetType().FullName, name);
        }
        private static void Invoke(object value, string name)
        {
            MethodInfo method = value.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, Type.EmptyTypes, null);
            if (method == null) throw new MissingMethodException(value.GetType().FullName, name);
            method.Invoke(value, null);
        }
        private static string ManifestField(string json, string name)
        {
            Match match = Regex.Match(json, "\\\"" + name + "\\\"\\s*:\\s*\\\"([^\\\"]{1,512})\\\"");
            if (!match.Success) throw new InvalidDataException("Copy manifest lacks " + name);
            return match.Groups[1].Value.Replace("\\\\", "\\");
        }
        private static string Hash(string path)
        {
            using (SHA256 sha = SHA256.Create()) using (FileStream stream = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", String.Empty);
        }
        private static void CheckPath(string path)
        {
            string current = Path.GetFullPath(path);
            while (!String.IsNullOrEmpty(current))
            {
                if (File.Exists(current) || Directory.Exists(current))
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse path rejected");
                string parent = Path.GetDirectoryName(current);
                if (parent == current) break;
                current = parent;
            }
        }
        private void Line(string value)
        {
            File.AppendAllText(logPath, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " " + value + Environment.NewLine);
        }
    }
}
