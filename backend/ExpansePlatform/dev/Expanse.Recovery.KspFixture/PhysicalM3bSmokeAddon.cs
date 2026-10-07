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
    // One-shot dev-only vertical test. Host submits routeUpsert/sendOnce through its
    // isolated command pipe after READY; this fixture observes the accepted capsule,
    // unload transition, disk witness, and selected-save reload. Never edits production.
    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public sealed class PhysicalM3bSmokeAddon : MonoBehaviour
    {
        const string RequiredRoot = @"C:\Users\griff\Documents\KSP-RMM-Dev";
        const string RequestName = "ExpansePhysical3bSmoke.request";
        const string RequestPrefix = "RUN_EXPANSE_PHYSICAL_3B=";
        const string ScheduleRequestName = "ExpanseScheduleM5Smoke.request";
        const string ScheduleRequestPrefix = "RUN_EXPANSE_SCHEDULE_M5=";
        string SaveName = "physical-3b-smoke";
        string root, token, folder, logPath;
        bool scheduleMode;
        bool started;
        float startedAt;
        bool loadObserved, guiReadyObserved, flightReadyObserved;
        string saveWorldId;
        double beforeUt;
        string dispatchShipmentId;
        double dispatchDueUt;
        long dispatchSequence;
        double fixtureSourceBaseline, fixtureDestinationBaseline;

        void Start()
        {
            bool accepted = false;
            try
            {
                root = Path.GetFullPath(KSPUtil.ApplicationRootPath).TrimEnd('\\', '/');
                if (!String.Equals(root, RequiredRoot, StringComparison.OrdinalIgnoreCase)) return;
                NoReparse(root);
                string requestPath = Path.Combine(root, RequestName);
                string prefix = RequestPrefix;
                if (!File.Exists(requestPath))
                {
                    requestPath = Path.Combine(root, ScheduleRequestName);
                    prefix = ScheduleRequestPrefix;
                    scheduleMode = true;
                }
                if (!File.Exists(requestPath) || (File.GetAttributes(requestPath) & FileAttributes.ReparsePoint) != 0) return;
                NoReparse(requestPath);
                if (new FileInfo(requestPath).Length > 128) throw new InvalidDataException("Physical fixture request exceeds bound");
                string request = File.ReadAllText(requestPath).Trim();
                Guid parsed;
                if (!request.StartsWith(prefix, StringComparison.Ordinal) || request.Length != prefix.Length + 32 || !Guid.TryParseExact(request.Substring(prefix.Length), "N", out parsed) || parsed == Guid.Empty) return;
                token = parsed.ToString("N");
                folder = (scheduleMode ? "ExpanseScheduleM5Smoke-" : "ExpansePhysical3bSmoke-") + token;
                if (scheduleMode) SaveName = "schedule-m5-smoke";
                logPath = Path.Combine(root, folder + ".log");
                string savesRoot = Path.GetFullPath(Path.Combine(root, "saves")); NoReparse(savesRoot);
                string savePath = Path.GetFullPath(Path.Combine(savesRoot, folder));
                if (!savePath.StartsWith(savesRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || Directory.Exists(savePath) || File.Exists(savePath) || File.Exists(logPath) || Directory.Exists(logPath)) throw new IOException("Derived disposable path exists or escapes dev root");
                string craft = Path.GetFullPath(Path.Combine(root, "GameData", "SquadExpansion", "MakingHistory", "Ships", "VAB", "Muna 1.craft"));
                if (!craft.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(craft)) throw new FileNotFoundException("Stock Muna 1 craft is missing", craft);
                if (!WorldBridgeAddon.ActivatePhysicalFixture(token)) throw new InvalidOperationException("Bridge rejected physical fixture token or install root");
                File.Delete(requestPath); // one-shot; no user-provided paths are accepted
                File.WriteAllText(logPath, "START utc=" + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " token=" + token + Environment.NewLine);
                AudioListener.volume = 0f; DontDestroyOnLoad(gameObject); started = accepted = true; startedAt = Time.realtimeSinceStartup;
                StartCoroutine(Supervise(Run(craft)));
            }
            catch (Exception ex) { if (accepted && logPath != null) Line("FAIL initialize " + Bound(ex.ToString(), 1000)); if (accepted) Application.Quit(); }
        }

        IEnumerator Supervise(IEnumerator body)
        {
            float deadline = Time.realtimeSinceStartup + 600f;
            Stack<IEnumerator> stack = new Stack<IEnumerator>(); stack.Push(body);
            while (Time.realtimeSinceStartup < deadline && Time.realtimeSinceStartup - startedAt < 600f)
            {
                if (stack.Count == 0) yield break;
                bool more; object current;
                try { more = stack.Peek().MoveNext(); current = more ? stack.Peek().Current : null; if (!more) { stack.Pop(); continue; } }
                catch (Exception ex) { Line("FAIL " + Bound(ex.ToString(), 2000)); Application.Quit(); yield break; }
                IEnumerator nested = current as IEnumerator;
                if (nested != null) stack.Push(nested); else yield return current;
            }
            Line("FAIL global watchdog 600s"); Application.Quit();
        }

        IEnumerator Run(string craft)
        {
            Line("REQUEST accepted token=" + token + " saveFolder=" + folder + " saveName=" + SaveName + " candidate=BRP-PartResource-Amount-OriginalAmount snapshot-untouched");
            craft = PrepareVariantCraft(craft, scheduleMode ? 400.0 : 403.0);
            yield return new WaitForSecondsRealtime(2f);
            HighLogic.SaveFolder = folder;
            HighLogic.CurrentGame = GamePersistence.CreateNewGame(folder, Game.Modes.SANDBOX, new GameParameters(), "Squad/Flags/default", GameScenes.SPACECENTER, EditorFacility.VAB);
            if (HighLogic.CurrentGame == null) throw new InvalidOperationException("CreateNewGame returned null");
            HighLogic.CurrentGame.Start();
            yield return WaitForScene(GameScenes.SPACECENTER, 120f);
            yield return WaitForSpaceCenterReady(folder, 90f);
            FlightDriver.StartWithNewLaunch(craft, "Squad/Flags/default", "LaunchPad", new VesselCrewManifest());
            yield return WaitForScene(GameScenes.FLIGHT, 180f);
            yield return WaitForFlight(folder, 120f);
            WorldBridgeAddon bridge = FindObjectOfType<WorldBridgeAddon>();
            DepotRegistryModule registry = DepotRegistryModule.Instance;
            if (bridge == null || registry == null || !bridge.CanAcceptPhysicalEffects) throw new InvalidOperationException("Bridge physical fixture is not ready in loaded Flight");
            Vessel vessel = FlightGlobals.ActiveVessel;
            Part[] pair = SelectPair(vessel, variantResourceName);
            Part source = pair[0], destination = pair[1];
            PartResource sourceResource = source.Resources.Cast<PartResource>().Single(x => x != null && x.resourceName == variantResourceName);
            PartResource destinationResource = destination.Resources.Cast<PartResource>().Single(x => x != null && x.resourceName == variantResourceName);
            if (sourceResource.amount < 1.0 || sourceResource.amount - 1.0 >= sourceResource.amount || destinationResource.amount >= destinationResource.maxAmount)
                throw new InvalidOperationException("Deterministic selected pair lacks a one-unit source or destination headroom; no resource was changed");
            if (destinationResource.resourceName != variantResourceName || Math.Abs(destinationResource.amount - variantDestinationAmount) > 1e-9)
                throw new InvalidDataException("Loaded destination does not match the exact resource amount edited in the derived craft variant");
            if (!registry.Register("M3b source " + token.Substring(0, 8), source.persistentId, new[] { source.persistentId }) || !registry.Register("M3b destination " + token.Substring(0, 8), destination.persistentId, new[] { destination.persistentId }))
                throw new InvalidOperationException("Could not register exact disposable source/destination parts");
            string[] depotIds = registry.RegisteredDepotIds;
            if (depotIds.Length != 2) throw new InvalidOperationException("Registry did not expose exactly two explicit fixture depots");
            saveWorldId = registry.WorldId;
            yield return WaitForState(saveWorldId, 2, 90f);
            AcceptedState state = ReadState();
            bridge = FindObjectOfType<WorldBridgeAddon>();
            beforeUt = Planetarium.GetUniversalTime();
            ProviderTriple sourceBefore = ReadTriple(source, sourceResource);
            ProviderTriple destinationBefore = ReadTriple(destination, destinationResource);
            Line("READY token=" + token + " worldId=" + state.WorldId + " runId=" + bridge.CurrentRunId + " sessionId=" + bridge.CurrentSessionId + " loadEpoch=" + bridge.CurrentLoadEpoch +
                " sequence=" + state.AcceptedSequence + " revision=" + state.Revision + " sourceDepotId=" + depotIds[0] + " destinationDepotId=" + depotIds[1] +
                " sourcePersistentId=" + source.persistentId + " sourceFlightId=" + source.flightID + " destinationPersistentId=" + destination.persistentId + " destinationFlightId=" + destination.flightID +
                " resource=" + sourceResource.resourceName + " sourceBefore=" + F(sourceResource.amount) + " sourceCapacity=" + F(sourceResource.maxAmount) + " destinationBefore=" + F(destinationResource.amount) + " destinationCapacity=" + F(destinationResource.maxAmount) + " routeDurationSeconds=reported-from-accepted-shipment " + "sourceProvider=" + F(sourceBefore.Amount) + "/" + F(sourceBefore.Original) + "/" + F(sourceBefore.Snapshot) +
                " destinationProvider=" + F(destinationBefore.Amount) + "/" + F(destinationBefore.Original) + "/" + F(destinationBefore.Snapshot) + " ut=" + F(beforeUt) + " saveFolder=" + folder);
            fixtureSourceBaseline = sourceBefore.Part;
            fixtureDestinationBaseline = destinationBefore.Part;
            if (scheduleMode)
            {
                Line("SCHEDULE_READY token=" + token + " worldId=" + state.WorldId + " runId=" + bridge.CurrentRunId + " sourceDepotId=" + depotIds[0] + " destinationDepotId=" + depotIds[1] +
                    " sourcePersistentId=" + source.persistentId + " destinationPersistentId=" + destination.persistentId + " resource=" + variantResourceName +
                    " sourceAmount=" + F(sourceBefore.Part) + " destinationAmount=" + F(destinationBefore.Part) + " destinationCapacity=" + F(destinationResource.maxAmount) +
                    " ut=" + F(beforeUt) + " suggestedKeepStock=low401000000,target405000000,batch1000000 suggestedRepeatNextDueUt=" + F(beforeUt + 10.0) +
                    " suggestedRepeatIntervalSeconds=20 suggestedRouteDurationSeconds=120 vesselIds=" + VesselIds());
                yield return RunScheduleScenario(bridge, state, source, sourceResource, destination, destinationResource, 540f);
                yield break;
            }
            yield return WaitForDispatch(180f);

            try { FlightDriver.SetPause(true, false); } catch { }
            float pauseDeadline = Time.realtimeSinceStartup + 5f;
            while (!FlightDriver.Pause && !Planetarium.Pause && Time.realtimeSinceStartup < pauseDeadline) yield return new WaitForSecondsRealtime(0.1f);
            if (!FlightDriver.Pause && !Planetarium.Pause) throw new InvalidOperationException("Could not pause disposable Flight immediately after dispatch; refusing to risk due arrival before save/reload");
            state = ReadState();
            bridge = FindObjectOfType<WorldBridgeAddon>();
            ProviderTriple sourceAfter = ReadTriple(source, sourceResource);
            Line("APPLIED token=" + token + " worldId=" + state.WorldId + " runId=" + bridge.CurrentRunId + " sequence=" + state.AcceptedSequence + " revision=" + state.Revision +
                " activeShipments=" + state.ActiveShipments.Length + " sourcePart=" + F(sourceResource.amount) + " sourceProvider=" + F(sourceAfter.Amount) + "/" + F(sourceAfter.Original) + "/" + F(sourceAfter.Snapshot) +
                " capsuleHash=" + AcceptedStateCodec.ComputeHash(state) + " receiptOperation=" + state.Receipts.Last().OperationId + " ut=" + F(Planetarium.GetUniversalTime()) +
                " flightDriverPause=" + FlightDriver.Pause + " planetariumPause=" + Planetarium.Pause + " routeDurationSeconds=" + F(state.ActiveShipments[0].DueUt - state.ActiveShipments[0].DepartureUt));
            if (!state.ActiveShipments.Any(x => !x.LegacyOpaque)) throw new InvalidOperationException("Dispatch receipt settled without an active shipment");
            ActiveShipmentRecord dispatchShipment = state.ActiveShipments.Single(x => !x.LegacyOpaque);
            dispatchShipmentId = dispatchShipment.ShipmentId; dispatchDueUt = dispatchShipment.DueUt; dispatchSequence = state.AcceptedSequence;
            if (Math.Abs(sourceResource.amount - (sourceBefore.Part - 1.0)) > 1e-9 || sourceAfter.Amount != sourceResource.amount || sourceAfter.Original != sourceResource.amount)
                throw new InvalidOperationException("Applied dispatch did not produce exact one-unit triple-field source debit");

            // Force the provider transition before any explicit selected-save reload.
            Line("UNLOAD_REQUEST vesselId=" + vessel.id.ToString("D") + " loaded=" + vessel.loaded + " snapshotBefore=" + F(sourceAfter.Snapshot));
            InvokeNoArg(vessel, "GoOnRails");
            float unloadDeadline = Time.realtimeSinceStartup + 20f;
            while (!vessel.packed && Time.realtimeSinceStartup < unloadDeadline) yield return new WaitForSecondsRealtime(0.1f);
            if (!vessel.packed) throw new InvalidOperationException("KSP did not pack the fixture vessel via GoOnRails");
            ProviderTriple unloaded = ReadTriple(source, sourceResource);
            state = ReadState();
            Line("PACKED_TRANSITION token=" + token + " loaded=" + vessel.loaded + " packed=" + vessel.packed + " classification=" + (vessel.loaded ? "packedLoadedOnly" : "unloaded") + " partAmount=" + F(sourceResource.amount) + " providerAmount=" + F(unloaded.Amount) + " originalAmount=" + F(unloaded.Original) +
                " snapshotAmount=" + F(unloaded.Snapshot) + " capsuleSequence=" + state.AcceptedSequence + " capsuleHash=" + AcceptedStateCodec.ComputeHash(state));
            GamePersistence.SaveGame(SaveName, folder, SaveMode.OVERWRITE);
            string savedPath = Path.GetFullPath(Path.Combine(root, "saves", folder, SaveName + ".sfs"));
            NoReparse(savedPath);
            if (!savedPath.StartsWith(Path.GetFullPath(Path.Combine(root, "saves", folder)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(savedPath)) throw new IOException("Selected physical fixture save is absent or outside derived save folder");
            LogSavedSelectedRows(savedPath, source, sourceResource, destination, destinationResource, state);
            Line("SAVED path=" + savedPath + " sha256=" + Sha256(savedPath) + " bytes=" + new FileInfo(savedPath).Length + " capsuleSequence=" + state.AcceptedSequence + " capsuleHash=" + AcceptedStateCodec.ComputeHash(state));
            yield return ReloadSelected(bridge, state, 180f);
            yield return WaitForArrivalAndVerify(vessel == null ? null : vessel.name, 300f);
            Line("PASS token=" + token + " mode=dev-only-physical-candidate persistenceGate=NOT_CLEARED; immediate-unload evidence above must be reviewed");
            Application.Quit();
        }

        IEnumerator WaitForArrivalAndVerify(string vesselNameBeforeReload, float timeout)
        {
            WorldBridgeAddon bridge = FindObjectOfType<WorldBridgeAddon>();
            AcceptedState state = ReadState();
            if (bridge == null || state == null || String.IsNullOrEmpty(dispatchShipmentId) || dispatchDueUt <= 0 || state.AcceptedSequence != dispatchSequence ||
                !state.ActiveShipments.Any(x => x.ShipmentId == dispatchShipmentId))
                throw new InvalidOperationException("Cannot begin due-arrival check without the exact accepted in-transit shipment");
            if (!HighLogic.LoadedSceneIsFlight || HighLogic.CurrentGame == null || FlightGlobals.ActiveVessel == null || !FlightGlobals.ActiveVessel.loaded || FlightGlobals.ActiveVessel.packed)
                throw new InvalidOperationException("Due-arrival check requires the selected loaded, unpacked Flight vessel");
            double utAtReload = Planetarium.GetUniversalTime();
            if (utAtReload > dispatchDueUt) throw new InvalidDataException("Selected reload UT is already beyond shipment due UT; cannot observe transit-before-arrival boundary safely");
            TimeWarp.SetRate(0, true);
            FlightDriver.SetPause(false, false);
            float unpauseEnd = Time.realtimeSinceStartup + 5f;
            while ((FlightDriver.Pause || Planetarium.Pause) && Time.realtimeSinceStartup < unpauseEnd) yield return new WaitForSecondsRealtime(0.1f);
            if (FlightDriver.Pause || Planetarium.Pause) throw new TimeoutException("KSP did not return to unpaused 1x after selected reload");
            Line("ARRIVAL_WAIT token=" + token + " shipmentId=" + dispatchShipmentId + " dueUt=" + F(dispatchDueUt) + " reloadUt=" + F(utAtReload) +
                " routeDurationSeconds=" + F(dispatchDueUt - state.ActiveShipments.Single(x => x.ShipmentId == dispatchShipmentId).DepartureUt) +
                " warpIndex=" + TimeWarp.CurrentRateIndex + " pause=" + FlightDriver.Pause + "/" + Planetarium.Pause + " vesselName=" + Bound(FlightGlobals.ActiveVessel.vesselName, 96) +
                " priorVesselName=" + Bound(vesselNameBeforeReload, 96) + " vesselCount=" + FlightGlobals.Vessels.Count);
            float end = Time.realtimeSinceStartup + timeout;
            float nextHeartbeat = Time.realtimeSinceStartup + 10f;
            double ut = utAtReload;
            AcceptedReceipt arrival = null;
            while (Time.realtimeSinceStartup < end)
            {
                bridge = FindObjectOfType<WorldBridgeAddon>();
                state = ReadState();
                try { ut = Planetarium.GetUniversalTime(); } catch { }
                if (state != null && state.WritesBlocked) throw new InvalidOperationException("Arrival physical fault blocked writes: " + Bound(state.Faults.Last().Reason, 256));
                if (state != null)
                    arrival = state.Receipts.Where(x => x.OperationKind == "arrival" && x.Outcome == "accepted" && x.CommandSequence > dispatchSequence && x.AppliedUt >= dispatchDueUt)
                        .OrderByDescending(x => x.CommandSequence).FirstOrDefault();
                if (arrival != null && state != null && !state.ActiveShipments.Any(x => x.ShipmentId == dispatchShipmentId)) break;
                if (Time.realtimeSinceStartup >= nextHeartbeat)
                {
                    Vessel v = FlightGlobals.ActiveVessel;
                    Line("ARRIVAL_PENDING token=" + token + " shipmentId=" + dispatchShipmentId + " dueUt=" + F(dispatchDueUt) + " currentUt=" + F(ut) +
                        " remainingUt=" + F(Math.Max(0, dispatchDueUt - ut)) + " acceptedSequence=" + (state == null ? "missing" : state.AcceptedSequence.ToString(CultureInfo.InvariantCulture)) +
                        " activeShipment=" + (state != null && state.ActiveShipments.Any(x => x.ShipmentId == dispatchShipmentId)) + " bridgeRunId=" + (bridge == null ? "missing" : bridge.CurrentRunId) +
                        " warpIndex=" + TimeWarp.CurrentRateIndex + " pause=" + FlightDriver.Pause + "/" + Planetarium.Pause + " vesselLoaded=" + (v != null && v.loaded) + " vesselPacked=" + (v != null && v.packed));
                    nextHeartbeat = Time.realtimeSinceStartup + 10f;
                }
                yield return new WaitForSecondsRealtime(0.25f);
            }
            bridge = FindObjectOfType<WorldBridgeAddon>(); state = ReadState();
            if (arrival == null || state == null || state.ActiveShipments.Any(x => x.ShipmentId == dispatchShipmentId))
            {
                Line("ARRIVAL_TIMEOUT token=" + token + " shipmentId=" + dispatchShipmentId + " dueUt=" + F(dispatchDueUt) + " currentUt=" + F(ut) +
                    " receipt=" + (arrival == null ? "missing" : arrival.Outcome) + " acceptedSequence=" + (state == null ? "missing" : state.AcceptedSequence.ToString(CultureInfo.InvariantCulture)) +
                    " active=" + (state != null && state.ActiveShipments.Any(x => x.ShipmentId == dispatchShipmentId)) + " writesBlocked=" + (state != null && state.WritesBlocked) +
                    " workerDiagnostic=" + (bridge == null ? "missing" : Bound(bridge.LastWorkerDiagnostic, 384)));
                throw new TimeoutException("Host did not settle the due arrival within " + timeout + " real seconds; in-transit save remains preserved");
            }
            Vessel vessel = FlightGlobals.ActiveVessel;
            Part source = FindPart(vessel, savedSourceId), destination = FindPart(vessel, savedDestinationId);
            ProviderTriple sourceRow = ReadTriple(source, FindResource(source, savedResource));
            ProviderTriple destinationRow = ReadTriple(destination, FindResource(destination, savedResource));
            Line("ARRIVAL_APPLIED token=" + token + " shipmentId=" + dispatchShipmentId + " scheduledDueUt=" + F(dispatchDueUt) + " actualApplyUt=" + F(arrival.AppliedUt) +
                " sequence=" + state.AcceptedSequence + " revision=" + state.Revision + " capsuleHash=" + AcceptedStateCodec.ComputeHash(state) + " activeShipmentRemaining=" + state.ActiveShipments.Any(x => x.ShipmentId == dispatchShipmentId) +
                " sourcePersistentId=" + source.persistentId + " sourcePart=" + F(sourceRow.Part) + " sourceAmount=" + F(sourceRow.Amount) + " sourceOriginal=" + F(sourceRow.Original) +
                " destinationPersistentId=" + destination.persistentId + " destinationPart=" + F(destinationRow.Part) + " destinationAmount=" + F(destinationRow.Amount) + " destinationOriginal=" + F(destinationRow.Original) +
                " warpIndex=" + TimeWarp.CurrentRateIndex + " vesselCount=" + FlightGlobals.Vessels.Count);
            if (Math.Abs(sourceRow.Part - savedSourceAmount) > 1e-9 || sourceRow.Amount != sourceRow.Part || sourceRow.Original != sourceRow.Part ||
                Math.Abs(destinationRow.Part - (savedDestinationAmount + 1.0)) > 1e-9 || destinationRow.Amount != destinationRow.Part || destinationRow.Original != destinationRow.Part || state.ActiveShipments.Length != 0)
                throw new InvalidDataException("Accepted due arrival did not credit the exact one-unit destination row, preserve source, and clear transit cargo");

            try { FlightDriver.SetPause(true, false); } catch { }
            float pauseEnd = Time.realtimeSinceStartup + 5f;
            while (!FlightDriver.Pause && !Planetarium.Pause && Time.realtimeSinceStartup < pauseEnd) yield return new WaitForSecondsRealtime(0.1f);
            if (!FlightDriver.Pause && !Planetarium.Pause) throw new TimeoutException("Could not pause after accepted arrival for its exact selected-save witness");
            GamePersistence.SaveGame(SaveName, folder, SaveMode.OVERWRITE);
            string savedPath = Path.GetFullPath(Path.Combine(root, "saves", folder, SaveName + ".sfs"));
            NoReparse(savedPath);
            savedSourceAmount = sourceRow.Part; savedDestinationAmount = destinationRow.Part;
            LogSavedSelectedRows(savedPath, source, FindResource(source, savedResource), destination, FindResource(destination, savedResource), state);
            Line("ARRIVAL_SAVED path=" + savedPath + " sha256=" + Sha256(savedPath) + " sequence=" + state.AcceptedSequence + " capsuleHash=" + AcceptedStateCodec.ComputeHash(state) +
                " source=" + F(savedSourceAmount) + " destination=" + F(savedDestinationAmount));
            yield return ReloadSelected(bridge, state, 180f);
            Line("ARRIVAL_RELOADED token=" + token + " worldId=" + state.WorldId + " sequence=" + state.AcceptedSequence + " source=" + F(savedSourceAmount) + " destination=" + F(savedDestinationAmount) + " shipmentCleared=true");
        }

        IEnumerator RunScheduleScenario(WorldBridgeAddon bridge, AcceptedState readyState, Part source, PartResource sourceResource,
            Part destination, PartResource destinationResource, float timeout)
        {
            string[] vesselIdsBefore = VesselIdsArray();
            Line("SCHEDULE_CONFIG_WAIT token=" + token + " acceptedSequence=" + readyState.AcceptedSequence + " vesselIds=" + String.Join(",", vesselIdsBefore));
            float end = Time.realtimeSinceStartup + 120f;
            AcceptedState state = readyState;
            while (Time.realtimeSinceStartup < end)
            {
                state = ReadState();
                if (state != null && state.WritesBlocked) throw new InvalidOperationException("Scheduled physical fault blocked writes: " + Bound(state.Faults.Last().Reason, 256));
                DeliveryRuleRecord keep = state == null ? null : state.DeliveryRules.SingleOrDefault(x => x.RuleId == "a-stock");
                DeliveryRuleRecord repeat = state == null ? null : state.DeliveryRules.SingleOrDefault(x => x.RuleId == "z-repeat");
                ActiveShipmentRecord[] shipments = state == null ? new ActiveShipmentRecord[0] : state.ActiveShipments.Where(x => !x.LegacyOpaque).OrderBy(x => x.DepartureUt).ToArray();
                long dispatches = state == null ? 0 : state.Receipts.LongCount(x => x.OperationKind == "dispatch" && x.Outcome == "accepted");
                if (keep != null && repeat != null && keep.Revision >= 2 && repeat.Revision >= 2 && shipments.Length == 3 && dispatches >= 3)
                    break;
                yield return new WaitForSecondsRealtime(0.25f);
            }
            if (state == null || state.DeliveryRules.SingleOrDefault(x => x.RuleId == "a-stock") == null || state.DeliveryRules.SingleOrDefault(x => x.RuleId == "z-repeat") == null || state.ActiveShipments.Count(x => !x.LegacyOpaque) != 3)
                throw new TimeoutException("Host did not accept one keep-stock and two repeat dispatches within the schedule fixture bound");
            ActiveShipmentRecord[] shipmentsAtDispatch = state.ActiveShipments.Where(x => !x.LegacyOpaque).OrderBy(x => x.DepartureUt).ToArray();
            double firstDue = shipmentsAtDispatch.Min(x => x.DueUt);
            double currentUt = Planetarium.GetUniversalTime();
            if (currentUt >= firstDue) throw new TimeoutException("Three scheduled dispatches settled too close to due UT to preserve an in-transit save gate");
            AcceptedReceipt[] sourceReceipts = state.Receipts.Where(x => x.OperationKind == "dispatch" && x.Outcome == "accepted" && x.PhysicalWitness != null)
                .OrderBy(x => x.CommandSequence).ToArray();
            if (sourceReceipts.Length < 3 || sourceReceipts.Skip(sourceReceipts.Length - 3).Any(x => x.PhysicalWitness.Rows.Length != 1 ||
                x.PhysicalWitness.Rows[0].MemberPersistentId != source.persistentId || x.PhysicalWitness.Rows[0].ResourceName != sourceResource.resourceName ||
                x.PhysicalWitness.Rows[0].BeforeAmount - x.PhysicalWitness.Rows[0].IntendedAfterAmount != 1.0 ||
                x.PhysicalWitness.Rows[0].ObservedAfterAmount != x.PhysicalWitness.Rows[0].IntendedAfterAmount))
                throw new InvalidDataException("The latest three dispatch witnesses do not prove one-unit debits from the same source member/resource");
            ProviderTriple sourceDispatch = ReadTriple(source, sourceResource), destinationDispatch = ReadTriple(destination, destinationResource);
            string[] idsAfterDispatch = VesselIdsArray();
            Line("SCHEDULE_DISPATCHED token=" + token + " worldId=" + state.WorldId + " runId=" + (FindObjectOfType<WorldBridgeAddon>() == null ? "missing" : FindObjectOfType<WorldBridgeAddon>().CurrentRunId) +
                " sequence=" + state.AcceptedSequence + " revision=" + state.Revision + " capsuleHash=" + AcceptedStateCodec.ComputeHash(state) + " dispatchReceipts=" + state.Receipts.LongCount(x => x.OperationKind == "dispatch" && x.Outcome == "accepted") +
                " keepStockRevision=" + state.DeliveryRules.Single(x => x.RuleId == "a-stock").Revision + " repeatRevision=" + state.DeliveryRules.Single(x => x.RuleId == "z-repeat").Revision +
                " repeatNextDueUt=" + F(state.DeliveryRules.Single(x => x.RuleId == "z-repeat").NextDueUt) + " repeatCoalescedSlots=" + state.DeliveryRules.Single(x => x.RuleId == "z-repeat").WaitingCoalescedSlots +
                " shipmentIds=" + String.Join(",", shipmentsAtDispatch.Select(x => x.ShipmentId).ToArray()) + " departureDue=" + String.Join(",", shipmentsAtDispatch.Select(x => F(x.DepartureUt) + "/" + F(x.DueUt)).ToArray()) +
                " currentUt=" + F(currentUt) + " sourcePart=" + F(sourceDispatch.Part) + " sourceBRP=" + F(sourceDispatch.Amount) + "/" + F(sourceDispatch.Original) +
                " destinationPart=" + F(destinationDispatch.Part) + " destinationBRP=" + F(destinationDispatch.Amount) + "/" + F(destinationDispatch.Original) +
                " sameSourceWrites=" + shipmentsAtDispatch.Length + " sourceMember=" + source.persistentId + " resource=" + sourceResource.resourceName +
                " vesselIdsBefore=" + String.Join(",", vesselIdsBefore) + " vesselIdsAfter=" + String.Join(",", idsAfterDispatch));
            if (vesselIdsBefore.Except(idsAfterDispatch).Any() || idsAfterDispatch.Except(vesselIdsBefore).Any())
                Line("VESSEL_IDENTITY_CHANGE window=scheduledDispatch classification=diagnosticOnly before=" + DescribeVessels(vesselIdsBefore) + " after=" + DescribeVessels(idsAfterDispatch));
            if (Math.Abs(sourceDispatch.Part - (fixtureSourceBaseline - 3.0)) > 1e-9 || sourceDispatch.Amount != sourceDispatch.Part || sourceDispatch.Original != sourceDispatch.Part ||
                Math.Abs(destinationDispatch.Part - fixtureDestinationBaseline) > 1e-9 || destinationDispatch.Amount != destinationDispatch.Part || destinationDispatch.Original != destinationDispatch.Part)
                throw new InvalidDataException("One keep-stock plus two repeat dispatches did not debit exactly three units from the same source while destination remained unchanged in transit");

            try { FlightDriver.SetPause(true, false); } catch { }
            float pauseEnd = Time.realtimeSinceStartup + 5f;
            while (!FlightDriver.Pause && !Planetarium.Pause && Time.realtimeSinceStartup < pauseEnd) yield return new WaitForSecondsRealtime(0.1f);
            if (!FlightDriver.Pause && !Planetarium.Pause) throw new TimeoutException("Could not pause scheduled Flight before saving in-transit capsules");
            if (Planetarium.GetUniversalTime() >= firstDue) throw new TimeoutException("First scheduled shipment became due before in-transit save");
            Line("SCHEDULE_DISABLE_READY token=" + token + " worldId=" + state.WorldId + " runId=" + (FindObjectOfType<WorldBridgeAddon>() == null ? "missing" : FindObjectOfType<WorldBridgeAddon>().CurrentRunId) +
                " acceptedSequence=" + state.AcceptedSequence + " activeShipments=3 keepRuleId=a-stock repeatRuleId=z-repeat paused=" + FlightDriver.Pause + "/" + Planetarium.Pause +
                " ut=" + F(Planetarium.GetUniversalTime()) + " command=rules-disable");
            end = Time.realtimeSinceStartup + 90f;
            while (Time.realtimeSinceStartup < end)
            {
                state = ReadState();
                DeliveryRuleRecord keep = state == null ? null : state.DeliveryRules.SingleOrDefault(x => x.RuleId == "a-stock");
                DeliveryRuleRecord repeat = state == null ? null : state.DeliveryRules.SingleOrDefault(x => x.RuleId == "z-repeat");
                if (keep != null && repeat != null && !keep.Enabled && !repeat.Enabled && keep.Revision >= 3 && repeat.Revision >= 3 && state.ActiveShipments.Length == 3) break;
                yield return new WaitForSecondsRealtime(0.25f);
            }
            if (state == null || state.DeliveryRules.Single(x => x.RuleId == "a-stock").Enabled || state.DeliveryRules.Single(x => x.RuleId == "z-repeat").Enabled || state.ActiveShipments.Length != 3)
                throw new TimeoutException("Host did not accept both scheduled rule pauses while preserving transit shipments");
            Line("SCHEDULE_RULES_DISABLED token=" + token + " sequence=" + state.AcceptedSequence + " keepRevision=" + state.DeliveryRules.Single(x => x.RuleId == "a-stock").Revision +
                " repeatRevision=" + state.DeliveryRules.Single(x => x.RuleId == "z-repeat").Revision + " enabled=false/false activeShipments=" + state.ActiveShipments.Length +
                " ut=" + F(Planetarium.GetUniversalTime()) + " capsuleHash=" + AcceptedStateCodec.ComputeHash(state));
            GamePersistence.SaveGame(SaveName, folder, SaveMode.OVERWRITE);
            string savedPath = Path.GetFullPath(Path.Combine(root, "saves", folder, SaveName + ".sfs"));
            NoReparse(savedPath);
            LogSavedSelectedRows(savedPath, source, sourceResource, destination, destinationResource, state);
            Line("SCHEDULE_TRANSIT_SAVED path=" + savedPath + " sha256=" + Sha256(savedPath) + " sequence=" + state.AcceptedSequence + " capsuleHash=" + AcceptedStateCodec.ComputeHash(state) +
                " activeShipmentCount=" + state.ActiveShipments.Length + " ut=" + F(Planetarium.GetUniversalTime()));
            yield return ReloadSelected(bridge, state, 180f);
            source = FindPart(FlightGlobals.ActiveVessel, savedSourceId); destination = FindPart(FlightGlobals.ActiveVessel, savedDestinationId);
            sourceResource = FindResource(source, savedResource); destinationResource = FindResource(destination, savedResource);
            if (Math.Abs(Planetarium.GetUniversalTime() - currentUt) > 1000.0) Line("SCHEDULE_RELOAD_UT drifted from dispatch sample savedUT=" + F(currentUt) + " actual=" + F(Planetarium.GetUniversalTime()));
            TimeWarp.SetRate(0, true);
            FlightDriver.SetPause(false, false);
            float unpauseEnd = Time.realtimeSinceStartup + 5f;
            while ((FlightDriver.Pause || Planetarium.Pause) && Time.realtimeSinceStartup < unpauseEnd) yield return new WaitForSecondsRealtime(0.1f);
            if (FlightDriver.Pause || Planetarium.Pause) throw new TimeoutException("Could not unpause loaded Flight after scheduled transit reload");
            double reloadUt = Planetarium.GetUniversalTime();
            if (reloadUt >= firstDue) throw new InvalidDataException("Reloaded Flight is already past first scheduled due UT; refusing to manufacture a transit interval");
            Line("SCHEDULE_ARRIVAL_WAIT token=" + token + " shipmentIds=" + String.Join(",", shipmentsAtDispatch.Select(x => x.ShipmentId).ToArray()) +
                " firstDueUt=" + F(firstDue) + " reloadUt=" + F(reloadUt) + " ruleIntervalSeconds=20 vesselIds=" + VesselIds());
            end = Time.realtimeSinceStartup + timeout;
            float nextLog = Time.realtimeSinceStartup + 10f;
            AcceptedState finalState = null;
            long lastObservedSequence = state.AcceptedSequence;
            string[] lastObservedVessels = VesselIdsArray();
            while (Time.realtimeSinceStartup < end)
            {
                finalState = ReadState();
                if (finalState != null && finalState.WritesBlocked) throw new InvalidOperationException("Scheduled arrival fault blocked writes: " + Bound(finalState.Faults.Last().Reason, 256));
                string[] vesselsNow = VesselIdsArray();
                if (finalState != null && finalState.AcceptedSequence != lastObservedSequence)
                {
                    Line("SCHEDULE_EFFECT_WINDOW sequenceBefore=" + lastObservedSequence + " sequenceAfter=" + finalState.AcceptedSequence + " ut=" + F(Planetarium.GetUniversalTime()) +
                        " priorVessels=" + DescribeVessels(lastObservedVessels) + " currentVessels=" + DescribeVessels(vesselsNow) +
                        " addedIds=" + String.Join(",", vesselsNow.Except(lastObservedVessels).ToArray()) + " removedIds=" + String.Join(",", lastObservedVessels.Except(vesselsNow).ToArray()));
                    lastObservedSequence = finalState.AcceptedSequence;
                    lastObservedVessels = vesselsNow;
                }
                long arrivals = finalState == null ? 0 : finalState.Receipts.LongCount(x => x.OperationKind == "arrival" && x.Outcome == "accepted" && x.CommandSequence > state.AcceptedSequence);
                if (finalState != null && arrivals >= 3 && finalState.ActiveShipments.Length == 0) break;
                if (Time.realtimeSinceStartup >= nextLog)
                {
                    Line("SCHEDULE_ARRIVAL_PENDING token=" + token + " ut=" + F(Planetarium.GetUniversalTime()) + " acceptedSequence=" + (finalState == null ? "missing" : finalState.AcceptedSequence.ToString(CultureInfo.InvariantCulture)) +
                        " arrivals=" + arrivals + " activeShipments=" + (finalState == null ? -1 : finalState.ActiveShipments.Length) + " vesselIds=" + VesselIds() +
                        " source=" + F(sourceResource.amount) + " destination=" + F(destinationResource.amount) + " pause=" + FlightDriver.Pause + "/" + Planetarium.Pause + " warp=" + TimeWarp.CurrentRateIndex);
                    nextLog = Time.realtimeSinceStartup + 10f;
                }
                yield return new WaitForSecondsRealtime(0.25f);
            }
            finalState = ReadState();
            if (finalState == null || finalState.ActiveShipments.Length != 0 || finalState.Receipts.LongCount(x => x.OperationKind == "arrival" && x.Outcome == "accepted" && x.CommandSequence > state.AcceptedSequence) < 3)
                throw new TimeoutException("All three due schedule shipments did not settle inside the bounded loaded-Flight window");
            ProviderTriple sourceFinal = ReadTriple(source, sourceResource), destinationFinal = ReadTriple(destination, destinationResource);
            string[] idsAfterArrival = VesselIdsArray();
            Line("SCHEDULE_ARRIVALS_APPLIED token=" + token + " sequence=" + finalState.AcceptedSequence + " revision=" + finalState.Revision + " capsuleHash=" + AcceptedStateCodec.ComputeHash(finalState) +
                " acceptedArrivalReceipts=" + finalState.Receipts.LongCount(x => x.OperationKind == "arrival" && x.Outcome == "accepted") + " sourcePersistentId=" + source.persistentId + " source=" + F(sourceFinal.Part) + "/" + F(sourceFinal.Amount) + "/" + F(sourceFinal.Original) +
                " destinationPersistentId=" + destination.persistentId + " destination=" + F(destinationFinal.Part) + "/" + F(destinationFinal.Amount) + "/" + F(destinationFinal.Original) +
                " vesselIdsBefore=" + String.Join(",", vesselIdsBefore) + " vesselIdsAfter=" + String.Join(",", idsAfterArrival) + " currentUt=" + F(Planetarium.GetUniversalTime()));
            if (vesselIdsBefore.Except(idsAfterArrival).Any() || idsAfterArrival.Except(vesselIdsBefore).Any())
                Line("VESSEL_IDENTITY_CHANGE window=scheduledArrival classification=diagnosticOnly before=" + DescribeVessels(vesselIdsBefore) + " after=" + DescribeVessels(idsAfterArrival));
            if (Math.Abs(sourceFinal.Part - (fixtureSourceBaseline - 3.0)) > 1e-9 || sourceFinal.Amount != sourceFinal.Part || sourceFinal.Original != sourceFinal.Part ||
                Math.Abs(destinationFinal.Part - (fixtureDestinationBaseline + 3.0)) > 1e-9 || destinationFinal.Amount != destinationFinal.Part || destinationFinal.Original != destinationFinal.Part)
                throw new InvalidDataException("Keep-stock/repeat scheduled arrivals failed exact three-unit loaded provider conservation");
            try { FlightDriver.SetPause(true, false); } catch { }
            pauseEnd = Time.realtimeSinceStartup + 5f;
            while (!FlightDriver.Pause && !Planetarium.Pause && Time.realtimeSinceStartup < pauseEnd) yield return new WaitForSecondsRealtime(0.1f);
            if (!FlightDriver.Pause && !Planetarium.Pause) throw new TimeoutException("Could not pause after schedule arrivals for final persistence witness");
            GamePersistence.SaveGame(SaveName, folder, SaveMode.OVERWRITE);
            savedPath = Path.GetFullPath(Path.Combine(root, "saves", folder, SaveName + ".sfs"));
            LogSavedSelectedRows(savedPath, source, sourceResource, destination, destinationResource, finalState);
            Line("SCHEDULE_FINAL_SAVED path=" + savedPath + " sha256=" + Sha256(savedPath) + " sequence=" + finalState.AcceptedSequence + " capsuleHash=" + AcceptedStateCodec.ComputeHash(finalState) +
                " source=" + F(sourceFinal.Part) + " destination=" + F(destinationFinal.Part));
            yield return ReloadSelected(FindObjectOfType<WorldBridgeAddon>(), finalState, 180f);
            Line("SCHEDULE_PASS token=" + token + " scope=loaded-active-vessel keepStock=one-batch-below-low-trigger repeat=two-due-slots routeDuration=120 noSendOnce=true shipmentCount=3 arrivals=3 trueUnloadedRemote=NOT_TESTED vesselIdentityDiffs=logged-per-effect-window");
            Application.Quit();
        }

        static string[] VesselIdsArray()
        {
            try { return FlightGlobals.Vessels.Where(x => x != null).Select(x => x.id.ToString("D")).OrderBy(x => x, StringComparer.Ordinal).ToArray(); }
            catch { return new string[0]; }
        }
        static string VesselIds() { return String.Join(",", VesselIdsArray()); }
        static string DescribeVessels(IEnumerable<string> ids)
        {
            try
            {
                HashSet<string> selected = new HashSet<string>(ids, StringComparer.Ordinal);
                return String.Join(";", FlightGlobals.Vessels.Where(v => v != null && selected.Contains(v.id.ToString("D")))
                    .OrderBy(v => v.id.ToString("D"), StringComparer.Ordinal)
                    .Select(v => v.id.ToString("D") + ":" + Bound(v.vesselName, 48) + ":" + v.vesselType.ToString()).ToArray());
            }
            catch (Exception ex) { return "unavailable:" + ex.GetType().Name; }
        }

        IEnumerator WaitForDispatch(float timeout)
        {
            float end = Time.realtimeSinceStartup + timeout;
            while (Time.realtimeSinceStartup < end)
            {
                AcceptedState state = ReadState();
                if (state != null && state.ActiveShipments.Length > 0 && state.Receipts.Any(x => x.OperationKind == "dispatch" && x.Outcome == "accepted")) yield break;
                if (state != null && state.WritesBlocked) throw new InvalidOperationException("Bridge accepted a physical fault instead of a dispatch: " + state.Faults.Last().Reason);
                yield return new WaitForSecondsRealtime(0.25f);
            }
            throw new TimeoutException("Host did not settle one sendOnce dispatch within " + timeout + " seconds");
        }

        IEnumerator WaitForState(string world, int depotCount, float timeout)
        {
            float end = Time.realtimeSinceStartup + timeout;
            while (Time.realtimeSinceStartup < end)
            {
                AcceptedState state = ReadState();
                if (state != null && state.WorldId == world && state.SchemaVersion == 2 && state.Depots.Count(x => x.Active) == depotCount) yield break;
                yield return new WaitForSecondsRealtime(0.25f);
            }
            WorldBridgeAddon bridge = FindObjectOfType<WorldBridgeAddon>();
            DepotRegistryModule registry = DepotRegistryModule.Instance;
            RecoveryCapsuleModule recovery = RecoveryCapsuleModule.Instance;
            AcceptedState currentState = ReadState();
            object clockSample = bridge == null ? null : ReadMemberOrNull(bridge, "latest");
            object effectContext = bridge == null ? null : ReadMemberOrNull(bridge, "latestEffectContext");
            Line("WAIT_SYNC_TIMEOUT expectedWorld=" + world + " expectedDepots=" + depotCount + " scene=" + HighLogic.LoadedScene + " saveFolder=" + Bound(HighLogic.SaveFolder, 128) +
                " bridgeRunId=" + (bridge == null ? "missing" : bridge.CurrentRunId) + " bridgeLoadEpoch=" + (bridge == null ? "missing" : bridge.CurrentLoadEpoch) +
                " bridgeSessionId=" + (bridge == null ? "missing" : bridge.CurrentSessionId) + " loadUnresolved=" + (bridge == null ? "n/a" : bridge.IsLoadUnresolved.ToString()) +
                " canAcceptPhysicalEffects=" + (bridge == null ? "n/a" : bridge.CanAcceptPhysicalEffects.ToString()) +
                " registry=" + (registry == null ? "missing" : "present") + " registryReady=" + (registry == null ? "n/a" : registry.IsReady.ToString()) +
                " registryCorrupt=" + (registry == null ? "n/a" : registry.IsCorrupt.ToString()) + " registryWorld=" + (registry == null ? "n/a" : V(registry.WorldId)) +
                " registryCount=" + (registry == null ? -1 : registry.RegisteredDepotCount) + " registryIds=" + (registry == null ? "n/a" : String.Join(",", registry.RegisteredDepotIds)) +
                " registryHash=" + RegistryHash(registry) + " recovery=" + (recovery == null ? "missing" : "present") + " recoveryLoaded=" + (recovery == null ? "n/a" : recovery.IsLoaded.ToString()) +
                " recoveryCorrupt=" + (recovery == null ? "n/a" : recovery.IsCorrupt.ToString()) + " recoveryWorld=" + (recovery == null ? "n/a" : V(recovery.WorldId)) +
                " recoverySequence=" + (currentState == null ? "n/a" : currentState.AcceptedSequence.ToString(CultureInfo.InvariantCulture)) + " recoveryRevision=" + (currentState == null ? "n/a" : currentState.Revision.ToString(CultureInfo.InvariantCulture)) +
                " recoveryHash=" + (currentState == null ? "n/a" : AcceptedStateCodec.ComputeHash(currentState)) + " acceptedRegistryHash=" + (currentState == null ? "n/a" : V(currentState.DepotRegistryHash)) +
                " clockContext=" + DescribeContext(clockSample) + " effectsContext=" + DescribeContext(effectContext) +
                " workerDiagnostic=" + (bridge == null ? "missing" : Bound(bridge.LastWorkerDiagnostic, 384)) + " workerLog=" + (bridge == null ? "missing" : Bound(bridge.WorkerDiagnosticLogPath, 256)));
            throw new TimeoutException("Host did not sync exactly " + depotCount + " registered depots into the accepted capsule");
        }

        static object ReadMemberOrNull(object instance, string name)
        {
            if (instance == null) return null;
            FieldInfo field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null) return field.GetValue(instance);
            PropertyInfo property = instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return property == null ? null : property.GetValue(instance, null);
        }
        static string DescribeContext(object context)
        {
            if (context == null) return "missing";
            return "run=" + V(Convert.ToString(ReadMemberOrNull(context, "RunId"), CultureInfo.InvariantCulture)) + ",session=" + V(Convert.ToString(ReadMemberOrNull(context, "SessionId"), CultureInfo.InvariantCulture)) +
                ",epoch=" + V(Convert.ToString(ReadMemberOrNull(context, "LoadEpoch"), CultureInfo.InvariantCulture)) + ",world=" + V(Convert.ToString(ReadMemberOrNull(context, "WorldId"), CultureInfo.InvariantCulture)) +
                ",seq=" + Convert.ToString(ReadMemberOrNull(context, "Sequence"), CultureInfo.InvariantCulture) + ",revision=" + Convert.ToString(ReadMemberOrNull(context, "Revision"), CultureInfo.InvariantCulture) +
                ",hash=" + V(Convert.ToString(ReadMemberOrNull(context, "StateHash"), CultureInfo.InvariantCulture)) +
                ",registryHash=" + V(Convert.ToString(ReadMemberOrNull(ReadMemberOrNull(context, "RegistrySnapshot"), "RegistryHash"), CultureInfo.InvariantCulture)) +
                ",install=" + V(Convert.ToString(ReadMemberOrNull(context, "InstallNamespace"), CultureInfo.InvariantCulture)) +
                ",save=" + V(Convert.ToString(ReadMemberOrNull(context, "SaveFolder"), CultureInfo.InvariantCulture)) + ",canWrite=" + Convert.ToString(ReadMemberOrNull(context, "CanWrite"), CultureInfo.InvariantCulture);
        }
        static string RegistryHash(DepotRegistryModule registry)
        {
            if (registry == null) return "missing";
            try
            {
                MethodInfo method = typeof(DepotRegistryModule).GetMethod("CreateEffectRegistrySnapshot", BindingFlags.Instance | BindingFlags.NonPublic);
                object snapshot = method == null ? null : method.Invoke(registry, null);
                return V(Convert.ToString(ReadMemberOrNull(snapshot, "RegistryHash"), CultureInfo.InvariantCulture));
            }
            catch (Exception ex) { return "unavailable:" + ex.GetType().Name; }
        }

        IEnumerator ReloadSelected(WorldBridgeAddon bridge, AcceptedState before, float timeout)
        {
            if (!vesselLoaded() && FlightGlobals.ActiveVessel != null && !FlightGlobals.ActiveVessel.packed)
                throw new InvalidOperationException("Unexpected vessel state before selected save reload");
            string previousRunId = bridge.CurrentRunId, previousEpoch = bridge.CurrentLoadEpoch, previousSession = bridge.CurrentSessionId;
            loadObserved = guiReadyObserved = flightReadyObserved = false;
            GameEvents.onGameStateLoad.Add(OnLoad); GameEvents.onLevelWasLoadedGUIReady.Add(OnGuiReady); GameEvents.onFlightReady.Add(OnFlightReady);
            Game loaded = GamePersistence.LoadGame(SaveName, folder, true, false);
            if (loaded == null) throw new InvalidOperationException("LoadGame returned null for physical selected save");
            HighLogic.CurrentGame = loaded; loaded.startScene = GameScenes.FLIGHT; loaded.Start();
            float end = Time.realtimeSinceStartup + timeout;
            int stable = 0;
            while (Time.realtimeSinceStartup < end)
            {
                AcceptedState current = ReadState();
                WorldBridgeAddon currentBridge = FindObjectOfType<WorldBridgeAddon>();
                bool active = HighLogic.LoadedSceneIsFlight && HighLogic.CurrentGame == loaded && FlightGlobals.ActiveVessel != null && FlightGlobals.ActiveVessel.loaded;
                bool loadAndReady = loadObserved && (guiReadyObserved || flightReadyObserved);
                bool freshFence = currentBridge != null && currentBridge.CurrentRunId != previousRunId && currentBridge.CurrentLoadEpoch != previousEpoch && currentBridge.CurrentSessionId == previousSession;
                bool exactCapsule = current != null && current.WorldId == before.WorldId && current.AcceptedSequence == before.AcceptedSequence && AcceptedStateCodec.ComputeHash(current) == AcceptedStateCodec.ComputeHash(before);
                stable = active && loadAndReady && freshFence && exactCapsule ? stable + 1 : 0;
                if (stable >= 5) break;
                yield return new WaitForSecondsRealtime(0.25f);
            }
            GameEvents.onGameStateLoad.Remove(OnLoad); GameEvents.onLevelWasLoadedGUIReady.Remove(OnGuiReady); GameEvents.onFlightReady.Remove(OnFlightReady);
            AcceptedState after = ReadState();
            WorldBridgeAddon finalBridge = FindObjectOfType<WorldBridgeAddon>();
            Vessel activeVessel = FlightGlobals.ActiveVessel;
            bool finalActive = HighLogic.LoadedSceneIsFlight && HighLogic.CurrentGame == loaded && activeVessel != null && activeVessel.loaded;
            bool finalReadyEvents = loadObserved && (guiReadyObserved || flightReadyObserved);
            bool finalFreshFence = finalBridge != null && finalBridge.CurrentRunId != previousRunId && finalBridge.CurrentLoadEpoch != previousEpoch && finalBridge.CurrentSessionId == previousSession;
            bool finalExactCapsule = after != null && after.WorldId == before.WorldId && after.AcceptedSequence == before.AcceptedSequence && AcceptedStateCodec.ComputeHash(after) == AcceptedStateCodec.ComputeHash(before);
            Line("RELOAD_GATE token=" + token + " stableFrames=" + stable + " scene=" + HighLogic.LoadedScene + " expectedGame=" + (HighLogic.CurrentGame == loaded) + " active=" + finalActive +
                " loadObserved=" + loadObserved + " guiReady=" + guiReadyObserved + " flightReady=" + flightReadyObserved + " previousRunId=" + previousRunId + " currentRunId=" + (finalBridge == null ? "missing" : finalBridge.CurrentRunId) +
                " previousEpoch=" + previousEpoch + " currentEpoch=" + (finalBridge == null ? "missing" : finalBridge.CurrentLoadEpoch) + " previousSession=" + previousSession + " currentSession=" + (finalBridge == null ? "missing" : finalBridge.CurrentSessionId) +
                " freshFence=" + finalFreshFence + " worldId=" + (after == null ? "missing" : after.WorldId) + " sequence=" + (after == null ? "missing" : after.AcceptedSequence.ToString(CultureInfo.InvariantCulture)) +
                " revision=" + (after == null ? "missing" : after.Revision.ToString(CultureInfo.InvariantCulture)) + " capsuleHash=" + (after == null ? "missing" : AcceptedStateCodec.ComputeHash(after)) +
                " expectedWorld=" + before.WorldId + " expectedSequence=" + before.AcceptedSequence.ToString(CultureInfo.InvariantCulture) + " expectedRevision=" + before.Revision.ToString(CultureInfo.InvariantCulture) +
                " expectedCapsuleHash=" + AcceptedStateCodec.ComputeHash(before) + " exactCapsule=" + finalExactCapsule);
            if (finalActive && savedSourceId != 0 && savedDestinationId != 0)
            {
                try
                {
                    Part sourceProbe = FindPart(activeVessel, savedSourceId), destinationProbe = FindPart(activeVessel, savedDestinationId);
                    ProviderTriple sourceProbeRow = ReadTriple(sourceProbe, FindResource(sourceProbe, savedResource));
                    ProviderTriple destinationProbeRow = ReadTriple(destinationProbe, FindResource(destinationProbe, savedResource));
                    Line("RELOAD_PROVIDER_ROWS token=" + token + " sourcePersistentId=" + sourceProbe.persistentId + " sourceFlightId=" + sourceProbe.flightID + " sourcePart=" + F(sourceProbeRow.Part) + " sourceAmount=" + F(sourceProbeRow.Amount) + " sourceOriginal=" + F(sourceProbeRow.Original) + " sourceSnapshot=" + F(sourceProbeRow.Snapshot) +
                        " destinationPersistentId=" + destinationProbe.persistentId + " destinationFlightId=" + destinationProbe.flightID + " destinationPart=" + F(destinationProbeRow.Part) + " destinationAmount=" + F(destinationProbeRow.Amount) + " destinationOriginal=" + F(destinationProbeRow.Original) + " destinationSnapshot=" + F(destinationProbeRow.Snapshot));
                }
                catch (Exception ex) { Line("RELOAD_PROVIDER_ROWS unavailable=" + Bound(ex.GetType().Name + ":" + ex.Message, 384)); }
            }
            if (stable < 5 || !finalActive || !finalReadyEvents || !finalFreshFence || !finalExactCapsule)
                throw new TimeoutException("Selected save reload failed final exact readiness/prefix gate; see RELOAD_GATE flags above");
            Part source = FindPart(activeVessel, savedSourceId), destination = FindPart(activeVessel, savedDestinationId);
            PartResource sourceResource = FindResource(source, savedResource), destinationResource = FindResource(destination, savedResource);
            ProviderTriple sourceRow = ReadTriple(source, sourceResource), destinationRow = ReadTriple(destination, destinationResource);
            Line("RELOADED token=" + token + " worldId=" + after.WorldId + " runId=" + FindObjectOfType<WorldBridgeAddon>().CurrentRunId + " sequence=" + after.AcceptedSequence + " revision=" + after.Revision +
                " capsuleHash=" + AcceptedStateCodec.ComputeHash(after) + " load=" + loadObserved + " guiReady=" + guiReadyObserved + " flightReady=" + flightReadyObserved + " loaded=" + activeVessel.loaded +
                " sourcePersistentId=" + source.persistentId + " sourceFlightId=" + source.flightID + " sourcePart=" + F(sourceRow.Part) + " sourceAmount=" + F(sourceRow.Amount) + " sourceOriginal=" + F(sourceRow.Original) + " sourceSnapshot=" + F(sourceRow.Snapshot) +
                " destinationPersistentId=" + destination.persistentId + " destinationFlightId=" + destination.flightID + " destinationPart=" + F(destinationRow.Part) + " destinationAmount=" + F(destinationRow.Amount) + " destinationOriginal=" + F(destinationRow.Original) + " destinationSnapshot=" + F(destinationRow.Snapshot));
            if (sourceRow.Part != savedSourceAmount || sourceRow.Amount != sourceRow.Part || sourceRow.Original != sourceRow.Part ||
                destinationRow.Part != savedDestinationAmount || destinationRow.Amount != destinationRow.Part || destinationRow.Original != destinationRow.Part)
                throw new InvalidDataException("Selected reload physical/provider rows differ from the accepted one-unit dispatch");
        }

        uint savedSourceId, savedDestinationId;
        string savedResource;
        double savedSourceAmount, savedDestinationAmount;
        string variantResourceName;
        double variantDestinationAmount;
        uint variantDestinationPersistentId;

        string PrepareVariantCraft(string sourcePath, double targetAmount)
        {
            NoReparse(sourcePath);
            if (!File.Exists(sourcePath) || new FileInfo(sourcePath).Length > 4 * 1024 * 1024) throw new InvalidDataException("Known-good Muna 1 craft is missing or exceeds the fixture file bound");
            string variantPath = Path.Combine(root, (scheduleMode ? "ExpanseScheduleM5Smoke-" : "ExpansePhysical3bSmoke-") + token + ".craft");
            NoReparse(variantPath);
            if (File.Exists(variantPath) || Directory.Exists(variantPath)) throw new IOException("Derived fixture craft already exists; refusing overwrite");
            ConfigNode craft = ConfigNode.Load(sourcePath);
            if (craft == null) throw new InvalidDataException("Could not parse known-good Muna 1 craft");
            ConfigNode selectedPart = null, selectedResource = null;
            string selectedPartName = null;
            double originalAmount = 0, maximum = 0;
            foreach (ConfigNode part in craft.GetNodes("PART"))
            {
                foreach (ConfigNode resource in part.GetNodes("RESOURCE"))
                {
                    if (!String.Equals(resource.GetValue("name"), "LiquidFuel", StringComparison.Ordinal)) continue;
                    double amount, capacity;
                    if (!Double.TryParse(resource.GetValue("amount"), NumberStyles.Float, CultureInfo.InvariantCulture, out amount) ||
                        !Double.TryParse(resource.GetValue("maxAmount"), NumberStyles.Float, CultureInfo.InvariantCulture, out capacity) || amount < 2.0 || capacity < amount) continue;
                    selectedPart = part; selectedResource = resource; selectedPartName = part.GetValue("name") ?? part.GetValue("partName") ?? "unknown";
                    originalAmount = amount; maximum = capacity; break;
                }
                if (selectedResource != null) break;
            }
            if (selectedResource == null) throw new InvalidDataException("Muna 1 craft has no LiquidFuel RESOURCE with two units of amount and capacity");
            if (!UInt32.TryParse(selectedPart.GetValue("persistentId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out variantDestinationPersistentId) || variantDestinationPersistentId == 0)
                throw new InvalidDataException("Edited craft part has no stable nonzero persistent ID");
            variantResourceName = "LiquidFuel";
            if (targetAmount < 0 || targetAmount > originalAmount || maximum < targetAmount || originalAmount - targetAmount < 1.0)
                throw new InvalidDataException("Requested fixture destination amount is outside the selected stock resource range");
            variantDestinationAmount = targetAmount;
            selectedResource.SetValue("amount", variantDestinationAmount.ToString("R", CultureInfo.InvariantCulture));
            craft.Save(variantPath);
            NoReparse(variantPath);
            if (!File.Exists(variantPath) || new FileInfo(variantPath).Length > 4 * 1024 * 1024) throw new IOException("Derived craft variant was not saved within bounds");
            ConfigNode verify = ConfigNode.Load(variantPath);
            bool found = verify != null && verify.GetNodes("PART").SelectMany(p => p.GetNodes("RESOURCE")).Any(r =>
                r.GetValue("name") == variantResourceName && r.GetValue("amount") == variantDestinationAmount.ToString("R", CultureInfo.InvariantCulture));
            if (!found) throw new InvalidDataException("Derived craft variant failed serialized amount verification");
            Line("CRAFT_VARIANT source=" + Bound(sourcePath, 256) + " sourceSha256=" + Sha256(sourcePath) + " variant=" + Bound(variantPath, 256) + " variantSha256=" + Sha256(variantPath) +
                " editedPart=" + Bound(selectedPartName, 96) + " editedPersistentId=" + variantDestinationPersistentId + " resource=" + variantResourceName + " originalAmount=" + F(originalAmount) + " variantAmount=" + F(variantDestinationAmount) + " capacity=" + F(maximum) + " changedOnlyResourceAmount=true");
            return variantPath;
        }
        int savedPartRows, savedBrpRows;

        void LogSavedSelectedRows(string path, Part source, PartResource sourceResource, Part destination, PartResource destinationResource, AcceptedState state)
        {
            savedSourceId = source.persistentId; savedDestinationId = destination.persistentId; savedResource = sourceResource.resourceName;
            savedSourceAmount = sourceResource.amount; savedDestinationAmount = destinationResource.amount;
            savedPartRows = savedBrpRows = 0;
            ConfigNode rootNode = ConfigNode.Load(path);
            if (rootNode == null) throw new InvalidDataException("Cannot parse saved physical fixture ConfigNode");
            LogSavedRowsRecursive(rootNode, source.flightID, destination.flightID, source.persistentId, destination.persistentId, savedResource, "root", 0);
            if (savedPartRows != 2 || savedBrpRows != 2) throw new InvalidDataException("Saved file must contain exactly two selected PART resource rows and two BRP inventory rows; found " + savedPartRows + "/" + savedBrpRows);
            Line("SAVED_ACCEPTED sequence=" + state.AcceptedSequence + " counter=" + state.Counter + " capsuleHash=" + AcceptedStateCodec.ComputeHash(state) + " sourcePersistentId=" + source.persistentId + " destinationPersistentId=" + destination.persistentId + " resource=" + savedResource);
        }

        void LogSavedRowsRecursive(ConfigNode node, uint sourceFlightId, uint destinationFlightId, uint sourcePersistentId, uint destinationPersistentId, string resource, string path, int depth)
        {
            if (node == null || depth > 64) return;
            if (node.name == "PART")
            {
                uint id;
                if (UInt32.TryParse(node.GetValue("persistentId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out id) && (id == sourcePersistentId || id == destinationPersistentId))
                    foreach (ConfigNode row in node.GetNodes("RESOURCE")) if (row.GetValue("name") == resource)
                    { savedPartRows++; Line("SAVED_PART_RESOURCE partPersistentId=" + id + " amount=" + Bound(row.GetValue("amount"), 64) + " maxAmount=" + Bound(row.GetValue("maxAmount"), 64) + " path=" + path); }
            }
            string flight = FirstNodeValue(node, "flightId", "FlightId"), moduleId = FirstNodeValue(node, "moduleId", "ModuleId"), name = FirstNodeValue(node, "resourceName", "ResourceName", "name");
            if ((node.name.IndexOf("INVENTORY", StringComparison.OrdinalIgnoreCase) >= 0 || node.name.IndexOf("RESOURCE", StringComparison.OrdinalIgnoreCase) >= 0) &&
                (flight == sourceFlightId.ToString(CultureInfo.InvariantCulture) || flight == destinationFlightId.ToString(CultureInfo.InvariantCulture)) && String.Equals(name, resource, StringComparison.Ordinal))
            { savedBrpRows++; Line("SAVED_BRP_ROW flightId=" + flight + " moduleId=" + (moduleId ?? "null") + " resource=" + name + " amount=" + Bound(FirstNodeValue(node, "amount", "Amount"), 64) + " originalAmount=" + Bound(FirstNodeValue(node, "originalAmount", "OriginalAmount"), 64) + " path=" + path); }
            ConfigNode[] children = node.GetNodes();
            for (int i = 0; i < children.Length; i++) LogSavedRowsRecursive(children[i], sourceFlightId, destinationFlightId, sourcePersistentId, destinationPersistentId, resource, path + "/" + children[i].name + "[" + i.ToString(CultureInfo.InvariantCulture) + "]", depth + 1);
        }
        static string FirstNodeValue(ConfigNode node, params string[] names) { foreach (string name in names) if (node.HasValue(name)) return node.GetValue(name); return null; }
        static Part FindPart(Vessel vessel, uint persistentId) { Part[] found = vessel.parts.Where(p => p != null && p.persistentId == persistentId).ToArray(); if (found.Length != 1) throw new InvalidDataException("Expected one reloaded part with persistentId=" + persistentId + ", got " + found.Length); return found[0]; }
        static PartResource FindResource(Part part, string name) { PartResource[] found = part.Resources.Cast<PartResource>().Where(r => r != null && r.resourceName == name).ToArray(); if (found.Length != 1) throw new InvalidDataException("Expected one reloaded resource " + name + " on part " + part.persistentId); return found[0]; }
        bool vesselLoaded() { return FlightGlobals.ActiveVessel != null && FlightGlobals.ActiveVessel.loaded; }
        void OnLoad(ConfigNode node) { loadObserved = true; Line("EVENT onGameStateLoad ut=" + F(Planetarium.GetUniversalTime())); }
        void OnGuiReady(GameScenes scene) { if (scene == GameScenes.FLIGHT) { guiReadyObserved = true; Line("EVENT onLevelWasLoadedGUIReady scene=" + scene + " ut=" + F(Planetarium.GetUniversalTime())); } }
        void OnFlightReady() { if (HighLogic.LoadedSceneIsFlight) { flightReadyObserved = true; Line("EVENT onFlightReady ut=" + F(Planetarium.GetUniversalTime())); } }

        Part[] SelectPair(Vessel vessel, string resourceName)
        {
            if (vessel == null || !vessel.loaded || vessel.packed || vessel.parts == null || vessel.parts.Count > 16384) throw new InvalidOperationException("Fixture vessel is unavailable or exceeds bound");
            var parts = vessel.parts.Where(p => p != null && p.persistentId != 0 && p.Resources != null && p.Resources.Count > 0).OrderBy(p => p.persistentId).ToArray();
            var destinations = parts.Where(p => p.persistentId == variantDestinationPersistentId && p.Resources.Cast<PartResource>().Any(r => r != null && r.resourceName == resourceName &&
                Math.Abs(r.amount - variantDestinationAmount) <= 1e-9 && r.maxAmount - r.amount >= 1.0 && r.flowState)).ToArray();
            if (destinations.Length != 1) throw new InvalidOperationException("Derived craft destination persistent ID and resource did not resolve to exactly one loaded part; matches=" + destinations.Length);
            Part destination = destinations[0];
            Part source = parts.FirstOrDefault(p => p != destination && p.Resources.Cast<PartResource>().Any(r => r != null &&
                r.resourceName == resourceName && r.amount >= 1.0 && r.flowState));
            if (source == null) throw new InvalidOperationException("No distinct loaded source has one unit of the edited destination's resource");
            return new[] { source, destination };
        }

        static AcceptedState ReadState()
        {
            RecoveryCapsuleModule module = RecoveryCapsuleModule.Instance;
            if (module == null) return null;
            byte[] bytes = module.GetAcceptedStateBytes();
            return bytes == null ? null : AcceptedStateCodec.Deserialize(bytes);
        }

        sealed class ProviderTriple { public double Part, Amount, Original, Snapshot; }
        static ProviderTriple ReadTriple(Part part, PartResource resource)
        {
            object inventory = FindInventory(part, resource.resourceName);
            double amount = Convert.ToDouble(ReadMember(inventory, "Amount"), CultureInfo.InvariantCulture);
            double original = Convert.ToDouble(ReadMember(inventory, "OriginalAmount"), CultureInfo.InvariantCulture);
            object snapshot = ReadMember(inventory, "Snapshot");
            double snap = snapshot == null ? resource.amount : Convert.ToDouble(ReadMember(snapshot, "amount"), CultureInfo.InvariantCulture);
            return new ProviderTriple { Part = resource.amount, Amount = amount, Original = original, Snapshot = snap };
        }
        static object FindInventory(Part part, string name)
        {
            object vesselModules = ReadMember(part.vessel, "vesselModules");
            foreach (object module in (IEnumerable)vesselModules)
            {
                if (module == null || module.GetType().FullName != "BackgroundResourceProcessing.BackgroundResourceProcessor") continue;
                foreach (object inv in (IEnumerable)ReadMember(module, "Inventories"))
                    if (Convert.ToUInt32(ReadMember(inv, "FlightId"), CultureInfo.InvariantCulture) == part.flightID && ReadMember(inv, "ModuleId") == null && Convert.ToString(ReadMember(inv, "ResourceName"), CultureInfo.InvariantCulture) == name) return inv;
            }
            throw new InvalidOperationException("BRP inventory row was not found by part.flightID/moduleId=null/resource");
        }
        static object ReadMember(object instance, string name)
        {
            if (instance == null) throw new InvalidOperationException("Cannot read " + name + " from null");
            FieldInfo field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null) return field.GetValue(instance);
            PropertyInfo property = instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null) return property.GetValue(instance, null);
            throw new MissingMemberException(instance.GetType().FullName, name);
        }
        static void InvokeNoArg(object target, string methodName)
        {
            MethodInfo method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            if (method == null) throw new MissingMethodException(target.GetType().FullName, methodName + "()");
            method.Invoke(target, null);
        }
        static IEnumerator WaitForScene(GameScenes scene, float timeout)
        {
            float end = Time.realtimeSinceStartup + timeout;
            while (Time.realtimeSinceStartup < end) { if (HighLogic.LoadedScene == scene) yield break; yield return new WaitForSecondsRealtime(0.25f); }
            throw new TimeoutException("KSP scene did not reach " + scene);
        }
        IEnumerator WaitForSpaceCenterReady(string save, float timeout)
        {
            float end = Time.realtimeSinceStartup + timeout;
            int stable = 0;
            while (Time.realtimeSinceStartup < end)
            {
                WorldBridgeAddon bridge = FindObjectOfType<WorldBridgeAddon>();
                DepotRegistryModule registry = DepotRegistryModule.Instance;
                RecoveryCapsuleModule recovery = RecoveryCapsuleModule.Instance;
                bool ready = HighLogic.LoadedScene == GameScenes.SPACECENTER && HighLogic.CurrentGame != null && HighLogic.SaveFolder == save &&
                    bridge != null && !bridge.IsLoadUnresolved && registry != null && registry.IsReady && !registry.IsCorrupt &&
                    recovery != null && recovery.IsLoaded && !recovery.IsCorrupt && recovery.HasAcceptedState && bridge.CanAcceptPhysicalEffects;
                stable = ready ? stable + 1 : 0;
                if (stable >= 5)
                {
                    AcceptedState state = ReadState();
                    Line("SPACECENTER_READY saveFolder=" + save + " worldId=" + (state == null ? "missing" : state.WorldId) + " stableFrames=" + stable +
                        " bridgeRunId=" + bridge.CurrentRunId + " sessionId=" + bridge.CurrentSessionId + " loadEpoch=" + bridge.CurrentLoadEpoch + " registryDepots=" + registry.RegisteredDepotCount);
                    yield break;
                }
                yield return null;
            }
            throw new TimeoutException("Disposable Space Center registry/recovery context did not become ready before launch");
        }
        IEnumerator WaitForFlight(string save, float timeout)
        {
            float end = Time.realtimeSinceStartup + timeout;
            while (Time.realtimeSinceStartup < end)
            {
                WorldBridgeAddon bridge = FindObjectOfType<WorldBridgeAddon>();
                if (HighLogic.LoadedSceneIsFlight && HighLogic.CurrentGame != null && HighLogic.SaveFolder == save && FlightGlobals.ActiveVessel != null && FlightGlobals.ActiveVessel.loaded && !FlightGlobals.ActiveVessel.packed && bridge != null && bridge.CanAcceptPhysicalEffects) yield break;
                yield return new WaitForSecondsRealtime(0.25f);
            }
            WorldBridgeAddon lastBridge = FindObjectOfType<WorldBridgeAddon>();
            DepotRegistryModule lastRegistry = DepotRegistryModule.Instance;
            RecoveryCapsuleModule lastRecovery = RecoveryCapsuleModule.Instance;
            Vessel vessel = FlightGlobals.ActiveVessel;
            AcceptedState state = ReadState();
            string vesselInfo = vessel == null ? "none" : "name=" + Bound(vessel.vesselName, 96) + ",id=" + vessel.id.ToString("D") + ",persistentId=" +
                (vessel.parts == null ? "none" : String.Join(",", vessel.parts.Where(p => p != null).Take(8).Select(p => p.persistentId.ToString(CultureInfo.InvariantCulture)).ToArray())) +
                ",loaded=" + vessel.loaded + ",packed=" + vessel.packed + ",parts=" + (vessel.parts == null ? -1 : vessel.parts.Count);
            Line("WAIT_FLIGHT_TIMEOUT scene=" + HighLogic.LoadedScene + " sceneIsFlight=" + HighLogic.LoadedSceneIsFlight + " currentGame=" + (HighLogic.CurrentGame == null ? "null" : Bound(HighLogic.CurrentGame.Title, 128)) +
                " saveFolder=" + Bound(HighLogic.SaveFolder, 128) + " expectedSaveFolder=" + save + " activeVessel=" + vesselInfo +
                " bridge=" + (lastBridge == null ? "missing" : "present") + " runId=" + (lastBridge == null ? "n/a" : lastBridge.CurrentRunId) + " sessionId=" + (lastBridge == null ? "n/a" : lastBridge.CurrentSessionId) +
                " loadEpoch=" + (lastBridge == null ? "n/a" : lastBridge.CurrentLoadEpoch) + " loadUnresolved=" + (lastBridge == null ? "n/a" : lastBridge.IsLoadUnresolved.ToString()) +
                " canAcceptPhysicalEffects=" + (lastBridge == null ? "n/a" : lastBridge.CanAcceptPhysicalEffects.ToString()) +
                " registry=" + (lastRegistry == null ? "missing" : "present") + " registryReady=" + (lastRegistry == null ? "n/a" : lastRegistry.IsReady.ToString()) +
                " registryCorrupt=" + (lastRegistry == null ? "n/a" : lastRegistry.IsCorrupt.ToString()) + " registryWorldId=" + (lastRegistry == null ? "n/a" : V(lastRegistry.WorldId)) +
                " registryCount=" + (lastRegistry == null ? -1 : lastRegistry.RegisteredDepotCount) +
                " recovery=" + (lastRecovery == null ? "missing" : "present") + " recoveryLoaded=" + (lastRecovery == null ? "n/a" : lastRecovery.IsLoaded.ToString()) +
                " recoveryCorrupt=" + (lastRecovery == null ? "n/a" : lastRecovery.IsCorrupt.ToString()) + " hasAcceptedState=" + (lastRecovery == null ? "n/a" : lastRecovery.HasAcceptedState.ToString()) +
                " recoveryWorldId=" + (lastRecovery == null ? "n/a" : V(lastRecovery.WorldId)) + " recoverySequence=" + (state == null ? "n/a" : state.AcceptedSequence.ToString(CultureInfo.InvariantCulture)) +
                " recoveryHold=" + (lastRecovery == null ? "n/a" : Bound(lastRecovery.HoldReason, 256)) + " fixtureActivation=accepted-at-start");
            throw new TimeoutException("Disposable loaded Flight world did not become effect-ready");
        }
        static string V(string value) { return String.IsNullOrEmpty(value) ? "empty" : Bound(value, 128); }
        void Line(string text) { try { File.AppendAllText(logPath, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " " + text.Replace('\r', ' ').Replace('\n', ' ') + Environment.NewLine); } catch { } }
        static string F(double x) { return x.ToString("R", CultureInfo.InvariantCulture); }
        static string Bound(string x, int n) { if (String.IsNullOrEmpty(x)) return "fixture error"; x = x.Replace('\r', ' ').Replace('\n', ' '); return x.Length <= n ? x : x.Substring(0, n); }
        static string Sha256(string path) { using (var sha = System.Security.Cryptography.SHA256.Create()) using (var stream = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", String.Empty); }
        static void NoReparse(string path)
        {
            string full = Path.GetFullPath(path); string cursor = full; bool mustExist = Directory.Exists(full) || File.Exists(full);
            while (!String.IsNullOrEmpty(cursor))
            {
                if (Directory.Exists(cursor) || File.Exists(cursor)) { if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse path is not allowed: " + cursor); }
                else if (mustExist) throw new DirectoryNotFoundException(cursor);
                string parent = Path.GetDirectoryName(cursor); if (String.Equals(parent, cursor, StringComparison.OrdinalIgnoreCase)) break; cursor = parent;
            }
        }
    }
}
