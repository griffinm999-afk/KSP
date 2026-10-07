using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Expanse.Domain;
using Expanse.WorldBridge;
using UnityEngine;

namespace Expanse.Recovery.KspFixture
{
    // One-shot, disposable two-vessel BRP/Host integration fixture. It never
    // responds in the regular KSP install. Delivery commands come from the
    // isolated Host fixture CLI after the READY line has been reviewed.
    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public sealed class RemoteDeliverySmokeAddon : MonoBehaviour
    {
        private const string ReducedDevRoot = @"C:\Users\griff\Documents\KSP-RMM-Dev";
        private const string FullDevRoot = @"C:\Users\griff\Documents\KSP-RMM-Dev-Full";
        private const string RequestName = "ExpanseRemoteDeliverySmoke.request";
        private const string RequestPrefix = "RUN_EXPANSE_REMOTE_DELIVERY=";
        private const string ResumePrefix = "RESUME_EXPANSE_REMOTE_DELIVERY=";
        private const string FinalAuditPrefix = "AUDIT_EXPANSE_REMOTE_DELIVERY=";
        private const string TransitSave = "remote-delivery-transit";
        private const string FinalSave = "remote-delivery-final";
        private const int MaxParts = 16384;
        private static readonly string[] Names = { "LiquidFuel", "MonoPropellant", "Oxidizer" };
        private static readonly double[] Cargo = { 1.0, 1.0, 1.0 };

        private string root, token, folder, logPath, sourceDepotId, destinationDepotId, worldId;
        private Guid sourceVesselId, destinationVesselId;
        private uint[] sourceMembers, destinationMembers;
        private Dictionary<string, double> visitorBaseline;
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
                if (!String.Equals(root, ReducedDevRoot, StringComparison.OrdinalIgnoreCase) &&
                    !String.Equals(root, FullDevRoot, StringComparison.OrdinalIgnoreCase)) return;
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
                folder = "ExpanseRemoteDeliverySmoke-" + token;
                logPath = Path.Combine(root, folder + ".log");
                string saveRoot = Path.GetFullPath(Path.Combine(root, "saves"));
                string savePath = Path.GetFullPath(Path.Combine(saveRoot, folder));
                NoReparse(saveRoot); NoReparse(savePath); NoReparse(logPath);
                if (!savePath.StartsWith(saveRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    File.Exists(savePath) || Directory.Exists(logPath) ||
                    (resumeMode || finalAuditMode ? !Directory.Exists(savePath) || !File.Exists(logPath) : Directory.Exists(savePath) || File.Exists(logPath)))
                    throw new IOException("Derived disposable save/log path is unsafe or already exists");
                string craft = null;
                if (!resumeMode && !finalAuditMode)
                {
                    craft = Path.GetFullPath(Path.Combine(root, "GameData", "SquadExpansion", "MakingHistory", "Ships", "VAB", "Muna 1.craft"));
                    if (!craft.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(craft))
                        throw new FileNotFoundException("Stock Muna 1 fixture craft is missing", craft);
                }
                if (!WorldBridgeAddon.ActivateRemotePhysicalFixture(token)) throw new InvalidOperationException("Remote dev Bridge gate rejected token/root");
                File.Delete(requestPath);
                if (resumeMode || finalAuditMode) File.AppendAllText(logPath, (finalAuditMode ? "FINAL_AUDIT_START" : "RESUME_START") +
                    " utc=" + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " token=" + token + Environment.NewLine);
                else File.WriteAllText(logPath, "START utc=" + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " token=" + token + Environment.NewLine);
                AudioListener.volume = 0f;
                DontDestroyOnLoad(gameObject);
                started = accepted = true; startedAt = Time.realtimeSinceStartup;
                StartCoroutine(Supervise(finalAuditMode ? AuditFinal() : resumeMode ? Resume() : Run(craft)));
            }
            catch (Exception ex)
            {
                if (accepted && logPath != null) { Line("FAIL initialize " + Bound(ex.ToString(), 1800)); Application.Quit(); }
            }
        }

        private IEnumerator Supervise(IEnumerator body)
        {
            Stack<IEnumerator> stack = new Stack<IEnumerator>(); stack.Push(body);
            while (Time.realtimeSinceStartup - startedAt < 900f)
            {
                if (stack.Count == 0) yield break;
                bool more; object current;
                try { more = stack.Peek().MoveNext(); current = more ? stack.Peek().Current : null; if (!more) { stack.Pop(); continue; } }
                catch (Exception ex) { Line("FAIL " + Bound(ex.ToString(), 2400)); Application.Quit(); yield break; }
                IEnumerator nested = current as IEnumerator;
                if (nested == null) yield return current; else stack.Push(nested);
            }
            Line("FAIL global watchdog 900s"); Application.Quit();
        }

        private IEnumerator Run(string originalCraft)
        {
            string sourceCraft = Variant(originalCraft, "source", false);
            string destinationCraft = Variant(originalCraft, "destination", true);
            Line("REQUEST manifest=LiquidFuel:1,MonoPropellant:1,Oxidizer:1 sourceCraftSha256=" + Sha256(sourceCraft) +
                " destinationCraftSha256=" + Sha256(destinationCraft) + " originalCraftSha256=" + Sha256(originalCraft));
            yield return new WaitForSecondsRealtime(2f);
            HighLogic.SaveFolder = folder;
            HighLogic.CurrentGame = GamePersistence.CreateNewGame(folder, Game.Modes.SANDBOX, new GameParameters(), "Squad/Flags/default", GameScenes.SPACECENTER, EditorFacility.VAB);
            if (HighLogic.CurrentGame == null) throw new InvalidOperationException("Disposable game creation failed");
            HighLogic.CurrentGame.Start();
            yield return WaitForScene(GameScenes.SPACECENTER, 120f);
            yield return WaitForReady(GameScenes.SPACECENTER, 90f);

            FlightDriver.StartWithNewLaunch(sourceCraft, "Squad/Flags/default", "LaunchPad", new VesselCrewManifest());
            yield return WaitForScene(GameScenes.FLIGHT, 180f);
            yield return WaitForReady(GameScenes.FLIGHT, 120f);
            Vessel source = FlightGlobals.ActiveVessel;
            sourceVesselId = source.id;
            sourceMembers = SelectMembers(source, false);
            Line("SOURCE_LOADED selected=" + LoadedAmounts(source, sourceMembers));
            DepotRegistryModule registry = DepotRegistryModule.Instance;
            if (registry == null || !registry.Register("Remote source " + token.Substring(0, 8), sourceMembers[0], sourceMembers))
                throw new InvalidOperationException("Source selected registration failed");
            sourceDepotId = registry.RegisteredDepotIds.Single();
            worldId = registry.WorldId;
            Line("SOURCE_REGISTERED vesselId=" + sourceVesselId.ToString("D") + " depotId=" + sourceDepotId + " memberIds=" + Join(sourceMembers));
            Save("persistent");
            HighLogic.LoadScene(GameScenes.SPACECENTER);
            yield return WaitForScene(GameScenes.SPACECENTER, 180f);
            yield return WaitForReady(GameScenes.SPACECENTER, 90f);
            AssertRemoteVessels(false);

            FlightDriver.StartWithNewLaunch(destinationCraft, "Squad/Flags/default", "Runway", new VesselCrewManifest());
            yield return WaitForScene(GameScenes.FLIGHT, 180f);
            yield return WaitForReady(GameScenes.FLIGHT, 120f);
            Vessel destination = FlightGlobals.ActiveVessel;
            destinationVesselId = destination.id;
            if (destinationVesselId == sourceVesselId) throw new InvalidOperationException("Second launch reused source vessel GUID");
            destinationMembers = SelectMembers(destination, true);
            Line("DESTINATION_LOADED selected=" + LoadedAmounts(destination, destinationMembers));
            registry = DepotRegistryModule.Instance;
            if (registry == null || registry.WorldId != worldId || !registry.Register("Remote destination " + token.Substring(0, 8), destinationMembers[0], destinationMembers))
                throw new InvalidOperationException("Destination selected registration failed or source world changed");
            destinationDepotId = registry.RegisteredDepotIds.Single(x => x != sourceDepotId);
            if (sourceMembers.Intersect(destinationMembers).Any()) throw new InvalidOperationException("Distinct vessels reused selected persistent part IDs");
            Line("DESTINATION_REGISTERED vesselId=" + destinationVesselId.ToString("D") + " depotId=" + destinationDepotId + " memberIds=" + Join(destinationMembers));
            Save("persistent");
            HighLogic.LoadScene(GameScenes.SPACECENTER);
            yield return WaitForScene(GameScenes.SPACECENTER, 180f);
            yield return WaitForReady(GameScenes.SPACECENTER, 90f);
            AssertRemoteVessels(true);
            yield return WaitForState(2, 90f);

            sourceBaseline = SelectedAmounts(Remote(sourceVesselId), sourceMembers);
            destinationBaseline = SelectedAmounts(Remote(destinationVesselId), destinationMembers);
            for (int i = 0; i < Names.Length; i++)
                if (sourceBaseline[i] < Cargo[i] || destinationBaseline[i] + Cargo[i] > SelectedCapacity(Remote(destinationVesselId), destinationMembers, Names[i]))
                    throw new InvalidOperationException("Derived craft did not provide exact manifest stock/capacity for " + Names[i]);
            visitorBaseline = VisitorAmounts();
            vesselBaseline = VesselIds();
            vesselBaselineDescriptions = VesselDescriptions();
            Line("REMOTE_BASELINE source=" + Numbers(sourceBaseline) + " destination=" + Numbers(destinationBaseline) +
                " visitorRows=" + visitorBaseline.Count + " vesselCount=" + vesselBaseline.Count + " ut=" + F(Planetarium.GetUniversalTime()));

            // Short, isolated high-warp phase measures provider scan cost before
            // dispatch, then returns to 1x so due time cannot race the first save.
            TimeWarp.SetRate(2, true);
            yield return new WaitForSecondsRealtime(8f);
            TimeWarp.SetRate(0, true);
            yield return new WaitForSecondsRealtime(2f);
            AssertRemoteVessels(true);
            AssertUnchanged(sourceBaseline, SelectedAmounts(Remote(sourceVesselId), sourceMembers), "source before dispatch");
            AssertUnchanged(destinationBaseline, SelectedAmounts(Remote(destinationVesselId), destinationMembers), "destination before dispatch");
            AssertVisitors();
            AcceptedState state = ReadState();
            WorldBridgeAddon bridge = FindObjectOfType<WorldBridgeAddon>();
            if (bridge == null || state == null || state.WorldId != worldId || state.Depots.Count(x => x.Active) != 2 ||
                !bridge.CanAcceptPhysicalEffects) throw new InvalidOperationException("Two selected remote endpoints are not effect-ready");
            Line("READY token=" + token + " worldId=" + worldId + " runId=" + bridge.CurrentRunId + " sessionId=" + bridge.CurrentSessionId +
                " loadEpoch=" + bridge.CurrentLoadEpoch + " sourceDepotId=" + sourceDepotId + " destinationDepotId=" + destinationDepotId +
                " sourceVesselId=" + sourceVesselId.ToString("D") + " destinationVesselId=" + destinationVesselId.ToString("D") +
                " sourceMembers=" + Join(sourceMembers) + " destinationMembers=" + Join(destinationMembers) +
                " routeDurationSeconds=120 manifest=LF1,MP1,OX1 acceptedSequence=" + state.AcceptedSequence +
                " capsuleHash=" + AcceptedStateCodec.ComputeHash(state));

            float dispatchUntil = Time.realtimeSinceStartup + 180f;
            ActiveShipmentRecord shipment = null;
            while (Time.realtimeSinceStartup < dispatchUntil)
            {
                state = ReadState();
                if (state != null && state.WritesBlocked) throw new InvalidOperationException("Physical fault during dispatch");
                if (state != null) shipment = state.ActiveShipments.SingleOrDefault(x => !x.LegacyOpaque);
                if (shipment != null && state.Receipts.Any(x => x.OperationKind == "dispatch" && x.Outcome == "accepted")) break;
                yield return new WaitForSecondsRealtime(0.25f);
            }
            if (shipment == null) throw new TimeoutException("Host did not dispatch the exact remote route");
            AssertRemoteVessels(true);
            double[] sourceAfter = SelectedAmounts(Remote(sourceVesselId), sourceMembers);
            double[] destinationAfter = SelectedAmounts(Remote(destinationVesselId), destinationMembers);
            for (int i = 0; i < Names.Length; i++)
                if (sourceAfter[i] != sourceBaseline[i] - Cargo[i] || destinationAfter[i] != destinationBaseline[i] ||
                    shipment.RemainingResources.Single(x => x.ResourceName == Names[i]).AmountMicroUnits != (long)(Cargo[i] * 1000000))
                    throw new InvalidOperationException("Dispatch debit/cargo conservation failed for " + Names[i]);
            AssertVisitors();
            if (shipment.DueUt <= shipment.DepartureUt || Planetarium.GetUniversalTime() >= shipment.DueUt)
                throw new InvalidOperationException("No in-transit interval remains after dispatch");
            string shipmentId = shipment.ShipmentId;
            long dispatchSequence = state.AcceptedSequence;
            Line("DISPATCHED shipmentId=" + shipmentId + " departureUt=" + F(shipment.DepartureUt) + " dueUt=" + F(shipment.DueUt) +
                " source=" + Numbers(sourceAfter) + " destination=" + Numbers(destinationAfter) +
                " cargo=" + Manifest(shipment.RemainingResources) + " sequence=" + dispatchSequence + " capsuleHash=" + AcceptedStateCodec.ComputeHash(state));
            AssertVesselIds();
            Save(TransitSave);
            WriteResumeSnapshot(shipmentId, dispatchSequence, state, sourceAfter, destinationAfter);
            Line("TRANSIT_SAVE_CUT_READY restart dev KSP with " + ResumePrefix + token + " and exact saved hash=" + Sha256(SavePath(TransitSave)));
            Application.Quit();
        }

        private string SavePath(string name) => Path.GetFullPath(Path.Combine(root, "saves", folder, name + ".sfs"));
        private string ResumePath => Path.GetFullPath(Path.Combine(root, folder + ".resume"));

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
                "visitorCount=" + visitorBaseline.Count.ToString(CultureInfo.InvariantCulture)
            };
            foreach (var row in visitorBaseline.OrderBy(x => x.Key, StringComparer.Ordinal)) lines.Add("visitor=" + row.Key + "=" + F(row.Value));
            File.WriteAllLines(path, lines.ToArray());
            Line("RESUME_SNAPSHOT path=" + path + " sha256=" + Sha256(path) + " visitorRows=" + visitorBaseline.Count);
        }

        private sealed class ResumeValues
        {
            internal readonly Dictionary<string, string> Fields = new Dictionary<string, string>(StringComparer.Ordinal);
            internal readonly Dictionary<string, double> Visitors = new Dictionary<string, double>(StringComparer.Ordinal);
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
                if (key == "visitor")
                {
                    int valueSeparator = value.LastIndexOf('='); double amount;
                    if (valueSeparator <= 0 || !Double.TryParse(value.Substring(valueSeparator + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out amount) ||
                        Double.IsNaN(amount) || Double.IsInfinity(amount) || result.Visitors.ContainsKey(value.Substring(0, valueSeparator)))
                        throw new InvalidDataException("Malformed or duplicate visitor resume row");
                    result.Visitors.Add(value.Substring(0, valueSeparator), amount);
                }
                else { if (result.Fields.ContainsKey(key)) throw new InvalidDataException("Duplicate resume snapshot field"); result.Fields.Add(key, value); }
            }
            int visitorCount;
            if (result.Get("token") != token || !Int32.TryParse(result.Get("visitorCount"), NumberStyles.None, CultureInfo.InvariantCulture, out visitorCount) ||
                visitorCount < 0 || visitorCount > 4096 || result.Visitors.Count != visitorCount)
                throw new InvalidDataException("Resume snapshot token or visitor count mismatch");
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
            long dispatchSequence = Int64.Parse(saved.Get("sequence"), CultureInfo.InvariantCulture);
            string shipmentId = saved.Get("shipment");
            Line("RESUME_HASH_VERIFIED transitSha256=" + Sha256(selectedSave) + " snapshotSha256=" + Sha256(ResumePath));
            yield return new WaitForSecondsRealtime(2f);
            HighLogic.SaveFolder = folder;
            Game loaded = GamePersistence.LoadGame("persistent", folder, true, false);
            if (loaded == null) throw new InvalidDataException("Staged disposable persistent save did not load in fresh process");
            HighLogic.CurrentGame = loaded;
            loaded.startScene = GameScenes.SPACECENTER;
            loaded.Start();
            yield return WaitForScene(GameScenes.SPACECENTER, 180f);
            yield return WaitForReady(GameScenes.SPACECENTER, 120f);
            AcceptedState state = ReadState();
            RecoveryCapsuleModule recoveredModule = RecoveryCapsuleModule.Instance;
            Line("FRESH_PROCESS_CAPSULE scene=" + HighLogic.LoadedScene + " saveFolder=" + HighLogic.SaveFolder +
                " sameGame=" + (HighLogic.CurrentGame == loaded) + " loadedStartScene=" + loaded.startScene +
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
            AssertVisitors();
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
            TimeWarp.SetRate(2, true);
            float arrivalUntil = Time.realtimeSinceStartup + 240f;
            AcceptedReceipt arrival = null;
            while (Time.realtimeSinceStartup < arrivalUntil)
            {
                state = ReadState();
                if (state != null && state.WritesBlocked) throw new InvalidOperationException("Physical fault during arrival");
                if (state != null) arrival = state.Receipts.LastOrDefault(x => x.OperationKind == "arrival" && x.Outcome == "accepted" && x.AppliedUt >= shipment.DueUt);
                if (arrival != null) break;
                yield return new WaitForSecondsRealtime(0.25f);
            }
            TimeWarp.SetRate(0, true);
            if (arrival == null) throw new TimeoutException("Host did not settle due remote arrival");
            if (state.ActiveShipments.Any(x => x.ShipmentId == shipmentId)) throw new InvalidOperationException("Cargo remains after full-capacity destination credit");
            AssertRemoteVessels(true);
            double[] sourceFinal = SelectedAmounts(Remote(sourceVesselId), sourceMembers);
            double[] destinationFinal = SelectedAmounts(Remote(destinationVesselId), destinationMembers);
            for (int i = 0; i < Names.Length; i++)
                if (sourceFinal[i] != sourceAfter[i] || destinationFinal[i] != destinationBaseline[i] + Cargo[i] ||
                    sourceFinal[i] + destinationFinal[i] != sourceBaseline[i] + destinationBaseline[i])
                    throw new InvalidOperationException("Arrival credit/conservation failed for " + Names[i]);
            AssertVisitors();
            Line("ARRIVED shipmentId=" + shipmentId + " dueUt=" + F(shipment.DueUt) + " appliedUt=" + F(arrival.AppliedUt) +
                " source=" + Numbers(sourceFinal) + " destination=" + Numbers(destinationFinal) +
                " sequence=" + state.AcceptedSequence + " capsuleHash=" + AcceptedStateCodec.ComputeHash(state));
            AssertVesselIds();
            Save(FinalSave);
            File.AppendAllLines(ResumePath, new[] {
                "finalHash=" + Sha256(SavePath(FinalSave)),
                "finalSequence=" + state.AcceptedSequence.ToString(CultureInfo.InvariantCulture),
                "finalCapsuleHash=" + AcceptedStateCodec.ComputeHash(state),
                "finalSource=" + Numbers(sourceFinal), "finalDestination=" + Numbers(destinationFinal)
            });
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
            visitorBaseline = saved.Visitors;
            double[] sourceFinal = ParseAmounts(saved.Get("finalSource")), destinationFinal = ParseAmounts(saved.Get("finalDestination"));
            long finalSequence = Int64.Parse(saved.Get("finalSequence"), CultureInfo.InvariantCulture);
            string shipmentId = saved.Get("shipment");
            yield return new WaitForSecondsRealtime(2f);
            HighLogic.SaveFolder = folder;
            Game loaded = GamePersistence.LoadGame("persistent", folder, true, false);
            if (loaded == null) throw new InvalidDataException("Staged disposable final save did not load");
            HighLogic.CurrentGame = loaded; loaded.startScene = GameScenes.SPACECENTER; loaded.Start();
            yield return WaitForScene(GameScenes.SPACECENTER, 180f);
            yield return WaitForReady(GameScenes.SPACECENTER, 120f);
            AcceptedState state = ReadState();
            Line("FINAL_FRESH_PROCESS_CAPSULE scene=" + HighLogic.LoadedScene + " folder=" + HighLogic.SaveFolder +
                " sameGame=" + (HighLogic.CurrentGame == loaded) + " moduleLoaded=" + (RecoveryCapsuleModule.Instance != null && RecoveryCapsuleModule.Instance.IsLoaded) +
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
            AssertVisitors();
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
            if (members.Length == 0 || members.Length > 3 || members.Any(x => x == 0) || members.Distinct().Count() != members.Length)
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

        private string Variant(string original, string role, bool empty)
        {
            ConfigNode node = ConfigNode.Load(original);
            if (node == null) throw new InvalidDataException("Stock craft ConfigNode did not load");
            HashSet<string> altered = new HashSet<string>(StringComparer.Ordinal);
            foreach (ConfigNode part in node.GetNodes("PART"))
                foreach (ConfigNode resource in part.GetNodes("RESOURCE"))
                    for (int i = 0; i < Names.Length; i++)
                        if (!altered.Contains(Names[i]) && resource.GetValue("name") == Names[i])
                        {
                            double max;
                            if (!Double.TryParse(resource.GetValue("maxAmount"), NumberStyles.Float, CultureInfo.InvariantCulture, out max) || max < 3)
                                throw new InvalidDataException("Stock craft resource has insufficient capacity for " + Names[i]);
                            if (empty && !resource.SetValue("amount", (max - 2.0).ToString("R", CultureInfo.InvariantCulture), false))
                                throw new InvalidDataException("Derived destination amount override failed for " + Names[i]);
                            altered.Add(Names[i]);
                        }
            if (altered.Count != 3) throw new InvalidDataException("Stock craft lacks a selected LF/OX/MP resource row");
            string path = Path.Combine(root, folder + "-" + role + ".craft");
            NoReparse(path);
            if (File.Exists(path)) throw new IOException("Derived craft already exists");
            node.Save(path);
            return path;
        }

        private static uint[] SelectMembers(Vessel vessel, bool destination)
        {
            if (vessel == null || vessel.parts == null || vessel.parts.Count > MaxParts) throw new InvalidOperationException("Loaded fixture vessel exceeds part bound");
            List<uint> ids = new List<uint>();
            foreach (string name in Names)
            {
                Part part = vessel.parts.FirstOrDefault(p => p != null && p.Resources != null && p.Resources.Cast<PartResource>().Any(r =>
                    r != null && r.resourceName == name && r.maxAmount >= 3 &&
                    (destination ? r.maxAmount - r.amount >= 1.0 : r.amount >= 1.0)));
                if (part == null || part.persistentId == 0 || part.flightID == 0) throw new InvalidOperationException("Derived craft selected resource did not load: " + name);
                if (!ids.Contains(part.persistentId)) ids.Add(part.persistentId);
            }
            return ids.ToArray();
        }

        private static string LoadedAmounts(Vessel vessel, uint[] members)
        {
            return String.Join(";", vessel.parts.Where(p => p != null && members.Contains(p.persistentId)).Select(p =>
                p.persistentId.ToString(CultureInfo.InvariantCulture) + ":" + String.Join(",", p.Resources.Cast<PartResource>()
                    .Where(r => r != null && Names.Contains(r.resourceName))
                    .Select(r => r.resourceName + "=" + F(r.amount) + "/" + F(r.maxAmount)).ToArray())).ToArray());
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
            if (members == null || members.Length == 0 || members.Length > 3 || members.Distinct().Count() != members.Length)
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
                        if (resource == null || !Names.Contains(resource.resourceName)) continue;
                        string key = pair.Id.ToString("N") + "/" + part.persistentId + "/" + resource.resourceName;
                        if (result.ContainsKey(key)) throw new InvalidOperationException("Visitor resource identity is duplicated");
                        result.Add(key, resource.amount);
                    }
                }
            }
            return result;
        }
        private void AssertVisitors()
        {
            Dictionary<string, double> now = VisitorAmounts();
            if (now.Count != visitorBaseline.Count || now.Any(x => !visitorBaseline.ContainsKey(x.Key) || visitorBaseline[x.Key] != x.Value))
                throw new InvalidOperationException("Unregistered visitor LF/OX/MP changed during selected delivery");
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
