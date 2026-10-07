using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Expanse.Domain;
using Expanse.WorldBridge;
using UnityEngine;

namespace Expanse.Recovery.KspFixture
{
    // Exact saved-route delivery fixture for a separately verified copy of the
    // user's save in the full development clone. It never runs in regular KSP.
    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public sealed class ProductionCopyDeliverySmokeAddon : MonoBehaviour
    {
        private const string FullDevRoot = @"C:\Users\griff\Documents\KSP-RMM-Dev-Full";
        private const string RequestName = "ExpanseProductionCopyDelivery.request";
        private const string RequestPrefix = "RUN_EXPANSE_PRODUCTION_COPY_DELIVERY=";
        private const string ResumePrefix = "RESUME_EXPANSE_PRODUCTION_COPY_DELIVERY=";
        private const string FinalAuditPrefix = "AUDIT_EXPANSE_PRODUCTION_COPY_DELIVERY=";
        private const string TransitSave = "production-copy-delivery-transit";
        private const string FinalSave = "production-copy-delivery-final";
        private const int MaxParts = 16384;
        private static readonly string[] Names = { "LiquidFuel", "MonoPropellant", "Oxidizer" };
        private static readonly double[] Cargo = { 1000.0, 500.0, 1400.0 };

        private string root, token, folder, logPath, sourceDepotId, destinationDepotId, worldId;
        private Guid sourceVesselId, destinationVesselId;
        private uint[] sourceMembers, destinationMembers;
        private Dictionary<string, double> visitorBaseline;
        private Dictionary<string, bool> flowBaseline;
        private HashSet<Guid> vesselBaseline;
        private Dictionary<Guid, string> vesselBaselineDescriptions;
        private double[] sourceBaseline, destinationBaseline;
        private float startedAt;
        private bool started, resumeMode, finalAuditMode;

        private void Start()
        {
            bool accepted = false;
            try
            {
                root = Path.GetFullPath(KSPUtil.ApplicationRootPath).TrimEnd('\\', '/');
                if (!String.Equals(root, FullDevRoot, StringComparison.OrdinalIgnoreCase)) return;
                NoReparse(root);
                string requestPath = Path.Combine(root, RequestName);
                if (!File.Exists(requestPath) || (File.GetAttributes(requestPath) & FileAttributes.ReparsePoint) != 0) return;
                NoReparse(requestPath);
                if (new FileInfo(requestPath).Length > 128) throw new InvalidDataException("Remote delivery request exceeds 128 bytes");
                string request = File.ReadAllText(requestPath).Trim();
                Guid parsed;
                string prefix = request.StartsWith(FinalAuditPrefix, StringComparison.Ordinal) ? FinalAuditPrefix :
                    request.StartsWith(ResumePrefix, StringComparison.Ordinal) ? ResumePrefix : RequestPrefix;
                if (!request.StartsWith(prefix, StringComparison.Ordinal) || request.Length != prefix.Length + 32 ||
                    !Guid.TryParseExact(request.Substring(prefix.Length), "N", out parsed) || parsed == Guid.Empty) return;
                resumeMode = prefix == ResumePrefix;
                finalAuditMode = prefix == FinalAuditPrefix;
                token = parsed.ToString("N");
                folder = "ExpanseProductionCopyDelivery-" + token;
                logPath = Path.Combine(root, folder + ".log");
                string saveRoot = Path.GetFullPath(Path.Combine(root, "saves"));
                string savePath = Path.GetFullPath(Path.Combine(saveRoot, folder));
                NoReparse(saveRoot); NoReparse(savePath); NoReparse(logPath);
                if (!savePath.StartsWith(saveRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    File.Exists(savePath) || Directory.Exists(logPath) || !Directory.Exists(savePath) ||
                    (resumeMode || finalAuditMode ? !File.Exists(logPath) : File.Exists(logPath)))
                    throw new IOException("Derived disposable save/log path is unsafe or already exists");
                string copiedSave = Path.Combine(savePath, "persistent.sfs");
                string copyManifest = Path.Combine(savePath, "copy-manifest.json");
                if (!resumeMode && !finalAuditMode)
                {
                    NoReparse(copiedSave); NoReparse(copyManifest);
                    if (!File.Exists(copiedSave) || !File.Exists(copyManifest) || new FileInfo(copyManifest).Length > 4096)
                        throw new FileNotFoundException("Token-scoped copied save or independent manifest is missing");
                    string manifest = File.ReadAllText(copyManifest);
                    if (ManifestField(manifest, "Token") != token || ManifestField(manifest, "Folder") != folder ||
                        !String.Equals(Path.GetFullPath(ManifestField(manifest, "Target")), copiedSave, StringComparison.OrdinalIgnoreCase) ||
                        !String.Equals(ManifestField(manifest, "Sha256"), Sha256(copiedSave), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Token-scoped copied save hash or manifest identity differs");
                }
                if (!WorldBridgeAddon.ActivateRemotePhysicalFixture(token)) throw new InvalidOperationException("Remote dev Bridge gate rejected token/root");
                File.Delete(requestPath);
                if (resumeMode || finalAuditMode) File.AppendAllText(logPath, (finalAuditMode ? "FINAL_AUDIT_START" : "RESUME_START") +
                    " utc=" + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " token=" + token + Environment.NewLine);
                else File.WriteAllText(logPath, "START utc=" + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " token=" + token + Environment.NewLine);
                AudioListener.volume = 0f;
                DontDestroyOnLoad(gameObject);
                started = accepted = true; startedAt = Time.realtimeSinceStartup;
                StartCoroutine(Supervise(finalAuditMode ? AuditFinal() : resumeMode ? Resume() : RunCopied(copiedSave)));
            }
            catch (Exception ex)
            {
                if (accepted && logPath != null) { Line("FAIL initialize " + Bound(ex.ToString(), 1800)); Application.Quit(); }
            }
        }

        private IEnumerator Supervise(IEnumerator body)
        {
            Stack<IEnumerator> stack = new Stack<IEnumerator>(); stack.Push(body);
            while (Time.realtimeSinceStartup - startedAt < 1200f)
            {
                if (stack.Count == 0) yield break;
                bool more; object current;
                try { more = stack.Peek().MoveNext(); current = more ? stack.Peek().Current : null; if (!more) { stack.Pop(); continue; } }
                catch (Exception ex) { Line("FAIL " + Bound(ex.ToString(), 2400)); Application.Quit(); yield break; }
                IEnumerator nested = current as IEnumerator;
                if (nested == null) yield return current; else stack.Push(nested);
            }
            Line("FAIL global watchdog 1200s"); Application.Quit();
        }

        private sealed class CopiedDepot
        {
            internal string Id, Label;
            internal uint Anchor;
            internal uint[] Members;
        }

        private IEnumerator RunCopied(string copiedSave)
        {
            string originalHash = Sha256(copiedSave);
            CopiedDepot[] depots = ReadCopiedDepots(copiedSave);
            CopiedDepot sourceDepot = depots.Single(x => x.Label == "Minmus Mining");
            CopiedDepot destinationDepot = depots.Single(x => x.Label == "Fuel Depot");
            sourceDepotId = sourceDepot.Id; destinationDepotId = destinationDepot.Id;
            sourceMembers = sourceDepot.Members; destinationMembers = destinationDepot.Members;
            if (sourceDepotId == destinationDepotId || sourceMembers.Intersect(destinationMembers).Any())
                throw new InvalidDataException("Copied source and destination identity overlaps");
            Line("COPIED_ROUTE_INPUT saveSha256=" + originalHash + " sourceDepot=" + sourceDepotId +
                " destinationDepot=" + destinationDepotId + " sourceMembers=" + Join(sourceMembers) +
                " destinationMembers=" + Join(destinationMembers));
            yield return LoadToKsc("persistent", originalHash);
            sourceVesselId = OwnerByAnchor(sourceDepot.Anchor).id;
            destinationVesselId = OwnerByAnchor(destinationDepot.Anchor).id;
            if (sourceVesselId == destinationVesselId) throw new InvalidDataException("Copied depots share a vessel GUID");
            AssertRemoteVessels(true);
            AssertSelectedOwner(sourceVesselId, sourceMembers);
            AssertSelectedOwner(destinationVesselId, destinationMembers);
            DepotRegistryModule registry = DepotRegistryModule.Instance;
            if (registry == null || !registry.IsReady || !registry.RegisteredDepotIds.Contains(sourceDepotId) ||
                !registry.RegisteredDepotIds.Contains(destinationDepotId))
                throw new InvalidDataException("Copied registry did not restore exact selected depots");
            worldId = registry.WorldId;
            AcceptedState state = ReadState();
            if (state == null || state.WorldId != worldId || state.WritesBlocked || state.ActiveShipments.Length != 0)
                throw new InvalidDataException("Copied accepted capsule/world has a fault or preexisting shipment");
            RouteVersionRecord[] routes = state.RouteVersions.Where(x => !x.LegacyOpaque && x.RouteId == "MM Route 1" && x.Version == 1 &&
                x.SourceDepotId == sourceDepotId && x.DestinationDepotId == destinationDepotId &&
                Names.Select((name, i) => new { name, amount = (long)(Cargo[i] * 1000000) })
                    .All(want => x.Resources.Any(r => r.ResourceName == want.name && r.AmountMicroUnits == want.amount))).ToArray();
            if (routes.Length != 1 || routes[0].Resources.Length != Names.Length || routes[0].TravelDurationSeconds != 302400)
                throw new InvalidDataException("Copied accepted state lacks exact existing MM Route 1 v1 manifest and duration");
            RouteVersionRecord route = routes[0];
            if (!state.Depots.Any(x => x.Active && x.DepotId == sourceDepotId && x.MembershipRevision == route.SourceMembershipRevision && x.MembershipHash == route.SourceMembershipHash) ||
                !state.Depots.Any(x => x.Active && x.DepotId == destinationDepotId && x.MembershipRevision == route.DestinationMembershipRevision && x.MembershipHash == route.DestinationMembershipHash))
                throw new InvalidDataException("Saved route membership does not match copied selected depot registry");
            sourceBaseline = SelectedAmounts(Remote(sourceVesselId), sourceMembers);
            destinationBaseline = SelectedAmounts(Remote(destinationVesselId), destinationMembers);
            for (int i = 0; i < Names.Length; i++)
                if (sourceBaseline[i] < Cargo[i] || destinationBaseline[i] + Cargo[i] > SelectedCapacity(Remote(destinationVesselId), destinationMembers, Names[i]))
                    throw new InvalidOperationException("Copied selected stock/capacity cannot cover exact manifest for " + Names[i]);
            visitorBaseline = VisitorAmounts();
            vesselBaseline = VesselIds(); vesselBaselineDescriptions = VesselDescriptions();
            flowBaseline = SelectedFlowStates();
            if (!flowBaseline.Any(x => x.Key.StartsWith(destinationVesselId.ToString("N") + "/", StringComparison.Ordinal) &&
                x.Key.EndsWith("/Oxidizer", StringComparison.Ordinal) && !x.Value))
                throw new InvalidOperationException("Copied destination lacks the expected selected flow-locked Oxidizer row");
            Line("REMOTE_BASELINE source=" + Numbers(sourceBaseline) + " destination=" + Numbers(destinationBaseline) +
                " visitorRows=" + visitorBaseline.Count + " selectedFlow=" + FlowSummary(flowBaseline) +
                " vesselCount=" + vesselBaseline.Count + " saveSha256=" + Sha256(copiedSave));
            if (!String.Equals(Sha256(copiedSave), originalHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Copied persistent changed before exact-route dispatch");
            WorldBridgeAddon bridge = FindObjectOfType<WorldBridgeAddon>();
            if (bridge == null || !bridge.CanAcceptPhysicalEffects)
                throw new InvalidOperationException("Copied selected Bridge does not accept physical effects");
            Line("READY token=" + token + " worldId=" + worldId + " runId=" + bridge.CurrentRunId +
                " sessionId=" + bridge.CurrentSessionId + " loadEpoch=" + bridge.CurrentLoadEpoch +
                " routeId=MM Route 1 routeVersion=1 sourceDepotId=" + sourceDepotId +
                " destinationDepotId=" + destinationDepotId + " sourceVesselId=" + sourceVesselId.ToString("D") +
                " destinationVesselId=" + destinationVesselId.ToString("D") + " sourceMembers=" + Join(sourceMembers) +
                " destinationMembers=" + Join(destinationMembers) + " routeDurationSeconds=302400" +
                " manifest=LF1000,MP500,OX1400 acceptedSequence=" + state.AcceptedSequence +
                " capsuleHash=" + AcceptedStateCodec.ComputeHash(state));
            float dispatchUntil = Time.realtimeSinceStartup + 180f;
            ActiveShipmentRecord shipment = null;
            while (Time.realtimeSinceStartup < dispatchUntil)
            {
                state = ReadState();
                if (state != null && state.WritesBlocked) throw new InvalidOperationException("Physical fault during copied-route dispatch");
                if (state != null) shipment = state.ActiveShipments.SingleOrDefault(x => x.RouteId == "MM Route 1" && x.RouteVersion == 1);
                if (shipment != null && state.ActiveShipments.Length == 1 &&
                    state.Receipts.Any(x => x.OperationKind == "dispatch" && x.Outcome == "accepted")) break;
                yield return new WaitForSecondsRealtime(0.25f);
            }
            if (shipment == null || state.ActiveShipments.Length != 1) throw new TimeoutException("Host did not dispatch one exact copied-route shipment");
            AssertRemoteVessels(true);
            double[] sourceAfter = SelectedAmounts(Remote(sourceVesselId), sourceMembers);
            double[] destinationAfter = SelectedAmounts(Remote(destinationVesselId), destinationMembers);
            for (int i = 0; i < Names.Length; i++)
                if (shipment.RemainingResources.Single(x => x.ResourceName == Names[i]).AmountMicroUnits != (long)(Cargo[i] * 1000000))
                    throw new InvalidOperationException("Copied-route shipment cargo differs for " + Names[i]);
            AssertPhysicalWitness(state, "dispatch", true);
            LogVisitorEvolution("dispatch"); AssertFlowStates(); AssertVesselIds();
            if (shipment.DueUt <= shipment.DepartureUt || Planetarium.GetUniversalTime() >= shipment.DueUt)
                throw new InvalidOperationException("No in-transit interval remains for copied-route shipment");
            string shipmentId = shipment.ShipmentId;
            long dispatchSequence = state.AcceptedSequence;
            Line("DISPATCHED shipmentId=" + shipmentId + " departureUt=" + F(shipment.DepartureUt) +
                " dueUt=" + F(shipment.DueUt) + " source=" + Numbers(sourceAfter) +
                " destination=" + Numbers(destinationAfter) + " cargo=" + Manifest(shipment.RemainingResources) +
                " sequence=" + dispatchSequence + " capsuleHash=" + AcceptedStateCodec.ComputeHash(state) +
                " lockedDestinationFlowPreserved=true");
            Save(TransitSave);
            AssertFlowStates(); AssertSavedFlowStates(SavePath(TransitSave), sourceAfter, destinationAfter);
            if (!String.Equals(Sha256(copiedSave), originalHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Original token-scoped copied persistent changed during dispatch save cut");
            WriteResumeSnapshot(shipmentId, dispatchSequence, state, sourceAfter, destinationAfter);
            Line("TRANSIT_SAVE_CUT_READY restart full dev clone with " + ResumePrefix + token +
                " savedHash=" + Sha256(SavePath(TransitSave)) + " originalCopyHash=" + originalHash);
            Application.Quit();
        }

        private string SavePath(string name) => Path.GetFullPath(Path.Combine(root, "saves", folder, name + ".sfs"));
        private string ResumePath => Path.GetFullPath(Path.Combine(root, folder + ".resume"));

        private static string ManifestField(string json, string name)
        {
            Match match = Regex.Match(json, "\\\"" + name + "\\\"\\s*:\\s*\\\"([^\\\"]{1,512})\\\"");
            if (!match.Success) throw new InvalidDataException("Copy manifest lacks " + name);
            return match.Groups[1].Value.Replace("\\\\", "\\");
        }

        private static CopiedDepot[] ReadCopiedDepots(string save)
        {
            ConfigNode wrapper = ConfigNode.Load(save);
            ConfigNode game = wrapper == null ? null : wrapper.name == "GAME" ? wrapper :
                wrapper.GetNodes("GAME").Length == 1 ? wrapper.GetNodes("GAME")[0] : null;
            if (game == null) throw new InvalidDataException("Copied SFS lacks unique GAME node");
            ConfigNode[] scenarios = game.GetNodes("SCENARIO").Where(x => x.GetValue("name") == "DepotRegistryModule").ToArray();
            if (scenarios.Length != 1 || scenarios[0].GetNodes("DEPOT_REGISTRY").Length != 1)
                throw new InvalidDataException("Copied SFS lacks unique depot registry");
            ConfigNode[] registrations = scenarios[0].GetNodes("DEPOT_REGISTRY")[0].GetNodes("DEPOT");
            if (registrations.Length < 2 || registrations.Length > 64) throw new InvalidDataException("Copied depot count exceeds bound");
            return registrations.Select(node =>
            {
                uint anchor;
                if (!UInt32.TryParse(node.GetValue("anchorPartId"), NumberStyles.None, CultureInfo.InvariantCulture, out anchor) || anchor == 0)
                    throw new InvalidDataException("Copied depot anchor is invalid");
                uint[] members = node.GetNodes("member").Select(x => UInt32.Parse(x.GetValue("id"), CultureInfo.InvariantCulture)).ToArray();
                if (members.Length == 0 || members.Length > 64 || members.Any(x => x == 0) ||
                    members.Distinct().Count() != members.Length || !members.Contains(anchor))
                    throw new InvalidDataException("Copied selected member set is invalid");
                return new CopiedDepot { Id = node.GetValue("depotId"), Label = node.GetValue("label"),
                    Anchor = anchor, Members = members };
            }).ToArray();
        }

        private IEnumerator LoadToKsc(string saveName, string expectedSaveHash)
        {
            string selectedSave = SavePath(saveName);
            if (!File.Exists(selectedSave) || !String.Equals(Sha256(selectedSave), expectedSaveHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Selected copied save hash changed before load");
            HighLogic.SaveFolder = folder;
            Game loaded = GamePersistence.LoadGame(saveName, folder, true, false);
            if (loaded == null || loaded.flightState == null || loaded.flightState.protoVessels == null ||
                loaded.flightState.protoVessels.Count == 0 || loaded.flightState.protoVessels.Count > 512)
                throw new InvalidDataException("Selected copied game has no bounded FlightState");
            int activeIndex = Convert.ToInt32(Read(loaded.flightState, "activeVesselIdx"), CultureInfo.InvariantCulture);
            if (activeIndex < 0 || activeIndex >= loaded.flightState.protoVessels.Count ||
                loaded.flightState.protoVessels[activeIndex] == null)
                throw new InvalidDataException("Selected copied FlightState active index is invalid");
            Guid activeId = loaded.flightState.protoVessels[activeIndex].vesselID;
            HighLogic.CurrentGame = loaded; loaded.startScene = GameScenes.FLIGHT; loaded.Start();
            yield return WaitForScene(GameScenes.FLIGHT, 360f);
            float flightEnd = Time.realtimeSinceStartup + 360f;
            while (Time.realtimeSinceStartup < flightEnd)
            {
                if (HighLogic.SaveFolder == folder && FlightGlobals.ActiveVessel != null &&
                    FlightGlobals.ActiveVessel.id == activeId && FlightGlobals.ActiveVessel.loaded &&
                    FlightGlobals.Vessels != null && FlightGlobals.Vessels.Count > 0) break;
                yield return new WaitForSecondsRealtime(0.25f);
            }
            if (FlightGlobals.ActiveVessel == null || FlightGlobals.ActiveVessel.id != activeId ||
                FlightGlobals.Vessels == null || FlightGlobals.Vessels.Count == 0)
                throw new TimeoutException("Selected copied Flight did not reconstruct exact active vessel");
            Line("FLIGHT_RECONSTRUCTED save=" + saveName + " active=" + activeId.ToString("D") +
                " vesselCount=" + FlightGlobals.Vessels.Count + " saveSha256=" + Sha256(selectedSave));
            if (!String.Equals(Sha256(selectedSave), expectedSaveHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Selected copied save changed during Flight load");
            HighLogic.LoadScene(GameScenes.SPACECENTER);
            yield return WaitForScene(GameScenes.SPACECENTER, 360f);
            yield return WaitForReady(GameScenes.SPACECENTER, 180f);
            if (FlightGlobals.Vessels == null || FlightGlobals.Vessels.Count == 0 ||
                !String.Equals(Sha256(selectedSave), expectedSaveHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Selected copied save/vessels changed during Space Center transition");
            Line("KSC_RECONSTRUCTED save=" + saveName + " vesselCount=" + FlightGlobals.Vessels.Count +
                " saveSha256=" + Sha256(selectedSave));
        }

        private static Vessel OwnerByAnchor(uint anchor)
        {
            if (FlightGlobals.Vessels == null || FlightGlobals.Vessels.Count > 512)
                throw new InvalidOperationException("Copied vessel set exceeds bound");
            Vessel[] matches = FlightGlobals.Vessels.Where(v => v != null && v.protoVessel != null &&
                v.protoVessel.protoPartSnapshots.Any(p => p != null && p.persistentId == anchor)).ToArray();
            if (matches.Length != 1 || matches[0].loaded)
                throw new InvalidOperationException("Copied selected owner is missing, ambiguous, or loaded for anchor " + anchor);
            return matches[0];
        }

        private static void AssertSelectedOwner(Guid vesselId, uint[] members)
        {
            foreach (uint id in members)
            {
                Vessel[] owners = FlightGlobals.Vessels.Where(v => v != null && v.protoVessel != null &&
                    v.protoVessel.protoPartSnapshots.Any(p => p != null && p.persistentId == id)).ToArray();
                if (owners.Length != 1 || owners[0].id != vesselId || owners[0].loaded)
                    throw new InvalidOperationException("Copied selected member has absent/duplicate/loaded owner: " + id);
            }
        }

        private void WriteResumeSnapshot(string shipmentId, long sequence, AcceptedState state, double[] sourceAfter, double[] destinationAfter)
        {
            string path = ResumePath;
            NoReparse(path);
            if (File.Exists(path) || Directory.Exists(path)) throw new IOException("Disposable resume snapshot already exists");
            List<string> lines = new List<string> {
                "token=" + token, "world=" + worldId, "sourceDepot=" + sourceDepotId, "destinationDepot=" + destinationDepotId,
                "sourceVessel=" + sourceVesselId.ToString("D"), "destinationVessel=" + destinationVesselId.ToString("D"),
                "sourceMembers=" + Join(sourceMembers), "destinationMembers=" + Join(destinationMembers),
                "sourceBaseline=" + Numbers(sourceBaseline), "destinationBaseline=" + Numbers(destinationBaseline),
                "sourceAfter=" + Numbers(sourceAfter), "destinationAfter=" + Numbers(destinationAfter),
                "shipment=" + shipmentId, "sequence=" + sequence.ToString(CultureInfo.InvariantCulture),
                "capsuleHash=" + AcceptedStateCodec.ComputeHash(state), "transitHash=" + Sha256(SavePath(TransitSave)),
                "visitorCount=" + visitorBaseline.Count.ToString(CultureInfo.InvariantCulture),
                "flowCount=" + flowBaseline.Count.ToString(CultureInfo.InvariantCulture)
            };
            foreach (var row in visitorBaseline.OrderBy(x => x.Key, StringComparer.Ordinal)) lines.Add("visitor=" + row.Key + "=" + F(row.Value));
            foreach (var row in flowBaseline.OrderBy(x => x.Key, StringComparer.Ordinal)) lines.Add("flow=" + row.Key + "=" + (row.Value ? "1" : "0"));
            File.WriteAllLines(path, lines.ToArray());
            Line("RESUME_SNAPSHOT path=" + path + " sha256=" + Sha256(path) + " visitorRows=" + visitorBaseline.Count);
        }

        private sealed class ResumeValues
        {
            internal readonly Dictionary<string, string> Fields = new Dictionary<string, string>(StringComparer.Ordinal);
            internal readonly Dictionary<string, double> Visitors = new Dictionary<string, double>(StringComparer.Ordinal);
            internal readonly Dictionary<string, double> ArrivalVisitors = new Dictionary<string, double>(StringComparer.Ordinal);
            internal readonly Dictionary<string, bool> Flows = new Dictionary<string, bool>(StringComparer.Ordinal);
            internal string Get(string key) { string value; if (!Fields.TryGetValue(key, out value)) throw new InvalidDataException("Resume snapshot missing " + key); return value; }
        }
        private ResumeValues ReadResumeSnapshot()
        {
            string path = ResumePath;
            NoReparse(path);
            if (!File.Exists(path) || new FileInfo(path).Length > 65536) throw new InvalidDataException("Token-scoped resume snapshot missing or oversized");
            ResumeValues result = new ResumeValues();
            foreach (string line in File.ReadAllLines(path))
            {
                int separator = line.IndexOf('=');
                if (separator <= 0) throw new InvalidDataException("Malformed resume snapshot row");
                string key = line.Substring(0, separator), value = line.Substring(separator + 1);
                if (key == "visitor" || key == "arrivalVisitor")
                {
                    int valueSeparator = value.LastIndexOf('='); double amount;
                    Dictionary<string, double> visitorSet = key == "visitor" ? result.Visitors : result.ArrivalVisitors;
                    if (valueSeparator <= 0 || !Double.TryParse(value.Substring(valueSeparator + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out amount) ||
                        Double.IsNaN(amount) || Double.IsInfinity(amount) || visitorSet.ContainsKey(value.Substring(0, valueSeparator)))
                        throw new InvalidDataException("Malformed or duplicate visitor resume row");
                    visitorSet.Add(value.Substring(0, valueSeparator), amount);
                }
                else if (key == "flow")
                {
                    int valueSeparator = value.LastIndexOf('=');
                    if (valueSeparator <= 0 || (value.Substring(valueSeparator + 1) != "0" && value.Substring(valueSeparator + 1) != "1") ||
                        result.Flows.ContainsKey(value.Substring(0, valueSeparator)))
                        throw new InvalidDataException("Malformed or duplicate selected flow resume row");
                    result.Flows.Add(value.Substring(0, valueSeparator), value.Substring(valueSeparator + 1) == "1");
                }
                else { if (result.Fields.ContainsKey(key)) throw new InvalidDataException("Duplicate resume snapshot field"); result.Fields.Add(key, value); }
            }
            int visitorCount, flowCount;
            if (result.Get("token") != token || !Int32.TryParse(result.Get("visitorCount"), NumberStyles.None, CultureInfo.InvariantCulture, out visitorCount) ||
                visitorCount < 0 || visitorCount > 4096 || result.Visitors.Count != visitorCount ||
                !Int32.TryParse(result.Get("flowCount"), NumberStyles.None, CultureInfo.InvariantCulture, out flowCount) ||
                flowCount < 1 || flowCount > 512 || result.Flows.Count != flowCount)
                throw new InvalidDataException("Resume snapshot token, visitor, or selected flow count mismatch");
            if (result.Fields.ContainsKey("arrivalVisitorCount") &&
                (!Int32.TryParse(result.Get("arrivalVisitorCount"), NumberStyles.None, CultureInfo.InvariantCulture, out visitorCount) ||
                 visitorCount < 0 || visitorCount > 4096 || result.ArrivalVisitors.Count != visitorCount))
                throw new InvalidDataException("Arrival-local visitor snapshot count mismatch");
            return result;
        }

        private IEnumerator Resume()
        {
            ResumeValues saved = ReadResumeSnapshot();
            string selectedSave = SavePath(TransitSave);
            NoReparse(selectedSave);
            if (!File.Exists(selectedSave) || !String.Equals(Sha256(selectedSave), saved.Get("transitHash"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Selected transit save hash differs from dispatch save cut");
            string persistentSave = SavePath("persistent");
            string persistentBackup = Path.GetFullPath(Path.Combine(root, "saves", folder, "persistent.pretransit.sfs"));
            NoReparse(persistentSave); NoReparse(persistentBackup);
            if (!File.Exists(persistentSave) || Directory.Exists(persistentBackup))
                throw new InvalidDataException("Disposable persistent save is missing or its pretransit backup path is invalid");
            string previousPersistentHash = Sha256(persistentSave);
            if (!File.Exists(persistentBackup))
            {
                if (String.Equals(previousPersistentHash, saved.Get("transitHash"), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Persistent already contains transit without an original backup");
                File.Copy(persistentSave, persistentBackup, false);
                if (!String.Equals(Sha256(persistentBackup), previousPersistentHash, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Disposable pretransit backup hash differs from persistent save");
                File.Copy(selectedSave, persistentSave, true);
            }
            else if (!String.Equals(previousPersistentHash, saved.Get("transitHash"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Persistent/backup state differs from prior token-scoped staging");
            if (!String.Equals(Sha256(persistentSave), saved.Get("transitHash"), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Disposable persistent save did not receive the selected transit bytes");
            Line("PERSISTENT_STAGED previousHash=" + previousPersistentHash + " backupPath=" + persistentBackup +
                " backupHash=" + Sha256(persistentBackup) + " transitPath=" + selectedSave +
                " transitHash=" + Sha256(selectedSave) + " persistentHash=" + Sha256(persistentSave));
            worldId = saved.Get("world"); sourceDepotId = saved.Get("sourceDepot"); destinationDepotId = saved.Get("destinationDepot");
            sourceVesselId = Guid.Parse(saved.Get("sourceVessel")); destinationVesselId = Guid.Parse(saved.Get("destinationVessel"));
            if (sourceVesselId == destinationVesselId) throw new InvalidDataException("Resume selected vessel IDs coincide");
            sourceMembers = ParseMembers(saved.Get("sourceMembers")); destinationMembers = ParseMembers(saved.Get("destinationMembers"));
            if (sourceMembers.Intersect(destinationMembers).Any()) throw new InvalidDataException("Resume selected members overlap");
            sourceBaseline = ParseAmounts(saved.Get("sourceBaseline")); destinationBaseline = ParseAmounts(saved.Get("destinationBaseline"));
            double[] sourceAfter = ParseAmounts(saved.Get("sourceAfter")), destinationAfter = ParseAmounts(saved.Get("destinationAfter"));
            visitorBaseline = saved.Visitors;
            flowBaseline = saved.Flows;
            long dispatchSequence = Int64.Parse(saved.Get("sequence"), CultureInfo.InvariantCulture);
            string shipmentId = saved.Get("shipment");
            Line("RESUME_HASH_VERIFIED transitSha256=" + Sha256(selectedSave) + " snapshotSha256=" + Sha256(ResumePath));
            yield return new WaitForSecondsRealtime(2f);
            AssertSavedFlowStates(selectedSave, sourceAfter, destinationAfter);
            yield return LoadToKsc("persistent", saved.Get("transitHash"));
            AcceptedState state = ReadState();
            RecoveryCapsuleModule recoveredModule = RecoveryCapsuleModule.Instance;
            Line("FRESH_PROCESS_CAPSULE scene=" + HighLogic.LoadedScene + " saveFolder=" + HighLogic.SaveFolder +
                " currentGame=" + (HighLogic.CurrentGame != null) +
                " currentStartScene=" + (HighLogic.CurrentGame == null ? "missing" : HighLogic.CurrentGame.startScene.ToString()) +
                " transitHashAfterLoad=" + Sha256(selectedSave) + " persistentHashAfterLoad=" + Sha256(persistentSave) +
                " modulePresent=" + (recoveredModule != null) +
                " moduleLoaded=" + (recoveredModule != null && recoveredModule.IsLoaded) +
                " moduleCorrupt=" + (recoveredModule != null && recoveredModule.IsCorrupt) +
                " moduleHold=" + (recoveredModule == null ? "missing" : recoveredModule.HoldReason) +
                " statePresent=" + (state != null) +
                " world=" + (state == null ? "missing" : state.WorldId) + " expectedWorld=" + worldId +
                " sequence=" + (state == null ? -1 : state.AcceptedSequence) + " expectedSequence=" + dispatchSequence +
                " hash=" + (state == null ? "missing" : AcceptedStateCodec.ComputeHash(state)) + " expectedHash=" + saved.Get("capsuleHash") +
                " activeShipments=" + (state == null ? -1 : state.ActiveShipments.Count()) +
                " writesBlocked=" + (state != null && state.WritesBlocked));
            if (state == null || state.WorldId != worldId || state.AcceptedSequence != dispatchSequence ||
                !String.Equals(AcceptedStateCodec.ComputeHash(state), saved.Get("capsuleHash"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Fresh-process reload did not restore exact in-transit capsule");
            ActiveShipmentRecord shipment = state.ActiveShipments.SingleOrDefault(x => x.ShipmentId == shipmentId);
            if (shipment == null || shipment.LegacyOpaque) throw new InvalidOperationException("Fresh-process reload lost exact in-transit shipment");
            AssertRemoteVessels(true);
            AssertUnchanged(sourceAfter, SelectedAmounts(Remote(sourceVesselId), sourceMembers), "source fresh-process reload");
            AssertUnchanged(destinationAfter, SelectedAmounts(Remote(destinationVesselId), destinationMembers), "destination fresh-process reload");
            LogVisitorEvolution("transitReload"); AssertFlowStates();
            vesselBaseline = VesselIds(); vesselBaselineDescriptions = VesselDescriptions(); AssertVesselIds();
            Line("FRESH_PROCESS_TRANSIT_PASS save=" + TransitSave + " shipmentId=" + shipmentId + " source=" + Numbers(sourceAfter) +
                " destination=" + Numbers(destinationAfter) + " visitorRows=" + visitorBaseline.Count + " sequence=" + dispatchSequence +
                " capsuleHash=" + AcceptedStateCodec.ComputeHash(state) + " runId=" + FindObjectOfType<WorldBridgeAddon>().CurrentRunId);
            Line("HOST_RESTART_WINDOW_READY shipmentId=" + shipmentId + " runId=" + FindObjectOfType<WorldBridgeAddon>().CurrentRunId +
                " sequence=" + state.AcceptedSequence + " capsuleHash=" + AcceptedStateCodec.ComputeHash(state));
            yield return new WaitForSecondsRealtime(25f);
            state = ReadState();
            if (state == null || state.AcceptedSequence != dispatchSequence || state.ActiveShipments.SingleOrDefault(x => x.ShipmentId == shipmentId) == null)
                throw new InvalidOperationException("Transit changed during bounded Host restart window");
            if (Planetarium.GetUniversalTime() >= shipment.DueUt) throw new InvalidOperationException("Due time passed before arrival observation window");
            TimeWarp.SetRate(5, true);
            Line("ARRIVAL_WARP_START rateIndex=" + TimeWarp.CurrentRateIndex + " ut=" + F(Planetarium.GetUniversalTime()) +
                " dueUt=" + F(shipment.DueUt) + " remainingUt=" + F(shipment.DueUt - Planetarium.GetUniversalTime()));
            float arrivalUntil = Time.realtimeSinceStartup + 900f;
            AcceptedReceipt arrival = null;
            bool arrivalLocalVisitors = false;
            while (Time.realtimeSinceStartup < arrivalUntil)
            {
                double remaining = shipment.DueUt - Planetarium.GetUniversalTime();
                if (remaining <= 5000 && TimeWarp.CurrentRateIndex > 3)
                {
                    TimeWarp.SetRate(3, true);
                    Line("ARRIVAL_WARP_SLOW rateIndex=" + TimeWarp.CurrentRateIndex + " remainingUt=" + F(remaining));
                }
                if (remaining <= 60 && TimeWarp.CurrentRateIndex > 0)
                {
                    TimeWarp.SetRate(0, true);
                    Line("ARRIVAL_WARP_1X rateIndex=" + TimeWarp.CurrentRateIndex + " remainingUt=" + F(remaining));
                }
                if (remaining <= 2 && !arrivalLocalVisitors)
                {
                    visitorBaseline = VisitorAmounts();
                    arrivalLocalVisitors = true;
                    Line("ARRIVAL_LOCAL_VISITOR_BASELINE rows=" + visitorBaseline.Count + " rateIndex=" +
                        TimeWarp.CurrentRateIndex + " remainingUt=" + F(remaining));
                }
                state = ReadState();
                if (state != null && state.WritesBlocked) throw new InvalidOperationException("Physical fault during arrival");
                if (state != null) arrival = state.Receipts.LastOrDefault(x => x.OperationKind == "arrival" && x.Outcome == "accepted" && x.AppliedUt >= shipment.DueUt);
                if (arrival != null) break;
                yield return new WaitForSecondsRealtime(0.25f);
            }
            TimeWarp.SetRate(0, true);
            if (arrival == null || !arrivalLocalVisitors) throw new TimeoutException("Host did not settle due remote arrival after local visitor baseline");
            if (state.ActiveShipments.Any(x => x.ShipmentId == shipmentId)) throw new InvalidOperationException("Cargo remains after full-capacity destination credit");
            AssertRemoteVessels(true);
            double[] sourceFinal = SelectedAmounts(Remote(sourceVesselId), sourceMembers);
            double[] destinationFinal = SelectedAmounts(Remote(destinationVesselId), destinationMembers);
            AssertPhysicalWitness(state, "arrival", false);
            LogVisitorEvolution("arrival"); AssertFlowStates();
            Line("ARRIVED shipmentId=" + shipmentId + " dueUt=" + F(shipment.DueUt) + " appliedUt=" + F(arrival.AppliedUt) +
                " source=" + Numbers(sourceFinal) + " destination=" + Numbers(destinationFinal) +
                " sequence=" + state.AcceptedSequence + " capsuleHash=" + AcceptedStateCodec.ComputeHash(state));
            AssertVesselIds();
            Save(FinalSave);
            AssertSavedFlowStates(SavePath(FinalSave), sourceFinal, destinationFinal);
            File.AppendAllLines(ResumePath, new[] {
                "finalHash=" + Sha256(SavePath(FinalSave)),
                "finalSequence=" + state.AcceptedSequence.ToString(CultureInfo.InvariantCulture),
                "finalCapsuleHash=" + AcceptedStateCodec.ComputeHash(state),
                "finalSource=" + Numbers(sourceFinal), "finalDestination=" + Numbers(destinationFinal),
                "arrivalVisitorCount=" + visitorBaseline.Count.ToString(CultureInfo.InvariantCulture)
            }.Concat(visitorBaseline.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x =>
                "arrivalVisitor=" + x.Key + "=" + F(x.Value))).ToArray());
            Line("FINAL_AUDIT_READY request=" + FinalAuditPrefix + token + " finalSha256=" + Sha256(SavePath(FinalSave)) +
                " resumeSnapshotSha256=" + Sha256(ResumePath));
            Line("PASS exact three-resource remote sendOnce/arrival survived fresh-process transit reload; final save hash is logged for a separate reload check");
            Application.Quit();
        }

        private IEnumerator AuditFinal()
        {
            ResumeValues saved = ReadResumeSnapshot();
            BackfillFinalAuditFields(saved);
            string finalSave = SavePath(FinalSave), persistentSave = SavePath("persistent");
            string persistentBackup = Path.GetFullPath(Path.Combine(root, "saves", folder, "persistent.prearrival.sfs"));
            NoReparse(finalSave); NoReparse(persistentSave); NoReparse(persistentBackup);
            if (!File.Exists(finalSave) || !String.Equals(Sha256(finalSave), saved.Get("finalHash"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Selected final save differs from arrival save cut");
            byte[] stagedBytes = SafeFinalBytes(finalSave);
            string stagedHash = BytesHash(stagedBytes);
            if (!File.Exists(persistentSave) || Directory.Exists(persistentBackup))
                throw new InvalidDataException("Token-scoped persistent or prearrival backup path is invalid");
            string oldHash = Sha256(persistentSave);
            if (!File.Exists(persistentBackup))
            {
                if (String.Equals(oldHash, stagedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Persistent already contains final bytes without prearrival backup");
                File.Copy(persistentSave, persistentBackup, false);
                if (!String.Equals(Sha256(persistentBackup), oldHash, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Prearrival persistent backup hash mismatch");
                File.WriteAllBytes(persistentSave, stagedBytes);
            }
            else if (!String.Equals(oldHash, stagedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Persistent/backup state differs from prior final staging");
            if (!String.Equals(Sha256(persistentSave), stagedHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Staged persistent does not match ConfigNode-safe final save");
            Line("FINAL_PERSISTENT_STAGED originalHash=" + oldHash + " backupPath=" + persistentBackup +
                " backupHash=" + Sha256(persistentBackup) + " finalPath=" + finalSave +
                " finalHash=" + Sha256(finalSave) + " configNodeSafeHash=" + stagedHash + " persistentHash=" + Sha256(persistentSave));

            worldId = saved.Get("world"); sourceDepotId = saved.Get("sourceDepot"); destinationDepotId = saved.Get("destinationDepot");
            sourceVesselId = Guid.Parse(saved.Get("sourceVessel")); destinationVesselId = Guid.Parse(saved.Get("destinationVessel"));
            if (sourceVesselId == destinationVesselId) throw new InvalidDataException("Final selected vessel IDs coincide");
            sourceMembers = ParseMembers(saved.Get("sourceMembers")); destinationMembers = ParseMembers(saved.Get("destinationMembers"));
            if (sourceMembers.Intersect(destinationMembers).Any()) throw new InvalidDataException("Final selected members overlap");
            visitorBaseline = saved.ArrivalVisitors;
            if (visitorBaseline.Count != Int32.Parse(saved.Get("arrivalVisitorCount"), CultureInfo.InvariantCulture))
                throw new InvalidDataException("Final arrival-local visitor snapshot is missing or incomplete");
            flowBaseline = saved.Flows;
            double[] sourceFinal = ParseAmounts(saved.Get("finalSource")), destinationFinal = ParseAmounts(saved.Get("finalDestination"));
            long finalSequence = Int64.Parse(saved.Get("finalSequence"), CultureInfo.InvariantCulture);
            string shipmentId = saved.Get("shipment");
            yield return new WaitForSecondsRealtime(2f);
            AssertSavedFlowStates(finalSave, sourceFinal, destinationFinal);
            yield return LoadToKsc("persistent", stagedHash);
            AcceptedState state = ReadState();
            Line("FINAL_FRESH_PROCESS_CAPSULE scene=" + HighLogic.LoadedScene + " folder=" + HighLogic.SaveFolder +
                " currentGame=" + (HighLogic.CurrentGame != null) + " moduleLoaded=" + (RecoveryCapsuleModule.Instance != null && RecoveryCapsuleModule.Instance.IsLoaded) +
                " moduleCorrupt=" + (RecoveryCapsuleModule.Instance != null && RecoveryCapsuleModule.Instance.IsCorrupt) +
                " statePresent=" + (state != null) + " world=" + (state == null ? "missing" : state.WorldId) +
                " sequence=" + (state == null ? -1 : state.AcceptedSequence) +
                " hash=" + (state == null ? "missing" : AcceptedStateCodec.ComputeHash(state)) +
                " finalHashAfterLoad=" + Sha256(finalSave) + " configNodeSafeHash=" + stagedHash +
                " persistentHashAfterLoad=" + Sha256(persistentSave));
            if (state == null || state.WorldId != worldId || state.AcceptedSequence != finalSequence ||
                !String.Equals(AcceptedStateCodec.ComputeHash(state), saved.Get("finalCapsuleHash"), StringComparison.OrdinalIgnoreCase) ||
                state.ActiveShipments.Any(x => x.ShipmentId == shipmentId) || state.WritesBlocked ||
                !String.Equals(Sha256(persistentSave), stagedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Fresh-process final capsule did not match completed shipment");
            AssertRemoteVessels(true);
            AssertUnchanged(sourceFinal, SelectedAmounts(Remote(sourceVesselId), sourceMembers), "source final fresh-process reload");
            AssertUnchanged(destinationFinal, SelectedAmounts(Remote(destinationVesselId), destinationMembers), "destination final fresh-process reload");
            LogVisitorEvolution("finalReload"); AssertFlowStates();
            vesselBaseline = VesselIds(); vesselBaselineDescriptions = VesselDescriptions(); AssertVesselIds();
            Line("FINAL_FRESH_PROCESS_PASS shipmentId=" + shipmentId + " source=" + Numbers(sourceFinal) +
                " destination=" + Numbers(destinationFinal) + " visitorRows=" + visitorBaseline.Count +
                " sequence=" + state.AcceptedSequence + " capsuleHash=" + AcceptedStateCodec.ComputeHash(state));
            Application.Quit();
        }

        private void BackfillFinalAuditFields(ResumeValues saved)
        {
            string[] keys = { "finalHash", "finalSequence", "finalCapsuleHash", "finalSource", "finalDestination" };
            int present = keys.Count(saved.Fields.ContainsKey);
            if (present == keys.Length) return;
            if (present != 0) throw new InvalidDataException("Partial final audit fields cannot be backfilled");
            string[] lines = File.ReadAllLines(logPath);
            string[] arrivals = lines.Where(x => x.Contains(" ARRIVED shipmentId=" + saved.Get("shipment") + " ")).ToArray();
            string[] saves = lines.Where(x => x.Contains(" SAVED name=" + FinalSave + " ")).ToArray();
            if (arrivals.Length != 1 || saves.Length != 1) throw new InvalidDataException("Exact arrival/final-save log pair is unavailable");
            string finalSave = SavePath(FinalSave);
            NoReparse(finalSave);
            string hash = LogField(saves[0], "sha256"), sequenceText = LogField(saves[0], "acceptedSequence"),
                capsuleHash = LogField(saves[0], "capsuleHash"), source = LogField(arrivals[0], "source"),
                destination = LogField(arrivals[0], "destination");
            if (!File.Exists(finalSave) || !String.Equals(Sha256(finalSave), hash, StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(LogField(arrivals[0], "capsuleHash"), capsuleHash, StringComparison.OrdinalIgnoreCase) ||
                LogField(arrivals[0], "sequence") != sequenceText)
                throw new InvalidDataException("Final log and selected save disagree");
            ConfigNode wrapper = ConfigNode.Load(finalSave);
            ConfigNode game = wrapper == null ? null : wrapper.name == "GAME" ? wrapper :
                wrapper.GetNodes("GAME").Length == 1 ? wrapper.GetNodes("GAME")[0] : null;
            ConfigNode[] modules = game == null ? new ConfigNode[0] : game.GetNodes("SCENARIO").Where(x => x.GetValue("name") == "RecoveryCapsuleModule").ToArray();
            if (modules.Length != 1) throw new InvalidDataException("Final save lacks one recovery scenario module");
            ConfigNode[] capsules = modules[0].GetNodes("EXPANSE_RECOVERY");
            if (capsules.Length != 1 || !String.Equals(capsules[0].GetValue("stateSha256"), capsuleHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Final save capsule hash differs from log");
            string parsedBase64 = capsules[0].GetValue("stateBytesBase64");
            string rawBase64 = RawSfsValue(finalSave, "stateBytesBase64");
            Line("FINAL_CAPSULE_PARSE rawChars=" + rawBase64.Length + " parsedChars=" + (parsedBase64 == null ? -1 : parsedBase64.Length) +
                " firstCommentAt=" + rawBase64.IndexOf("//", StringComparison.Ordinal) +
                " rawDecodedSha256=" + Base64Hash(rawBase64) + " parsedDecodedSha256=" + Base64Hash(parsedBase64));
            if (!String.Equals(Base64Hash(rawBase64), capsuleHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Raw final save Base64 differs from logged capsule hash");
            RecoveryCapsule capsule = new RecoveryCapsule {
                SchemaVersion = Int32.Parse(capsules[0].GetValue("schemaVersion"), CultureInfo.InvariantCulture),
                WorldId = capsules[0].GetValue("worldId"), StateBytesBase64 = rawBase64,
                StateSha256 = capsules[0].GetValue("stateSha256")
            };
            AcceptedState finalState = AcceptedStateCodec.ReadCapsule(capsule);
            if (finalState.WorldId != saved.Get("world") ||
                finalState.AcceptedSequence != Int64.Parse(sequenceText, CultureInfo.InvariantCulture) ||
                finalState.ActiveShipments.Any(x => x.ShipmentId == saved.Get("shipment")) ||
                !String.Equals(AcceptedStateCodec.ComputeHash(finalState), capsuleHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Final saved capsule did not complete the selected shipment");
            ParseAmounts(source); ParseAmounts(destination);
            string[] added = { "finalHash=" + hash, "finalSequence=" + sequenceText, "finalCapsuleHash=" + capsuleHash,
                "finalSource=" + source, "finalDestination=" + destination };
            File.AppendAllLines(ResumePath, added);
            foreach (string line in added) { int at = line.IndexOf('='); saved.Fields.Add(line.Substring(0, at), line.Substring(at + 1)); }
            Line("FINAL_AUDIT_BACKFILLED finalSha256=" + hash + " capsuleHash=" + capsuleHash + " sequence=" + sequenceText +
                " resumeSnapshotSha256=" + Sha256(ResumePath));
        }

        private static string LogField(string line, string key)
        {
            string prefix = " " + key + "=";
            int start = line.IndexOf(prefix, StringComparison.Ordinal);
            if (start < 0) throw new InvalidDataException("Log field missing: " + key);
            start += prefix.Length;
            int end = line.IndexOf(' ', start);
            string value = end < 0 ? line.Substring(start) : line.Substring(start, end - start);
            if (value.Length == 0 || value.Length > 256) throw new InvalidDataException("Log field invalid: " + key);
            return value;
        }
        private static string RawSfsValue(string path, string key)
        {
            string prefix = key + " = "; string found = null;
            foreach (string line in File.ReadLines(path))
            {
                string trimmed = line.TrimStart();
                if (!trimmed.StartsWith(prefix, StringComparison.Ordinal)) continue;
                if (found != null) throw new InvalidDataException("Raw SFS field duplicated: " + key);
                found = trimmed.Substring(prefix.Length);
            }
            if (String.IsNullOrEmpty(found) || found.Length > AcceptedStateCodec.MaxEncodedCapsuleBytes)
                throw new InvalidDataException("Raw SFS field missing or oversized: " + key);
            return found;
        }
        private static string Base64Hash(string value)
        {
            if (value == null) return "missing";
            try { return BytesHash(AcceptedStateCodec.DecodeCapsuleBytes(value)); }
            catch (InvalidDataException) { return "invalid-base64"; }
        }
        private static byte[] SafeFinalBytes(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            string raw = RawSfsValue(path, "stateBytesBase64");
            byte[] marker = System.Text.Encoding.ASCII.GetBytes("stateBytesBase64 = " + raw);
            int found = -1;
            for (int i = 0; i <= bytes.Length - marker.Length; i++)
            {
                bool same = true;
                for (int j = 0; j < marker.Length; j++) if (bytes[i + j] != marker[j]) { same = false; break; }
                if (!same) continue;
                if (found >= 0) throw new InvalidDataException("Final SFS capsule line is duplicated");
                found = i;
            }
            if (found < 0) throw new InvalidDataException("Final SFS capsule line is missing");
            int valueStart = found + marker.Length - raw.Length;
            for (int i = 0; i < raw.Length; i++)
            {
                int at = valueStart + i;
                if (bytes[at] == (byte)'/') bytes[at] = (byte)'_';
                else if (bytes[at] == (byte)'+') bytes[at] = (byte)'-';
            }
            if (!String.Equals(BytesHash(AcceptedStateCodec.DecodeCapsuleBytes(System.Text.Encoding.ASCII.GetString(bytes, valueStart, raw.Length))),
                Base64Hash(raw), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("ConfigNode-safe alphabet conversion changed decoded capsule bytes");
            return bytes;
        }
        private static string BytesHash(byte[] bytes)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", String.Empty);
        }

        private static uint[] ParseMembers(string value)
        {
            uint[] members = value.Split(',').Select(x => UInt32.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            if (members.Length == 0 || members.Length > 64 || members.Any(x => x == 0) || members.Distinct().Count() != members.Length)
                throw new InvalidDataException("Resume member set is invalid");
            return members;
        }
        private static double[] ParseAmounts(string value)
        {
            double[] amounts = value.Split(',').Select(x => Double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            if (amounts.Length != Names.Length || amounts.Any(x => Double.IsNaN(x) || Double.IsInfinity(x) || x < 0))
                throw new InvalidDataException("Resume stock vector is invalid");
            return amounts;
        }

        private static double SelectedCapacity(Vessel vessel, uint[] members, string resourceName)
        {
            return vessel.protoVessel.protoPartSnapshots.Where(p => p != null && members.Contains(p.persistentId))
                .SelectMany(p => p.resources).Where(r => r != null && r.resourceName == resourceName).Sum(r => r.maxAmount);
        }

        private Vessel Remote(Guid id)
        {
            if (FlightGlobals.Vessels == null || FlightGlobals.Vessels.Count > 512) throw new InvalidOperationException("Remote vessel set exceeds bound");
            Vessel[] matches = FlightGlobals.Vessels.Where(x => x != null && x.id == id).ToArray();
            if (matches.Length != 1 || matches[0].loaded || matches[0].protoVessel == null || matches[0].protoVessel.protoPartSnapshots == null)
                throw new InvalidOperationException("Selected vessel is absent, duplicate, or still loaded: " + id.ToString("D"));
            return matches[0];
        }

        private void AssertRemoteVessels(bool both)
        {
            Remote(sourceVesselId);
            if (both)
            {
                if (sourceVesselId == destinationVesselId) throw new InvalidOperationException("Source/destination vessel GUIDs match");
                Remote(destinationVesselId);
            }
        }

        private double[] SelectedAmounts(Vessel vessel, uint[] members)
        {
            if (members == null || members.Length == 0 || members.Length > 64 || members.Distinct().Count() != members.Length)
                throw new InvalidOperationException("Selected member set invalid");
            object[] processors = ((IEnumerable)Read(vessel, "vesselModules")).Cast<object>().Where(x => x != null && x.GetType().FullName == "BackgroundResourceProcessing.BackgroundResourceProcessor").ToArray();
            if (processors.Length != 1) throw new InvalidOperationException("Remote vessel has no unique BRP processor");
            Invoke(processors[0], "UpdateBackgroundState");
            double[] totals = new double[Names.Length];
            foreach (uint id in members)
            {
                ProtoPartSnapshot[] parts = vessel.protoVessel.protoPartSnapshots.Where(p => p != null && p.persistentId == id).ToArray();
                if (parts.Length != 1) throw new InvalidOperationException("Selected persistent ID became ambiguous");
                foreach (ProtoPartResourceSnapshot resource in parts[0].resources)
                    for (int i = 0; i < Names.Length; i++) if (resource != null && resource.resourceName == Names[i])
                    {
                        object[] rows = ((IEnumerable)Read(processors[0], "Inventories")).Cast<object>().Where(x => x != null && Read(x, "ModuleId") == null &&
                            Convert.ToUInt32(Read(x, "FlightId"), CultureInfo.InvariantCulture) == parts[0].flightID &&
                            Convert.ToString(Read(x, "ResourceName"), CultureInfo.InvariantCulture) == Names[i]).ToArray();
                        if (rows.Length != 1 || !System.Object.ReferenceEquals(Read(rows[0], "Snapshot"), resource) ||
                            Convert.ToDouble(Read(rows[0], "Amount"), CultureInfo.InvariantCulture) != resource.amount ||
                            Convert.ToDouble(Read(rows[0], "OriginalAmount"), CultureInfo.InvariantCulture) != resource.amount)
                            throw new InvalidOperationException("Selected remote BRP ownership/amount mismatch");
                        totals[i] += resource.amount;
                    }
            }
            return totals;
        }

        private Dictionary<string, bool> SelectedFlowStates()
        {
            Dictionary<string, bool> result = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var pair in new[] { new { Id = sourceVesselId, Members = sourceMembers }, new { Id = destinationVesselId, Members = destinationMembers } })
            {
                Vessel vessel = Remote(pair.Id);
                foreach (uint id in pair.Members)
                {
                    ProtoPartSnapshot[] parts = vessel.protoVessel.protoPartSnapshots.Where(p => p != null && p.persistentId == id).ToArray();
                    if (parts.Length != 1) throw new InvalidOperationException("Selected flow member identity changed");
                    foreach (ProtoPartResourceSnapshot resource in parts[0].resources)
                    {
                        if (resource == null || !Names.Contains(resource.resourceName)) continue;
                        string key = pair.Id.ToString("N") + "/" + id.ToString(CultureInfo.InvariantCulture) + "/" + resource.resourceName;
                        if (result.ContainsKey(key)) throw new InvalidOperationException("Selected flow row identity duplicated");
                        result.Add(key, resource.flowState);
                    }
                }
            }
            return result;
        }
        private void AssertFlowStates()
        {
            Dictionary<string, bool> now = SelectedFlowStates();
            if (flowBaseline == null || now.Count != flowBaseline.Count ||
                now.Any(x => !flowBaseline.ContainsKey(x.Key) || flowBaseline[x.Key] != x.Value))
                throw new InvalidOperationException("Selected tank flowState changed during exact copied-route delivery");
        }
        private void AssertPhysicalWitness(AcceptedState state, string kind, bool debit)
        {
            AcceptedReceipt[] receipts = state.Receipts.Where(x => x.OperationKind == kind && x.Outcome == "accepted" &&
                x.PhysicalWitness != null).OrderByDescending(x => x.CommandSequence).ToArray();
            if (receipts.Length == 0) throw new InvalidDataException("Accepted " + kind + " lacks physical success witness");
            PhysicalSuccessWitness witness = receipts[0].PhysicalWitness;
            if (witness.ProviderId != "BackgroundResourceProcessing.Remote" || witness.Rows.Length == 0 || witness.Rows.Length > 16)
                throw new InvalidDataException("Accepted " + kind + " has wrong remote provider or row bound");
            HashSet<uint> selected = new HashSet<uint>(debit ? sourceMembers : destinationMembers);
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            double[] observed = new double[Names.Length];
            bool creditedLockedOx = false;
            foreach (PhysicalSuccessWitnessRow row in witness.Rows)
            {
                int index = Array.IndexOf(Names, row.ResourceName);
                string key = (debit ? sourceVesselId : destinationVesselId).ToString("N") + "/" +
                    row.MemberPersistentId.ToString(CultureInfo.InvariantCulture) + "/" + row.ResourceName;
                bool originalFlow;
                if (index < 0 || !selected.Contains(row.MemberPersistentId) || !seen.Add(key) ||
                    !flowBaseline.TryGetValue(key, out originalFlow) ||
                    row.ObservedAfterAmount != row.IntendedAfterAmount ||
                    (debit && (!originalFlow || row.IntendedAfterAmount >= row.BeforeAmount)) ||
                    (!debit && row.IntendedAfterAmount <= row.BeforeAmount))
                    throw new InvalidDataException("Accepted " + kind + " witness contains an unselected, locked debit, or nonexact row: " + key);
                observed[index] += Math.Abs(row.IntendedAfterAmount - row.BeforeAmount);
                if (!debit && row.ResourceName == "Oxidizer" && !originalFlow) creditedLockedOx = true;
            }
            for (int i = 0; i < Names.Length; i++)
                if (!EqualQuantity(observed[i], Cargo[i]))
                    throw new InvalidDataException("Accepted " + kind + " witness does not total exact manifest for " + Names[i]);
            if (!debit && !creditedLockedOx)
                throw new InvalidDataException("Accepted arrival did not credit the selected flow-locked Oxidizer tank");
            Line("PHYSICAL_WITNESS kind=" + kind + " rows=" + witness.Rows.Length + " totals=" + Numbers(observed) +
                " provider=" + witness.ProviderId + " lockedOxCredited=" + creditedLockedOx +
                " operationId=" + receipts[0].OperationId);
        }
        private static string FlowSummary(Dictionary<string, bool> rows) => String.Join(",", rows.OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => x.Key + ":" + (x.Value ? "on" : "off")).ToArray());
        private void AssertSavedFlowStates(string save, double[] expectedSource, double[] expectedDestination)
        {
            NoReparse(save);
            ConfigNode wrapper = ConfigNode.Load(save);
            ConfigNode game = wrapper == null ? null : wrapper.name == "GAME" ? wrapper :
                wrapper.GetNodes("GAME").Length == 1 ? wrapper.GetNodes("GAME")[0] : null;
            ConfigNode flight = game == null ? null : game.GetNode("FLIGHTSTATE");
            if (flight == null) throw new InvalidDataException("Selected save lacks FlightState for tank flow proof");
            Dictionary<string, bool> savedFlow = new Dictionary<string, bool>(StringComparer.Ordinal);
            double[] source = new double[Names.Length], destination = new double[Names.Length];
            foreach (ConfigNode vessel in flight.GetNodes("VESSEL"))
            {
                string pid = vessel.GetValue("pid");
                Guid vesselId;
                if (!Guid.TryParseExact(pid, "N", out vesselId)) continue;
                bool isSource = vesselId == sourceVesselId, isDestination = vesselId == destinationVesselId;
                if (!isSource && !isDestination) continue;
                uint[] members = isSource ? sourceMembers : destinationMembers;
                foreach (ConfigNode part in vessel.GetNodes("PART"))
                {
                    uint id;
                    if (!UInt32.TryParse(part.GetValue("persistentId"), NumberStyles.None, CultureInfo.InvariantCulture, out id) ||
                        !members.Contains(id)) continue;
                    foreach (ConfigNode resource in part.GetNodes("RESOURCE"))
                    {
                        string name = resource.GetValue("name");
                        int index = Array.IndexOf(Names, name);
                        if (index < 0) continue;
                        bool flow; double amount;
                        if (!Boolean.TryParse(resource.GetValue("flowState"), out flow) ||
                            !Double.TryParse(resource.GetValue("amount"), NumberStyles.Float, CultureInfo.InvariantCulture, out amount) ||
                            Double.IsNaN(amount) || Double.IsInfinity(amount))
                            throw new InvalidDataException("Selected saved tank flow/amount is invalid");
                        string key = vesselId.ToString("N") + "/" + id.ToString(CultureInfo.InvariantCulture) + "/" + name;
                        if (savedFlow.ContainsKey(key)) throw new InvalidDataException("Selected saved tank row is duplicated");
                        savedFlow.Add(key, flow);
                        if (isSource) source[index] += amount; else destination[index] += amount;
                    }
                }
            }
            if (flowBaseline == null || savedFlow.Count != flowBaseline.Count ||
                savedFlow.Any(x => !flowBaseline.ContainsKey(x.Key) || flowBaseline[x.Key] != x.Value))
                throw new InvalidDataException("Selected saved tank flowState differs from pre-dispatch copy");
            AssertSavedTotals(expectedSource, source, "selected source rows in saved SFS");
            AssertSavedTotals(expectedDestination, destination, "selected destination rows in saved SFS");
            Line("SAVED_SELECTED_ROWS_VERIFIED save=" + save + " sha256=" + Sha256(save) +
                " source=" + Numbers(source) + " destination=" + Numbers(destination) +
                " flowRows=" + savedFlow.Count + " lockedOxPreserved=" + savedFlow.Any(x =>
                    x.Key.StartsWith(destinationVesselId.ToString("N") + "/", StringComparison.Ordinal) &&
                    x.Key.EndsWith("/Oxidizer", StringComparison.Ordinal) && !x.Value));
        }

        private Dictionary<string, double> VisitorAmounts()
        {
            Dictionary<string, double> result = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var pair in new[] { new { Id = sourceVesselId, Members = sourceMembers }, new { Id = destinationVesselId, Members = destinationMembers } })
            {
                Vessel vessel = Remote(pair.Id);
                HashSet<uint> enrolled = new HashSet<uint>(pair.Members);
                foreach (ProtoPartSnapshot part in vessel.protoVessel.protoPartSnapshots)
                {
                    if (part == null || enrolled.Contains(part.persistentId)) continue;
                    foreach (ProtoPartResourceSnapshot resource in part.resources)
                    {
                        if (resource == null) continue;
                        string key = pair.Id.ToString("N") + "/" + part.persistentId + "/" + resource.resourceName;
                        if (result.ContainsKey(key)) throw new InvalidOperationException("Visitor resource identity is duplicated");
                        result.Add(key, resource.amount);
                    }
                }
            }
            return result;
        }
        private void LogVisitorEvolution(string phase)
        {
            Dictionary<string, double> now = VisitorAmounts();
            string[] changes = now.Keys.Union(visitorBaseline.Keys, StringComparer.Ordinal)
                .Where(key => !now.ContainsKey(key) || !visitorBaseline.ContainsKey(key) || now[key] != visitorBaseline[key])
                .OrderBy(key => key, StringComparer.Ordinal).ToArray();
            foreach (string key in changes.Take(64))
                Line("VISITOR_NATURAL_EVOLUTION phase=" + phase + " row=" + key +
                    " baseline=" + (visitorBaseline.ContainsKey(key) ? F(visitorBaseline[key]) : "absent") +
                    " current=" + (now.ContainsKey(key) ? F(now[key]) : "absent"));
            Line("VISITOR_EVOLUTION_SUMMARY phase=" + phase + " baselineRows=" + visitorBaseline.Count +
                " currentRows=" + now.Count + " changedRows=" + changes.Length + " listed=" + Math.Min(changes.Length, 64) +
                " isolationProof=atomicGatewayEffectBoundary");
        }
        private static HashSet<Guid> VesselIds() => FlightGlobals.Vessels == null ? new HashSet<Guid>() : new HashSet<Guid>(FlightGlobals.Vessels.Where(x => x != null).Select(x => x.id));
        private static Dictionary<Guid, string> VesselDescriptions() => FlightGlobals.Vessels == null
            ? new Dictionary<Guid, string>()
            : FlightGlobals.Vessels.Where(x => x != null).GroupBy(x => x.id).ToDictionary(g => g.Key,
                g => g.First().vesselType + ":" + Bound(g.First().vesselName, 80), EqualityComparer<Guid>.Default);
        private void AssertVesselIds()
        {
            Dictionary<Guid, string> current = VesselDescriptions();
            Guid[] added = current.Keys.Except(vesselBaseline).ToArray();
            Guid[] removed = vesselBaseline.Except(current.Keys).ToArray();
            if (added.Length == 0 && removed.Length == 0) return;
            Line("VESSEL_SET_DELTA added=" + String.Join(",", added.Select(id => id.ToString("D") + ":" + current[id]).ToArray()) +
                " removed=" + String.Join(",", removed.Select(id => id.ToString("D") + ":" + vesselBaselineDescriptions[id]).ToArray()));
            bool ambientOnly = added.All(id => current[id].StartsWith(VesselType.SpaceObject + ":", StringComparison.Ordinal)) &&
                removed.All(id => vesselBaselineDescriptions[id].StartsWith(VesselType.SpaceObject + ":", StringComparison.Ordinal));
            if (!ambientOnly) throw new InvalidOperationException("Delivery changed a non-asteroid vessel GUID set");
        }
        private static void AssertUnchanged(double[] before, double[] after, string phase)
        {
            if (before.Length != after.Length || before.Where((x, i) => x != after[i]).Any()) throw new InvalidOperationException("Selected stock changed unexpectedly at " + phase);
        }
        private static bool EqualQuantity(double a, double b) => !Double.IsNaN(a) && !Double.IsNaN(b) &&
            !Double.IsInfinity(a) && !Double.IsInfinity(b) && Math.Abs(a - b) <= 0.000001;
        private static void AssertSavedTotals(double[] expected, double[] actual, string phase)
        {
            if (expected.Length != actual.Length || expected.Where((x, i) => !EqualQuantity(x, actual[i])).Any())
                throw new InvalidDataException("Selected saved tank total differs at " + phase + ": expected=" + Numbers(expected) +
                    " actual=" + Numbers(actual));
        }

        private IEnumerator WaitForState(int depotCount, float seconds)
        {
            float end = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < end)
            {
                AcceptedState state = ReadState();
                if (state != null && state.WorldId == worldId && state.SchemaVersion == 2 && state.Depots.Count(x => x.Active) == depotCount) yield break;
                yield return new WaitForSecondsRealtime(0.25f);
            }
            throw new TimeoutException("Host did not accept exactly " + depotCount + " registered remote depots");
        }
        private IEnumerator WaitForReady(GameScenes scene, float seconds)
        {
            float end = Time.realtimeSinceStartup + seconds; int stable = 0;
            while (Time.realtimeSinceStartup < end)
            {
                WorldBridgeAddon bridge = FindObjectOfType<WorldBridgeAddon>();
                DepotRegistryModule registry = DepotRegistryModule.Instance;
                RecoveryCapsuleModule recovery = RecoveryCapsuleModule.Instance;
                bool ready = HighLogic.LoadedScene == scene && HighLogic.CurrentGame != null && HighLogic.SaveFolder == folder &&
                    bridge != null && !bridge.IsLoadUnresolved && bridge.CanAcceptPhysicalEffects && registry != null && registry.IsReady && !registry.IsCorrupt &&
                    recovery != null && recovery.IsLoaded && !recovery.IsCorrupt && recovery.HasAcceptedState &&
                    (scene != GameScenes.FLIGHT || FlightGlobals.ActiveVessel != null && FlightGlobals.ActiveVessel.loaded && !FlightGlobals.ActiveVessel.packed);
                stable = ready ? stable + 1 : 0;
                if (stable >= 3) yield break;
                yield return null;
            }
            throw new TimeoutException("Disposable context did not become ready in " + scene);
        }
        private static IEnumerator WaitForScene(GameScenes scene, float seconds)
        {
            float end = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < end) { if (HighLogic.LoadedScene == scene) yield break; yield return new WaitForSecondsRealtime(0.25f); }
            throw new TimeoutException("Scene did not reach " + scene);
        }
        private IEnumerator Reload(string name, long sequence, float seconds)
        {
            Game loaded = GamePersistence.LoadGame(name, folder, true, false);
            if (loaded == null) throw new InvalidOperationException("Selected save did not load: " + name);
            HighLogic.CurrentGame = loaded;
            loaded.startScene = GameScenes.SPACECENTER;
            loaded.Start();
            float end = Time.realtimeSinceStartup + seconds; float nextDiagnostic = 0f; int stable = 0;
            while (Time.realtimeSinceStartup < end)
            {
                AcceptedState state = ReadState();
                bool sameGame = HighLogic.CurrentGame == loaded;
                bool sameScene = HighLogic.LoadedScene == GameScenes.SPACECENTER;
                bool sameFolder = HighLogic.SaveFolder == folder;
                bool sameWorld = state != null && state.WorldId == worldId;
                bool sameSequence = state != null && state.AcceptedSequence == sequence;
                bool ready = sameGame && sameScene && sameFolder && sameWorld && sameSequence;
                if (Time.realtimeSinceStartup >= nextDiagnostic)
                {
                    Line("RELOAD_WAIT save=" + name + " sameGame=" + sameGame + " scene=" + HighLogic.LoadedScene +
                        " folder=" + HighLogic.SaveFolder + " sameFolder=" + sameFolder + " statePresent=" + (state != null) +
                        " world=" + (state == null ? "missing" : state.WorldId) + " sameWorld=" + sameWorld +
                        " sequence=" + (state == null ? -1 : state.AcceptedSequence) + " sameSequence=" + sameSequence +
                        " stable=" + stable);
                    nextDiagnostic = Time.realtimeSinceStartup + 5f;
                }
                stable = ready ? stable + 1 : 0;
                if (stable >= 3) { Line("RELOADED save=" + name + " sequence=" + sequence + " capsuleHash=" + AcceptedStateCodec.ComputeHash(state) + " ut=" + F(Planetarium.GetUniversalTime())); yield break; }
                yield return null;
            }
            throw new TimeoutException("Selected Space Center save did not reload with exact accepted sequence");
        }
        private void Save(string name)
        {
            if (HighLogic.SaveFolder != folder || HighLogic.CurrentGame == null) throw new InvalidOperationException("Refusing save outside disposable game");
            GamePersistence.SaveGame(name, folder, SaveMode.OVERWRITE);
            string path = Path.GetFullPath(Path.Combine(root, "saves", folder, name + ".sfs"));
            NoReparse(path);
            if (!path.StartsWith(Path.GetFullPath(Path.Combine(root, "saves", folder)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                throw new IOException("Selected save escaped disposable folder or is missing");
            AcceptedState state = ReadState();
            Line("SAVED name=" + name + " path=" + path + " sha256=" + Sha256(path) + " bytes=" + new FileInfo(path).Length +
                " acceptedSequence=" + (state == null ? -1 : state.AcceptedSequence) + " capsuleHash=" + (state == null ? "missing" : AcceptedStateCodec.ComputeHash(state)));
        }
        private static AcceptedState ReadState()
        {
            RecoveryCapsuleModule module = RecoveryCapsuleModule.Instance;
            byte[] bytes = module == null ? null : module.GetAcceptedStateBytes();
            return bytes == null ? null : AcceptedStateCodec.Deserialize(bytes);
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
            MethodInfo method = value.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            if (method == null) throw new MissingMethodException(value.GetType().FullName, name);
            method.Invoke(value, null);
        }
        private void Line(string message)
        {
            try { File.AppendAllText(logPath, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " " + Bound(message, 2800) + Environment.NewLine); }
            catch { }
        }
        private static string Join(uint[] values) => String.Join(",", values.Select(x => x.ToString(CultureInfo.InvariantCulture)).ToArray());
        private static string Numbers(double[] values) => String.Join(",", values.Select(F).ToArray());
        private static string Manifest(ResourceAmount[] values) => String.Join(",", values.Select(x => x.ResourceName + ":" + x.AmountMicroUnits.ToString(CultureInfo.InvariantCulture)).ToArray());
        private static string F(double value) => value.ToString("R", CultureInfo.InvariantCulture);
        private static string Bound(string value, int max) { if (value == null) return ""; value = value.Replace('\r', ' ').Replace('\n', ' '); return value.Length <= max ? value : value.Substring(0, max); }
        private static string Sha256(string path) { using (var sha = System.Security.Cryptography.SHA256.Create()) using (var stream = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", String.Empty); }
        private static void NoReparse(string path)
        {
            string cursor = Path.GetFullPath(path); string volume = Path.GetPathRoot(cursor);
            while (!String.IsNullOrEmpty(cursor))
            {
                try { if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse-point path refused: " + cursor); }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                if (String.Equals(cursor, volume, StringComparison.OrdinalIgnoreCase)) break;
                cursor = Path.GetDirectoryName(cursor);
            }
        }
        private void OnDestroy() { if (started) TimeWarp.SetRate(0, true); }
    }
}
