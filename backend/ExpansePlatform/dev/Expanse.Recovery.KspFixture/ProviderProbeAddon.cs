using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Expanse.WorldBridge;
using UnityEngine;

namespace Expanse.Recovery.KspFixture
{
    // This one-shot diagnostic runs only in the exact muted development install. It does
    // not change PartResource amounts and saves only a new token-derived disposable world.
    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public sealed class ProviderProbeAddon : MonoBehaviour
    {
        private const string RequiredRoot = @"C:\Users\griff\Documents\KSP-RMM-Dev";
        private const string RequestName = "ExpanseProviderProbe.request";
        private const string RequestPrefix = "RUN_EXPANSE_PROVIDER_PROBE=";
        private const string RemoteRequestName = "ExpanseRemoteBrpProbe.request";
        private const string RemoteRequestPrefix = "RUN_EXPANSE_REMOTE_BRP_PROBE=";
        private const string TransferRequestName = "ExpanseRemoteBrpTransfer.request";
        private const string TransferRequestPrefix = "RUN_EXPANSE_REMOTE_BRP_TRANSFER=";
        private const string SaveName = "provider-probe";
        private const int MaxParts = 16384;
        private const int MaxResources = 4096;

        private string root, token, logPath, saveFolder;
        private bool started, subscribed, remoteMode, transferMode;
        private bool reloadLoadObserved, reloadGuiReadyObserved, reloadFlightReadyObserved;
        private int lastBrpInventoryCount;
        private uint selectedPartIdA, selectedPartIdB;
        private float startedAt;

        private void Start()
        {
            bool accepted = false;
            try
            {
                root = Path.GetFullPath(KSPUtil.ApplicationRootPath).TrimEnd('\\', '/');
                if (!String.Equals(root, RequiredRoot, StringComparison.OrdinalIgnoreCase)) return;
                AssertNoReparseAncestors(root);
                string requestPath = Path.Combine(root, RequestName);
                string requestPrefix = RequestPrefix;
                if (!File.Exists(requestPath))
                {
                    requestPath = Path.Combine(root, RemoteRequestName);
                    requestPrefix = RemoteRequestPrefix;
                    remoteMode = true;
                }
                if (!File.Exists(requestPath))
                {
                    requestPath = Path.Combine(root, TransferRequestName);
                    requestPrefix = TransferRequestPrefix;
                    remoteMode = transferMode = true;
                }
                if (!File.Exists(requestPath)) return;
                if ((File.GetAttributes(requestPath) & FileAttributes.ReparsePoint) != 0) return;
                AssertNoReparseAncestors(requestPath);
                if (new FileInfo(requestPath).Length > 128) throw new InvalidDataException("Probe request exceeds its small input bound.");
                string request = File.ReadAllText(requestPath).Trim();
                if (!request.StartsWith(requestPrefix, StringComparison.Ordinal) || request.Length != requestPrefix.Length + 32 ||
                    !Guid.TryParseExact(request.Substring(requestPrefix.Length), "N", out Guid parsed) || parsed == Guid.Empty) return;

                token = parsed.ToString("N");
                logPath = Path.Combine(root, (transferMode ? "ExpanseRemoteBrpTransfer-" : remoteMode ? "ExpanseRemoteBrpProbe-" : "ExpanseProviderProbe-") + token + ".log");
                AssertNoReparseAncestors(logPath);
                if (File.Exists(logPath) || Directory.Exists(logPath)) throw new IOException("Probe log already exists; refusing to overwrite evidence.");
                saveFolder = (transferMode ? "ExpanseRemoteBrpTransfer-" : remoteMode ? "ExpanseRemoteBrpProbe-" : "ExpanseProviderProbe-") + token;
                string savesRoot = Path.GetFullPath(Path.Combine(root, "saves"));
                AssertNoReparseAncestors(savesRoot);
                string savePath = Path.GetFullPath(Path.Combine(savesRoot, saveFolder));
                if (!savePath.StartsWith(savesRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || Directory.Exists(savePath) || File.Exists(savePath))
                    throw new IOException("Derived probe save path exists or escapes the dev saves directory.");
                string craft = Path.GetFullPath(Path.Combine(root, "GameData", "SquadExpansion", "MakingHistory", "Ships", "VAB", "Muna 1.craft"));
                if (!craft.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(craft))
                    throw new FileNotFoundException("Expected stock Muna 1 probe craft is missing.", craft);

                File.Delete(requestPath); // consume the exact token request once
                File.WriteAllText(logPath, "START utc=" + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " token=" + token + Environment.NewLine);
                AudioListener.volume = 0f;
                DontDestroyOnLoad(gameObject);
                ProviderProbeScenario.Log = Line;
                SubscribeSaveEvents();
                started = true;
                accepted = true;
                startedAt = Time.realtimeSinceStartup;
                StartCoroutine(Supervise(Run(craft)));
            }
            catch (Exception ex)
            {
                if (accepted) { Line("FAIL initialize " + Bound(ex.ToString(), 1024)); Application.Quit(); }
                else if (logPath != null && File.Exists(logPath)) { Line("FAIL initialize " + Bound(ex.ToString(), 1024)); Application.Quit(); }
            }
        }

        private IEnumerator Supervise(IEnumerator body)
        {
            float deadline = Time.realtimeSinceStartup + 600f;
            while (true)
            {
                if (Time.realtimeSinceStartup - startedAt > 600f || Time.realtimeSinceStartup > deadline)
                {
                    Line("FAIL global watchdog 600s");
                    Application.Quit();
                    yield break;
                }
                object current;
                bool more;
                try { more = body.MoveNext(); current = more ? body.Current : null; }
                catch (Exception ex) { Line("FAIL " + Bound(ex.ToString(), 2048)); Application.Quit(); yield break; }
                if (!more) yield break;
                yield return current;
            }
        }

        private IEnumerator Run(string craftPath)
        {
            Line("REQUEST accepted root=" + root + " saveFolder=" + saveFolder + " saveName=" + SaveName + " craft=Muna-1 transferCandidate=" + transferMode);
            Line("SAVE_ORDER_SOURCE GamePersistence.SaveGame -> Game.Updated -> FlightState()/Vessel.BackupVessel captures PART and vessel-module state -> ScenarioRunner.GetUpdatedProtoModules calls ScenarioModule.OnSave -> onGameStateSave -> Game.Save copies snapshots -> onGameStateSaved -> ConfigNode.Save. BRP onGameStateSave is after saved stock/capsule capture and is not a pre-capture sync hook.");
            if (transferMode) craftPath = PrepareRemoteTransferCraft(craftPath);
            yield return new WaitForSecondsRealtime(3f);
            HighLogic.SaveFolder = saveFolder;
            HighLogic.CurrentGame = GamePersistence.CreateNewGame(saveFolder, Game.Modes.SANDBOX, new GameParameters(), "Squad/Flags/default", GameScenes.SPACECENTER, EditorFacility.VAB);
            if (HighLogic.CurrentGame == null) throw new InvalidOperationException("CreateNewGame returned null.");
            HighLogic.CurrentGame.Start();
            yield return WaitForScene(GameScenes.SPACECENTER, 120f);
            FlightDriver.StartWithNewLaunch(craftPath, "Squad/Flags/default", "LaunchPad", new VesselCrewManifest());
            yield return WaitForScene(GameScenes.FLIGHT, 180f);
            yield return WaitForVessel(180f);

            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel.parts == null || vessel.parts.Count > MaxParts) throw new InvalidOperationException("Active vessel part set is unavailable or exceeds the diagnostic bound.");
            Part[] pair = ChooseDeterministicResourcePair(vessel);
            Part partA = pair[0], partB = pair[1];
            selectedPartIdA = partA.persistentId;
            selectedPartIdB = partB.persistentId;
            Line("FLIGHT_READY saveFolder=" + HighLogic.SaveFolder + " vessel=" + Bound(vessel.vesselName, 128) + " vesselId=" + vessel.id.ToString("D") + " parts=" + vessel.parts.Count + " loaded=" + vessel.loaded + " packed=" + vessel.packed);
            ProbeSnapshot beforeSave = LogProbeObservations(vessel, "before-save", new[] { partA, partB });
            int inventories = lastBrpInventoryCount;
            if (inventories < 1) throw new InvalidOperationException("BRP BackgroundResourceProcessor or its inventory collection was not available on the active vessel.");
            if (remoteMode)
            {
                // This gate is deliberately observation-only. A packed loaded vessel
                // does not count as an unloaded provider endpoint.
                DepotRegistryModule registry = DepotRegistryModule.Instance;
                if (registry == null || !registry.IsReady || registry.IsCorrupt ||
                    !registry.Register("Remote probe source " + token.Substring(0, 8), selectedPartIdA, new[] { selectedPartIdA }) ||
                    !registry.Register("Remote probe destination " + token.Substring(0, 8), selectedPartIdB, new[] { selectedPartIdB }) ||
                    registry.RegisteredDepotIds.Length != 2)
                    throw new InvalidOperationException("Disposable selected members could not be registered as two exact depots.");
                string[] registeredDepotIds = registry.RegisteredDepotIds.OrderBy(x => x, StringComparer.Ordinal).ToArray();
                // KSP rebuilds Scenario modules when leaving Flight. Persist the
                // exact disposable registration before that scene transition.
                GamePersistence.SaveGame("persistent", saveFolder, SaveMode.OVERWRITE);
                string registrationSave = Path.GetFullPath(Path.Combine(root, "saves", saveFolder, "persistent.sfs"));
                AssertNoReparseAncestors(registrationSave);
                if (!registrationSave.StartsWith(Path.GetFullPath(Path.Combine(root, "saves", saveFolder)) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) || !File.Exists(registrationSave))
                    throw new IOException("Selected registration save was not captured in the disposable folder.");
                Line("REMOTE_REGISTRY_SAVED depotIds=" + String.Join(",", registeredDepotIds) + " sha256=" + Sha256(registrationSave));
                Guid selectedVesselId = vessel.id;
                uint selectedFlightIdA = partA.flightID, selectedFlightIdB = partB.flightID;
                Line("REMOTE_PROBE_START vesselId=" + selectedVesselId.ToString("D") + " selectedPersistentIds=" + selectedPartIdA + "," + selectedPartIdB +
                    " selectedFlightIds=" + selectedFlightIdA + "," + selectedFlightIdB + " depotIds=" + String.Join(",", registeredDepotIds) +
                    " loaded=" + vessel.loaded + " packed=" + vessel.packed);
                HighLogic.LoadScene(GameScenes.SPACECENTER);
                yield return WaitForScene(GameScenes.SPACECENTER, 180f);
                float remoteUntil = Time.realtimeSinceStartup + 90f;
                Vessel remote = null;
                while (Time.realtimeSinceStartup < remoteUntil)
                {
                    remote = FlightGlobals.Vessels == null ? null : FlightGlobals.Vessels.SingleOrDefault(x => x != null && x.id == selectedVesselId);
                    if (remote != null && !remote.loaded && remote.protoVessel != null) break;
                    yield return null;
                }
                if (remote == null || remote.loaded || remote.protoVessel == null)
                    throw new InvalidOperationException("Selected vessel did not become a truly unloaded proto vessel after Space Center transition; no write was attempted.");
                DepotRegistryModule reattachedRegistry = DepotRegistryModule.Instance;
                if (reattachedRegistry == null || !reattachedRegistry.IsReady || reattachedRegistry.IsCorrupt ||
                    !registeredDepotIds.SequenceEqual(reattachedRegistry.RegisteredDepotIds.OrderBy(x => x, StringComparer.Ordinal)))
                    throw new InvalidOperationException("The selected depot registrations did not survive the remote scene transition.");
                LogRemoteProviderOwnership(remote, new[] { selectedPartIdA, selectedPartIdB }, new[] { selectedFlightIdA, selectedFlightIdB });
                if (transferMode)
                {
                    const string resourceName = "LiquidFuel";
                    ProtoPartResourceSnapshot sourceSnapshot = FindRemoteResource(remote, selectedPartIdA, selectedFlightIdA, resourceName);
                    ProtoPartResourceSnapshot destinationSnapshot = FindRemoteResource(remote, selectedPartIdB, selectedFlightIdB, resourceName);
                    double sourceBefore = sourceSnapshot.amount, destinationBefore = destinationSnapshot.amount;
                    if (sourceBefore < 1.0 || destinationSnapshot.maxAmount - destinationBefore < 1.0)
                        throw new InvalidOperationException("Derived craft did not provide exact selected debit and credit headroom.");
                    Dictionary<string, double> allBefore = CaptureRemoteProtoStock(remote);
                    var transfer = RemoteBrpTransactionCandidate.Transfer(
                        new RemoteBrpTransactionCandidate.Selection { Vessel = remote, PersistentId = selectedPartIdA, FlightId = selectedFlightIdA,
                            ResourceName = resourceName, ObservedAmount = sourceBefore, ObservedCapacity = sourceSnapshot.maxAmount },
                        new RemoteBrpTransactionCandidate.Selection { Vessel = remote, PersistentId = selectedPartIdB, FlightId = selectedFlightIdB,
                            ResourceName = resourceName, ObservedAmount = destinationBefore, ObservedCapacity = destinationSnapshot.maxAmount }, 1.0);
                    Line("REMOTE_TRANSFER_RESULT applied=" + transfer.Applied + " faulted=" + transfer.Faulted + " reason=" + Bound(transfer.Reason, 256));
                    if (!transfer.Applied || transfer.Faulted) throw new InvalidOperationException("Selected BRP development transaction did not apply: " + transfer.Reason);
                    if (sourceSnapshot.amount != sourceBefore - 1.0 || destinationSnapshot.amount != destinationBefore + 1.0)
                        throw new InvalidDataException("Selected remote proto amounts differ from exact one-unit transfer.");
                    Dictionary<string, double> allAfter = CaptureRemoteProtoStock(remote);
                    AssertOnlySelectedChanged(allBefore, allAfter, selectedPartIdA, selectedPartIdB, resourceName);
                    LogRemoteProviderOwnership(remote, new[] { selectedPartIdA, selectedPartIdB }, new[] { selectedFlightIdA, selectedFlightIdB });
                    Line("REMOTE_TRANSFER_CONSERVED sourceBefore=" + F(sourceBefore) + " sourceAfter=" + F(sourceSnapshot.amount) +
                        " destinationBefore=" + F(destinationBefore) + " destinationAfter=" + F(destinationSnapshot.amount) +
                        " visitorRowsUnchanged=" + (allBefore.Count - 2));
                    GamePersistence.SaveGame(SaveName, saveFolder, SaveMode.OVERWRITE);
                    string selectedSave = Path.GetFullPath(Path.Combine(root, "saves", saveFolder, SaveName + ".sfs"));
                    AssertNoReparseAncestors(selectedSave);
                    if (!selectedSave.StartsWith(Path.GetFullPath(Path.Combine(root, "saves", saveFolder)) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase) || !File.Exists(selectedSave))
                        throw new IOException("Selected remote transfer save is missing or outside disposable save folder.");
                    Line("REMOTE_TRANSFER_SAVED sha256=" + Sha256(selectedSave) + " bytes=" + new FileInfo(selectedSave).Length +
                        " source=" + F(sourceSnapshot.amount) + " destination=" + F(destinationSnapshot.amount));
                    Game reloaded = GamePersistence.LoadGame(SaveName, saveFolder, true, false);
                    if (reloaded == null) throw new InvalidDataException("Selected remote transfer save did not load.");
                    HighLogic.CurrentGame = reloaded;
                    SelectSavedActiveVessel(reloaded, selectedVesselId);
                    // Flight is required to reconstruct an observable Vessel and
                    // exercise BRP's unloaded-to-loaded restore path. Space Center
                    // keeps FlightGlobals.Vessels unavailable after this LoadGame call.
                    reloaded.startScene = GameScenes.FLIGHT;
                    reloaded.Start();
                    yield return WaitForScene(GameScenes.FLIGHT, 180f);
                    float reloadDeadline = Time.realtimeSinceStartup + 90f;
                    Vessel reloadedRemote = null;
                    while (Time.realtimeSinceStartup < reloadDeadline)
                    {
                        reloadedRemote = FlightGlobals.Vessels == null ? null : FlightGlobals.Vessels.SingleOrDefault(x => x != null && x.id == selectedVesselId);
                        if (HighLogic.CurrentGame == reloaded && reloadedRemote != null && reloadedRemote.loaded && !reloadedRemote.packed && reloadedRemote.parts != null) break;
                        yield return null;
                    }
                    if (HighLogic.CurrentGame != reloaded || reloadedRemote == null || !reloadedRemote.loaded || reloadedRemote.packed || reloadedRemote.parts == null)
                        throw new TimeoutException("Selected saved game did not restore the exact remote vessel into ready Flight.");
                    Part restoredSource = FindUniquePart(reloadedRemote, selectedPartIdA);
                    Part restoredDestination = FindUniquePart(reloadedRemote, selectedPartIdB);
                    PartResource restoredSourceFuel = restoredSource.Resources.Cast<PartResource>().Single(x => x != null && x.resourceName == resourceName);
                    PartResource restoredDestinationFuel = restoredDestination.Resources.Cast<PartResource>().Single(x => x != null && x.resourceName == resourceName);
                    Line("REMOTE_RELOAD_FLIGHT vesselId=" + reloadedRemote.id.ToString("D") + " loaded=" + reloadedRemote.loaded +
                        " packed=" + reloadedRemote.packed + " source=" + F(restoredSourceFuel.amount) + " destination=" + F(restoredDestinationFuel.amount));
                    if (restoredSourceFuel.amount != sourceBefore - 1.0 || restoredDestinationFuel.amount != destinationBefore + 1.0)
                        throw new InvalidDataException("BRP loaded restore did not retain the selected remote debit and credit.");
                    LogProbeObservations(reloadedRemote, "remote-after-loaded-reload", new[] { restoredSource, restoredDestination });
                    Line("REMOTE_TRANSFER_PASS selected unloaded transfer survived save and loaded Flight reload; visitor proto rows were unchanged at apply");
                    Application.Quit();
                    yield break;
                }
                Line("REMOTE_PROBE_PASS read-only selected identity/provider mapping and catch-up observed on loaded=false vessel; no fuel transfer was attempted");
                Application.Quit();
                yield break;
            }
            LogHarmonyFlightSaveHookAvailability();

            if (!HighLogic.LoadedSceneIsFlight || HighLogic.CurrentGame == null || HighLogic.SaveFolder != saveFolder ||
                FlightGlobals.ActiveVessel != vessel || !vessel.loaded || vessel.packed)
                throw new InvalidOperationException("Refusing probe save outside its loaded disposable Flight context.");
            Line("SAVEGAME_BEGIN saveFolder=" + saveFolder + " saveName=" + SaveName + " flightStateType=" + HighLogic.CurrentGame.flightState.GetType().FullName + " selectedA=" + selectedPartIdA + " selectedB=" + selectedPartIdB);
            GamePersistence.SaveGame(SaveName, saveFolder, SaveMode.OVERWRITE);
            string savedPath = Path.GetFullPath(Path.Combine(root, "saves", saveFolder, SaveName + ".sfs"));
            AssertNoReparseAncestors(savedPath);
            if (!savedPath.StartsWith(Path.GetFullPath(Path.Combine(root, "saves", saveFolder)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(savedPath))
                throw new IOException("Selected disposable save file was not created at its derived path.");
            FileInfo info = new FileInfo(savedPath);
            Line("SAVEGAME_RETURN fileBytes=" + info.Length + " lastWriteUtc=" + info.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture) + " sha256=" + Sha256(savedPath));
            Dictionary<string, double> savedPartResources, savedBrpResources;
            InspectSerializedSave(savedPath, new[] { selectedPartIdA, selectedPartIdB }, out savedPartResources, out savedBrpResources);
            Line("SAVED_NODE_COMPARISON liveKspRows=" + beforeSave.Stock.Count + " savedPartRows=" + savedPartResources.Count + " exactSame=" + SameStock(beforeSave.Stock, savedPartResources) + " liveBrpRows=" + beforeSave.Brp.Count + " savedBrpRows=" + savedBrpResources.Count + " exactSame=" + SameStock(beforeSave.Brp, savedBrpResources));

            reloadLoadObserved = reloadGuiReadyObserved = reloadFlightReadyObserved = false;
            GameEvents.onGameStateLoad.Add(OnGameStateLoad);
            GameEvents.onLevelWasLoadedGUIReady.Add(OnLevelWasLoadedGuiReady);
            GameEvents.onFlightReady.Add(OnFlightReady);
            Game loaded = GamePersistence.LoadGame(SaveName, saveFolder, true, false);
            if (loaded == null) throw new InvalidOperationException("LoadGame returned null for the just-written disposable selected save.");
            HighLogic.CurrentGame = loaded;
            loaded.startScene = GameScenes.FLIGHT;
            loaded.Start();
            float reloadUntil = Time.realtimeSinceStartup + 180f;
            int stableFlightFrames = 0;
            while (Time.realtimeSinceStartup < reloadUntil)
            {
                bool ready = HighLogic.LoadedSceneIsFlight && HighLogic.CurrentGame == loaded && HighLogic.SaveFolder == saveFolder &&
                    FlightGlobals.ActiveVessel != null && FlightGlobals.ActiveVessel.loaded && !FlightGlobals.ActiveVessel.packed;
                stableFlightFrames = ready ? stableFlightFrames + 1 : 0;
                if (reloadLoadObserved && (reloadGuiReadyObserved || reloadFlightReadyObserved) && stableFlightFrames >= 2) break;
                yield return null;
            }
            GameEvents.onGameStateLoad.Remove(OnGameStateLoad);
            GameEvents.onLevelWasLoadedGUIReady.Remove(OnLevelWasLoadedGuiReady);
            GameEvents.onFlightReady.Remove(OnFlightReady);
            if (!reloadLoadObserved || (!reloadGuiReadyObserved && !reloadFlightReadyObserved) || stableFlightFrames < 2 || HighLogic.CurrentGame != loaded)
                throw new TimeoutException("Selected provider probe save did not become the exact ready Flight Game within 180 seconds.");
            Line("RELOAD_READY gameIdentity=" + System.Object.ReferenceEquals(HighLogic.CurrentGame, loaded) + " onGameStateLoad=" + reloadLoadObserved + " guiReady=" + reloadGuiReadyObserved + " flightReady=" + reloadFlightReadyObserved + " stableFlightFrames=" + stableFlightFrames + " saveFolder=" + HighLogic.SaveFolder);
            yield return WaitForVessel(30f);
            Vessel reloadedVessel = FlightGlobals.ActiveVessel;
            Part reloadedA = FindUniquePart(reloadedVessel, selectedPartIdA);
            Part reloadedB = FindUniquePart(reloadedVessel, selectedPartIdB);
            ProbeSnapshot afterReload = LogProbeObservations(reloadedVessel, "after-reload", new[] { reloadedA, reloadedB });
            bool kspSame = SameStock(beforeSave.Stock, afterReload.Stock);
            bool brpSame = SameStock(beforeSave.Brp, afterReload.Brp);
            Line("RELOAD_STOCK_COMPARISON beforeKspRows=" + beforeSave.Stock.Count + " afterKspRows=" + afterReload.Stock.Count + " kspExactSame=" + kspSame + " beforeBrpRows=" + beforeSave.Brp.Count + " afterBrpRows=" + afterReload.Brp.Count + " brpExactSame=" + brpSame + " result=observed-from-selected-saved-Game");
            if (!kspSame || !brpSame) throw new InvalidDataException("Selected KSP or BRP stock observation changed across the no-write SaveGame/reload probe; inspect before/after evidence.");
            Line("PASS probe-only no PartResource amount was changed; selected part/BRP IDs, selected-save reload and observable save lifecycle markers recorded.");
            yield return new WaitForSecondsRealtime(2f);
            Application.Quit();
        }

        private IEnumerator WaitForScene(GameScenes scene, float timeout)
        {
            float until = Time.realtimeSinceStartup + timeout;
            while (Time.realtimeSinceStartup < until)
            {
                if (HighLogic.LoadedScene == scene) yield break;
                yield return null;
            }
            throw new TimeoutException("Timed out waiting for scene " + scene + ".");
        }

        private string PrepareRemoteTransferCraft(string sourcePath)
        {
            if (!File.Exists(sourcePath) || new FileInfo(sourcePath).Length > 4 * 1024 * 1024)
                throw new InvalidDataException("Known disposable craft is missing or exceeds the source bound.");
            string variant = Path.Combine(root, "ExpanseRemoteBrpTransfer-" + token + ".craft");
            AssertNoReparseAncestors(variant);
            if (File.Exists(variant) || Directory.Exists(variant)) throw new IOException("Derived craft already exists.");
            ConfigNode craft = ConfigNode.Load(sourcePath);
            if (craft == null) throw new InvalidDataException("Cannot parse source craft.");
            int changed = 0;
            foreach (ConfigNode part in craft.GetNodes("PART"))
                foreach (ConfigNode resource in part.GetNodes("RESOURCE"))
                {
                    if (resource.GetValue("name") != "LiquidFuel") continue;
                    double amount, capacity;
                    if (!Double.TryParse(resource.GetValue("amount"), NumberStyles.Float, CultureInfo.InvariantCulture, out amount) ||
                        !Double.TryParse(resource.GetValue("maxAmount"), NumberStyles.Float, CultureInfo.InvariantCulture, out capacity) ||
                        amount < 3.0 || capacity < amount) continue;
                    resource.SetValue("amount", (amount - 2.0).ToString("R", CultureInfo.InvariantCulture));
                    changed++;
                }
            if (changed < 2) throw new InvalidDataException("Craft lacks two fuel tanks with one-unit debit/headroom after fixture edit.");
            craft.Save(variant);
            if (!File.Exists(variant) || new FileInfo(variant).Length > 4 * 1024 * 1024)
                throw new IOException("Derived craft exceeds bound or was not saved.");
            Line("REMOTE_CRAFT_VARIANT sourceSha256=" + Sha256(sourcePath) + " variantSha256=" + Sha256(variant) +
                " liquidFuelAmountsReducedByTwo=" + changed + " path=" + Bound(variant, 256));
            return variant;
        }

        private void LogRemoteProviderOwnership(Vessel vessel, uint[] persistentIds, uint[] expectedFlightIds)
        {
            if (vessel == null || vessel.loaded || vessel.protoVessel == null || vessel.protoVessel.protoPartSnapshots == null ||
                vessel.protoVessel.protoPartSnapshots.Count > MaxParts || persistentIds.Length != 2 || expectedFlightIds.Length != 2)
                throw new InvalidOperationException("Remote vessel or selected membership is invalid.");
            var selected = new Dictionary<string, ProtoPartResourceSnapshot>(StringComparer.Ordinal);
            for (int i = 0; i < persistentIds.Length; i++)
            {
                var parts = vessel.protoVessel.protoPartSnapshots.Where(x => x != null && x.persistentId == persistentIds[i]).ToArray();
                if (parts.Length != 1 || parts[0].flightID != expectedFlightIds[i])
                    throw new InvalidOperationException("Selected persistent ID no longer maps uniquely to its original flight ID.");
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (ProtoPartResourceSnapshot resource in parts[0].resources)
                {
                    if (resource == null || String.IsNullOrWhiteSpace(resource.resourceName) || !names.Add(resource.resourceName) ||
                        !FiniteStock(resource.amount, resource.maxAmount))
                        throw new InvalidOperationException("Selected proto resource is missing, duplicate or nonfinite.");
                    string key = parts[0].flightID.ToString(CultureInfo.InvariantCulture) + "\0" + resource.resourceName;
                    selected.Add(key, resource);
                    Line("REMOTE_PROTO partPersistentId=" + persistentIds[i] + " partFlightId=" + expectedFlightIds[i] +
                        " resource=" + resource.resourceName + " amount=" + F(resource.amount) + " capacity=" + F(resource.maxAmount));
                }
            }
            object[] processors = GetVesselModules(vessel).Where(x => x != null && x.GetType().FullName ==
                "BackgroundResourceProcessing.BackgroundResourceProcessor").ToArray();
            if (processors.Length != 1) throw new InvalidOperationException("Expected exactly one BRP processor on the unloaded selected vessel.");
            object processor = processors[0];
            MethodInfo catchUp = processor.GetType().GetMethod("UpdateBackgroundState", BindingFlags.Public | BindingFlags.Instance);
            if (catchUp == null || catchUp.GetParameters().Length != 0)
                throw new MissingMethodException("BRP UpdateBackgroundState is unavailable.");
            // BRP itself advances background production to the current KSP UT. This
            // is a provider-owned update; the probe never alters selected quantities.
            catchUp.Invoke(processor, null);
            if (vessel.loaded) throw new InvalidOperationException("Selected vessel loaded during BRP catch-up.");
            object collection = ReadMember(processor, "Inventories");
            if (!(collection is IEnumerable)) throw new InvalidOperationException("BRP remote inventory collection is unavailable.");
            var matched = new HashSet<string>(StringComparer.Ordinal);
            int scanned = 0;
            foreach (object inventory in (IEnumerable)collection)
            {
                if (++scanned > MaxResources || inventory == null) throw new InvalidOperationException("BRP remote inventory enumeration exceeded bound or contained null.");
                if (ReadMember(inventory, "ModuleId") != null) continue;
                uint flightId = ToUInt(ReadMember(inventory, "FlightId"));
                string name = Convert.ToString(ReadMember(inventory, "ResourceName"), CultureInfo.InvariantCulture);
                string key = flightId.ToString(CultureInfo.InvariantCulture) + "\0" + name;
                ProtoPartResourceSnapshot snapshot;
                if (!selected.TryGetValue(key, out snapshot)) continue;
                if (!matched.Add(key) || !System.Object.ReferenceEquals(ReadMember(inventory, "Snapshot"), snapshot))
                    throw new InvalidOperationException("Selected BRP inventory is duplicated or does not own the exact proto snapshot.");
                double amount = Convert.ToDouble(ReadMember(inventory, "Amount"), CultureInfo.InvariantCulture);
                double original = Convert.ToDouble(ReadMember(inventory, "OriginalAmount"), CultureInfo.InvariantCulture);
                double capacity = Convert.ToDouble(ReadMember(inventory, "MaxAmount"), CultureInfo.InvariantCulture);
                if (!FiniteStock(amount, capacity) || !FiniteStock(original, capacity) || capacity != snapshot.maxAmount ||
                    amount != snapshot.amount || original != snapshot.amount)
                    throw new InvalidOperationException("BRP amount/original, selected proto stock or capacity disagree after provider catch-up.");
                Line("REMOTE_BRP_MATCH flightId=" + flightId + " resource=" + name + " amount=" + F(amount) +
                    " original=" + F(original) + " snapshot=" + F(snapshot.amount) + " capacity=" + F(capacity));
            }
            if (matched.Count != selected.Count)
                throw new InvalidOperationException("BRP did not uniquely cover every selected proto resource; matched=" + matched.Count + " expected=" + selected.Count);
            Line("REMOTE_OWNERSHIP_COMPLETE loaded=" + vessel.loaded + " selectedParts=" + persistentIds.Length +
                " selectedResources=" + selected.Count + " brpInventoriesScanned=" + scanned + " ut=" + F(Planetarium.GetUniversalTime()));
        }

        private static bool FiniteStock(double amount, double capacity)
        { return !Double.IsNaN(amount) && !Double.IsInfinity(amount) && !Double.IsNaN(capacity) && !Double.IsInfinity(capacity) &&
            amount >= 0 && capacity >= 0 && amount <= capacity; }

        private static ProtoPartResourceSnapshot FindRemoteResource(Vessel vessel, uint persistentId, uint flightId, string resourceName)
        {
            if (vessel == null || vessel.loaded || vessel.protoVessel == null) throw new InvalidOperationException("Remote vessel is unavailable.");
            var parts = vessel.protoVessel.protoPartSnapshots.Where(x => x != null && x.persistentId == persistentId && x.flightID == flightId).ToArray();
            if (parts.Length != 1) throw new InvalidOperationException("Selected remote part no longer resolves uniquely.");
            var rows = parts[0].resources.Where(x => x != null && x.resourceName == resourceName).ToArray();
            if (rows.Length != 1 || !FiniteStock(rows[0].amount, rows[0].maxAmount))
                throw new InvalidOperationException("Selected remote resource no longer resolves uniquely.");
            return rows[0];
        }

        private static Dictionary<string, double> CaptureRemoteProtoStock(Vessel vessel)
        {
            if (vessel == null || vessel.loaded || vessel.protoVessel == null || vessel.protoVessel.protoPartSnapshots == null ||
                vessel.protoVessel.protoPartSnapshots.Count > MaxParts) throw new InvalidOperationException("Remote stock snapshot is unavailable.");
            var rows = new Dictionary<string, double>(StringComparer.Ordinal);
            int count = 0;
            foreach (ProtoPartSnapshot part in vessel.protoVessel.protoPartSnapshots)
            {
                if (part == null || part.persistentId == 0) throw new InvalidOperationException("Remote part identity is invalid.");
                foreach (ProtoPartResourceSnapshot resource in part.resources)
                {
                    if (++count > MaxResources || resource == null || !FiniteStock(resource.amount, resource.maxAmount))
                        throw new InvalidOperationException("Remote stock observation exceeds bound or contains invalid quantity.");
                    string key = part.persistentId.ToString(CultureInfo.InvariantCulture) + "\0" + resource.resourceName;
                    if (rows.ContainsKey(key)) throw new InvalidOperationException("Remote stock has duplicate member/resource identity.");
                    rows.Add(key, resource.amount);
                }
            }
            return rows;
        }

        private static void AssertOnlySelectedChanged(Dictionary<string, double> before, Dictionary<string, double> after,
            uint sourceId, uint destinationId, string resourceName)
        {
            if (before.Count != after.Count) throw new InvalidDataException("Remote resource row count changed during selected transfer.");
            string sourceKey = sourceId.ToString(CultureInfo.InvariantCulture) + "\0" + resourceName;
            string destinationKey = destinationId.ToString(CultureInfo.InvariantCulture) + "\0" + resourceName;
            foreach (var row in before)
            {
                double current;
                if (!after.TryGetValue(row.Key, out current)) throw new InvalidDataException("A remote resource row disappeared.");
                double expected = row.Value + (row.Key == sourceKey ? -1.0 : row.Key == destinationKey ? 1.0 : 0.0);
                if (current != expected) throw new InvalidDataException("Selected or visitor row changed by an unexpected amount: " + row.Key.Replace('\0', '/'));
            }
        }

        private void SelectSavedActiveVessel(Game selectedGame, Guid expectedId)
        {
            if (selectedGame == null || selectedGame.flightState == null) throw new InvalidOperationException("Selected Game has no FlightState.");
            object state = selectedGame.flightState;
            IEnumerable vessels = ReadMember(state, "protoVessels") as IEnumerable;
            if (vessels == null) throw new InvalidOperationException("Selected FlightState has no proto vessel list.");
            int index = 0, found = -1;
            foreach (object vessel in vessels)
            {
                if (vessel == null || index >= 16384) throw new InvalidOperationException("Saved proto vessel list is invalid or exceeds bound.");
                object value = ReadMember(vessel, "vesselID");
                Guid candidate = value is Guid ? (Guid)value : Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture));
                if (candidate == expectedId)
                {
                    if (found >= 0) throw new InvalidOperationException("Saved depot vessel GUID appears more than once.");
                    found = index;
                }
                index++;
            }
            if (found < 0) throw new InvalidOperationException("Selected saved FlightState lacks the exact depot vessel GUID.");
            // KSP persists this field under the ConfigNode key `activeVessel`.
            FieldInfo active = state.GetType().GetField("activeVesselIdx", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (active == null || active.FieldType != typeof(int)) throw new MissingFieldException(state.GetType().FullName, "activeVesselIdx");
            active.SetValue(state, found);
            Line("REMOTE_RELOAD_SELECTED_VESSEL vesselId=" + expectedId.ToString("D") + " activeIndex=" + found + " savedVessels=" + index);
        }

        private IEnumerator WaitForVessel(float timeout)
        {
            float until = Time.realtimeSinceStartup + timeout;
            while (Time.realtimeSinceStartup < until)
            {
                Vessel v = FlightGlobals.ActiveVessel;
                if (HighLogic.LoadedSceneIsFlight && HighLogic.CurrentGame != null && HighLogic.SaveFolder == saveFolder && v != null && v.loaded && !v.packed && v.parts != null)
                    yield break;
                yield return null;
            }
            throw new TimeoutException("No loaded, unpacked active Flight vessel became available.");
        }

        private Part[] ChooseDeterministicResourcePair(Vessel vessel)
        {
            List<Part> parts = vessel.parts.Where(p => p != null && p.persistentId != 0 && p.Resources != null && p.Resources.Count > 0).OrderBy(p => p.persistentId).ToList();
            for (int i = 0; i < parts.Count; i++)
                for (int j = i + 1; j < parts.Count; j++)
                {
                    HashSet<string> names = new HashSet<string>(parts[i].Resources.Cast<PartResource>().Where(r => r != null).Select(r => r.resourceName), StringComparer.Ordinal);
                    string shared = parts[j].Resources.Cast<PartResource>().Where(r => r != null && names.Contains(r.resourceName)).Select(r => r.resourceName).OrderByDescending(n => n == "LiquidFuel" || n == "Oxidizer").ThenBy(n => n, StringComparer.Ordinal).FirstOrDefault();
                    if (!String.IsNullOrEmpty(shared))
                    {
                        Line("SELECTED_PAIR_RULE pair=ascending-persistentId sharedResource=" + shared);
                        return new[] { parts[i], parts[j] };
                    }
                }
            throw new InvalidOperationException("Muna 1 Flight vessel did not contain two distinct resource parts sharing a resource.");
        }

        private static Part FindUniquePart(Vessel vessel, uint id)
        {
            List<Part> matches = vessel.parts.Where(p => p != null && p.persistentId == id).ToList();
            if (matches.Count != 1) throw new InvalidOperationException("Expected exactly one selected Part.persistentId=" + id + ", found " + matches.Count + ".");
            return matches[0];
        }

        private void LogPart(Part part, string role)
        {
            Line("PART role=" + role + " persistentId=" + part.persistentId.ToString(CultureInfo.InvariantCulture) + " flightID=" + part.flightID.ToString(CultureInfo.InvariantCulture) + " title=" + Bound(part.partInfo == null ? part.name : part.partInfo.title, 128) + " resources=" + (part.Resources == null ? 0 : part.Resources.Count));
        }

        private sealed class ProbeSnapshot
        {
            public Dictionary<string, double> Stock = new Dictionary<string, double>(StringComparer.Ordinal);
            public Dictionary<string, double> Brp = new Dictionary<string, double>(StringComparer.Ordinal);
        }

        private ProbeSnapshot LogProbeObservations(Vessel vessel, string phase, Part[] selected)
        {
            LogPart(selected[0], phase + "-selected-A");
            LogPart(selected[1], phase + "-selected-B");
            LogAllResources(vessel, phase);
            Dictionary<string, double> provider = LogBrpInventories(vessel, selected, phase, out lastBrpInventoryCount);
            return new ProbeSnapshot { Stock = CaptureSelectedStock(selected, phase), Brp = provider };
        }

        private void LogAllResources(Vessel vessel, string phase)
        {
            int count = 0;
            foreach (Part part in vessel.parts)
            {
                if (part == null || part.Resources == null) continue;
                foreach (PartResource resource in part.Resources)
                {
                    if (++count > MaxResources) throw new InvalidOperationException("Vessel resource observation exceeds 4096 row bound.");
                    Line("KSP_RESOURCE phase=" + phase + " partPersistentId=" + part.persistentId.ToString(CultureInfo.InvariantCulture) + " partFlightID=" + part.flightID.ToString(CultureInfo.InvariantCulture) + " name=" + Bound(resource.resourceName, 128) + " amount=" + F(resource.amount) + " capacity=" + F(resource.maxAmount) + " flowState=" + resource.flowState);
                }
            }
            Line("KSP_RESOURCE_COUNT rows=" + count);
        }

        private Dictionary<string, double> LogBrpInventories(Vessel vessel, Part[] selected, string phase, out int inventoryCount)
        {
            List<object> processors = GetVesselModules(vessel).Where(module => module != null && module.GetType().FullName == "BackgroundResourceProcessing.BackgroundResourceProcessor").ToList();
            Line("BRP_PROCESSOR_COUNT phase=" + phase + " count=" + processors.Count);
            int rowCount = 0;
            Dictionary<string, double> amounts = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (object processor in processors)
            {
                object collection = ReadMember(processor, "Inventories");
                if (!(collection is IEnumerable)) throw new InvalidOperationException("BRP Inventories is not enumerable.");
                foreach (object inventory in (IEnumerable)collection)
                {
                    if (inventory == null) continue;
                    if (++rowCount > MaxResources) throw new InvalidOperationException("BRP inventory observation exceeds 4096 row bound.");
                    uint flightId = ToUInt(ReadMember(inventory, "FlightId"));
                    object moduleId = ReadMember(inventory, "ModuleId");
                    string resourceName = Convert.ToString(ReadMember(inventory, "ResourceName"), CultureInfo.InvariantCulture);
                    object amountValue = ReadMember(inventory, "Amount");
                    object originalValue = ReadMember(inventory, "OriginalAmount");
                    object capacityValue = ReadMember(inventory, "MaxAmount");
                    object availableValue = ReadMember(inventory, "Available");
                    object snapshot = ReadMember(inventory, "Snapshot");
                    string snapshotAmount = "unavailable";
                    if (snapshot != null) { object snapAmount; if (TryReadMember(snapshot, "amount", out snapAmount) || TryReadMember(snapshot, "Amount", out snapAmount)) snapshotAmount = Convert.ToString(snapAmount, CultureInfo.InvariantCulture); }
                    foreach (Part part in selected)
                    {
                        bool byPersistent = flightId == part.persistentId;
                        bool byFlight = flightId == part.flightID;
                        if (byPersistent || byFlight)
                        {
                            bool partResource = part.Resources.Cast<PartResource>().Any(r => r != null && r.resourceName == resourceName);
                            Line("BRP_MATCH phase=" + phase + " partPersistentId=" + part.persistentId.ToString(CultureInfo.InvariantCulture) + " partFlightID=" + part.flightID.ToString(CultureInfo.InvariantCulture) + " inventoryFlightId=" + flightId.ToString(CultureInfo.InvariantCulture) + " resource=" + Bound(resourceName, 128) + " matchPersistentId=" + byPersistent + " matchPartFlightID=" + byFlight + " partHasResource=" + partResource);
                        }
                    }
                    string moduleText = Convert.ToString(moduleId, CultureInfo.InvariantCulture);
                    double amount = Convert.ToDouble(amountValue, CultureInfo.InvariantCulture);
                    double original = Convert.ToDouble(originalValue, CultureInfo.InvariantCulture);
                    Line("BRP_INVENTORY phase=" + phase + " flightId=" + flightId.ToString(CultureInfo.InvariantCulture) + " moduleId=" + (moduleId == null ? "null" : moduleText) + " resource=" + Bound(resourceName, 128) + " amount=" + F(amount) + " originalAmount=" + F(original) + " maxAmount=" + F(Convert.ToDouble(capacityValue, CultureInfo.InvariantCulture)) + " available=" + Convert.ToString(availableValue, CultureInfo.InvariantCulture) + " snapshot=" + (snapshot == null ? "null" : snapshot.GetType().FullName) + " snapshotAmount=" + snapshotAmount);
                    string inventoryKey = InventoryKey(flightId, moduleText, resourceName);
                    amounts.Add(inventoryKey + "\0amount", amount);
                    amounts.Add(inventoryKey + "\0originalAmount", original);
                    if (snapshot != null && Double.TryParse(snapshotAmount, NumberStyles.Float, CultureInfo.InvariantCulture, out double snapshotNumeric)) amounts.Add(inventoryKey + "\0snapshotAmount", snapshotNumeric);
                }
            }
            Line("BRP_INVENTORY_COUNT phase=" + phase + " count=" + rowCount + " mapping=compare-inventory-FlightId-to-both-Part-persistentId-and-Part-flightID");
            inventoryCount = rowCount;
            return amounts;
        }

        private static List<object> GetVesselModules(Vessel vessel)
        {
            object source = ReadMember(vessel, "vesselModules");
            IEnumerable sequence = source as IEnumerable;
            if (sequence == null) throw new InvalidOperationException("Vessel.vesselModules is not enumerable.");
            List<object> result = new List<object>();
            foreach (object item in sequence) if (item != null) result.Add(item);
            return result;
        }

        private static string InventoryKey(uint flightId, string moduleId, string resource)
        { return flightId.ToString(CultureInfo.InvariantCulture) + "\0" + moduleId + "\0" + resource; }

        private void InspectSerializedSave(string path, uint[] selectedIds, out Dictionary<string, double> partAmounts, out Dictionary<string, double> brpAmounts)
        {
            ConfigNode rootNode = ConfigNode.Load(path);
            if (rootNode == null) throw new InvalidDataException("ConfigNode.Load could not read the saved probe file.");
            HashSet<uint> selected = new HashSet<uint>(selectedIds);
            partAmounts = new Dictionary<string, double>(StringComparer.Ordinal);
            brpAmounts = new Dictionary<string, double>(StringComparer.Ordinal);
            InspectNodes(rootNode, "root", selected, partAmounts, brpAmounts, 0);
            foreach (KeyValuePair<string, double> row in partAmounts) Line("SAVED_PART_RESOURCE key=" + row.Key.Replace('\0', '/') + " amount=" + F(row.Value));
            foreach (KeyValuePair<string, double> row in brpAmounts) Line("SAVED_BRP_INVENTORY key=" + row.Key.Replace('\0', '/') + " amount=" + F(row.Value));
        }

        private void InspectNodes(ConfigNode node, string path, HashSet<uint> selected, Dictionary<string, double> partAmounts, Dictionary<string, double> brpAmounts, int depth)
        {
            if (node == null || depth > 64) return;
            if (node.name == "PART")
            {
                uint id;
                if (UInt32.TryParse(node.GetValue("persistentId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out id) && selected.Contains(id))
                {
                    foreach (ConfigNode resource in node.GetNodes("RESOURCE"))
                    {
                        string name = resource.GetValue("name");
                        double amount;
                        if (!String.IsNullOrEmpty(name) && Double.TryParse(resource.GetValue("amount"), NumberStyles.Float, CultureInfo.InvariantCulture, out amount))
                            partAmounts.Add(id.ToString(CultureInfo.InvariantCulture) + "\0" + name, amount);
                    }
                }
            }
            if (node.name.IndexOf("BackgroundResourceProcessor", StringComparison.OrdinalIgnoreCase) >= 0 || HasValue(node, "FlightId") || HasValue(node, "flightId"))
            {
                string flightId = FirstValue(node, "FlightId", "flightId");
                string moduleId = FirstValue(node, "ModuleId", "moduleId");
                string resource = FirstValue(node, "ResourceName", "resourceName", "name");
                    string amountText = FirstValue(node, "Amount", "amount");
                    double amount;
                    if (!String.IsNullOrEmpty(flightId) && !String.IsNullOrEmpty(resource) && Double.TryParse(amountText, NumberStyles.Float, CultureInfo.InvariantCulture, out amount))
                    {
                    string key = flightId + "\0" + (moduleId ?? "") + "\0" + resource;
                    if (!brpAmounts.ContainsKey(key + "\0amount")) brpAmounts.Add(key + "\0amount", amount);
                    string originalText = FirstValue(node, "OriginalAmount", "originalAmount"); double original;
                    if (Double.TryParse(originalText, NumberStyles.Float, CultureInfo.InvariantCulture, out original) && !brpAmounts.ContainsKey(key + "\0originalAmount")) brpAmounts.Add(key + "\0originalAmount", original);
                    Line("SAVED_PROVIDER_NODE path=" + Bound(path + "/" + node.name, 256) + " flightId=" + flightId + " moduleId=" + (moduleId ?? "null") + " resource=" + resource + " amount=" + F(amount) + " originalAmount=" + (Double.TryParse(originalText, NumberStyles.Float, CultureInfo.InvariantCulture, out original) ? F(original) : "unavailable"));
                }
            }
            ConfigNode[] children = node.GetNodes();
            for (int i = 0; i < children.Length; i++) InspectNodes(children[i], path + "/" + node.name + "[" + i.ToString(CultureInfo.InvariantCulture) + "]", selected, partAmounts, brpAmounts, depth + 1);
        }

        private static bool HasValue(ConfigNode node, string name) { return node.HasValue(name); }
        private static string FirstValue(ConfigNode node, params string[] names)
        { foreach (string name in names) if (node.HasValue(name)) return node.GetValue(name); return null; }

        private Dictionary<string, double> CaptureSelectedStock(Part[] selected, string phase)
        {
            Dictionary<string, double> result = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (Part part in selected)
                foreach (PartResource resource in part.Resources)
                {
                    string key = part.persistentId.ToString(CultureInfo.InvariantCulture) + "\0" + resource.resourceName;
                    if (result.ContainsKey(key)) throw new InvalidDataException("Duplicate resource name on selected part makes identity ambiguous.");
                    result.Add(key, resource.amount);
                    Line("SELECTED_STOCK phase=" + phase + " partPersistentId=" + part.persistentId.ToString(CultureInfo.InvariantCulture) + " partFlightID=" + part.flightID.ToString(CultureInfo.InvariantCulture) + " resource=" + Bound(resource.resourceName, 128) + " amount=" + F(resource.amount) + " capacity=" + F(resource.maxAmount));
                }
            return result;
        }

        private static bool SameStock(Dictionary<string, double> before, Dictionary<string, double> after)
        {
            if (before.Count != after.Count) return false;
            foreach (KeyValuePair<string, double> row in before)
            {
                double value;
                if (!after.TryGetValue(row.Key, out value) || BitConverter.DoubleToInt64Bits(value) != BitConverter.DoubleToInt64Bits(row.Value)) return false;
            }
            return true;
        }

        private void LogHarmonyFlightSaveHookAvailability()
        {
            Type harmony = Type.GetType("HarmonyLib.Harmony, 0Harmony", false);
            Type state = typeof(FlightState);
            MethodInfo[] saves = state.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static).Where(m => m.Name == "Save").ToArray();
            Line("FLIGHT_SAVE_METHODS type=" + state.FullName + " methods=" + saves.Length + " harmonyLoaded=" + (harmony != null));
            foreach (MethodInfo method in saves) Line("FLIGHT_SAVE_METHOD signature=" + method.ToString());
            // KSP's observed save contract has no public FlightState.Save event. The fixture
            // reports methods but deliberately does not patch/instrument provider save code.
        }

        private void SubscribeSaveEvents()
        {
            GameEvents.onGameStateSave.Add(OnGameStateSave);
            GameEvents.onGameStateSaved.Add(OnGameStateSaved);
            subscribed = true;
        }

        private void OnGameStateSave(ConfigNode node) { Line("EVENT onGameStateSave node=" + (node == null ? "null" : node.name) + " observedAfter-Scenario-and-FlightState-serialization=true"); }
        private void OnGameStateSaved(Game game) { Line("EVENT onGameStateSaved game=" + (game == null ? "null" : game.GetType().Name) + " observedBefore-ConfigNode.Save=true"); }
        private void OnGameStateLoad(ConfigNode ignored) { reloadLoadObserved = true; Line("EVENT onGameStateLoad saveFolder=" + HighLogic.SaveFolder); }
        private void OnLevelWasLoadedGuiReady(GameScenes scene) { if (scene == GameScenes.FLIGHT) { reloadGuiReadyObserved = true; Line("EVENT onLevelWasLoadedGUIReady scene=" + scene); } }
        private void OnFlightReady() { if (HighLogic.LoadedSceneIsFlight) { reloadFlightReadyObserved = true; Line("EVENT onFlightReady scene=FLIGHT"); } }

        internal static void LogScenarioSave(string name) { Action<string> log = ProviderProbeScenario.Log; if (log != null) log(name); }

        private static object ReadMember(object instance, string name)
        {
            if (instance == null) throw new InvalidOperationException("Cannot read " + name + " from null provider object.");
            Type type = instance.GetType();
            FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null) return field.GetValue(instance);
            PropertyInfo property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (property != null) return property.GetValue(instance, null);
            throw new MissingMemberException(type.FullName, name);
        }

        private static bool TryReadMember(object instance, string name, out object value)
        {
            value = null;
            if (instance == null) return false;
            Type type = instance.GetType();
            FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null) { value = field.GetValue(instance); return true; }
            PropertyInfo property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (property != null) { value = property.GetValue(instance, null); return true; }
            return false;
        }

        private static uint ToUInt(object value)
        { return Convert.ToUInt32(value, CultureInfo.InvariantCulture); }
        private static string Sha256(string path)
        { using (var sha = System.Security.Cryptography.SHA256.Create()) using (var stream = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", String.Empty); }
        private static string F(double value) { return value.ToString("R", CultureInfo.InvariantCulture); }
        private static string Bound(string value, int max) { if (String.IsNullOrEmpty(value)) return ""; value = value.Replace('\r', ' ').Replace('\n', ' '); return value.Length <= max ? value : value.Substring(0, max); }

        private static void AssertNoReparseAncestors(string path)
        {
            string cursor = Path.GetFullPath(path);
            string volume = Path.GetPathRoot(cursor);
            while (!String.IsNullOrEmpty(cursor))
            {
                try
                {
                    if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0) throw new IOException("Refusing reparse-point path: " + cursor);
                }
                catch (FileNotFoundException) { /* The final output file may not exist yet; keep checking its ancestors. */ }
                catch (DirectoryNotFoundException) { /* A derived output directory may not exist yet; keep checking its ancestors. */ }
                if (String.Equals(cursor, volume, StringComparison.OrdinalIgnoreCase)) break;
                cursor = Path.GetDirectoryName(cursor);
            }
        }

        private void Line(string text)
        {
            try
            {
                string line = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " " + Bound(text, 2048);
                File.AppendAllText(logPath, line + Environment.NewLine);
                Debug.Log("[ExpanseProviderProbe] " + Bound(text, 256));
            }
            catch { }
        }

        private void OnDestroy()
        {
            if (subscribed)
            {
                GameEvents.onGameStateSave.Remove(OnGameStateSave);
                GameEvents.onGameStateSaved.Remove(OnGameStateSaved);
                GameEvents.onGameStateLoad.Remove(OnGameStateLoad);
                GameEvents.onLevelWasLoadedGUIReady.Remove(OnLevelWasLoadedGuiReady);
                GameEvents.onFlightReady.Remove(OnFlightReady);
                subscribed = false;
            }
            if (started) ProviderProbeScenario.Log = null;
        }
    }

    [KSPScenario(ScenarioCreationOptions.AddToAllGames, GameScenes.FLIGHT, GameScenes.SPACECENTER)]
    public sealed class ProviderProbeScenario : ScenarioModule
    {
        internal static Action<string> Log;
        public override void OnLoad(ConfigNode node)
        {
            base.OnLoad(node);
            ProviderProbeAddon.LogScenarioSave("SCENARIO OnLoad node=" + (node == null ? "null" : node.name));
        }
        public override void OnSave(ConfigNode node)
        {
            base.OnSave(node);
            ProviderProbeAddon.LogScenarioSave("SCENARIO OnSave node=" + (node == null ? "null" : node.name));
        }
    }
}
