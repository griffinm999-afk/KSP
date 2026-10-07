using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Cryptography;
using UnityEngine;
using Expanse.Domain;
using Expanse.WorldBridge;

[KSPAddon(KSPAddon.Startup.MainMenu, true)]
public sealed class RecoveryM3aSmokeAddon : MonoBehaviour
{
    const string RequiredRoot = @"C:\Users\griff\Documents\KSP-RMM-Dev";
    const string RequestName = "ExpanseRecoveryM3aSmoke.request";
    const string RequestPrefix = "RUN_EXPANSE_RECOVERY_M3A=";
    const string ResumeRequestName = "ExpanseRecoveryM3aResume.request";
    const string ResumeRequestPrefix = "RUN_EXPANSE_RECOVERY_M3A_RESUME=";
    const string ExpectedResumeWorldId = "76e6e7fc-e5bc-47a3-8ea5-3703c068108b";
    const string SaveName = "recovery-m3a-smoke";
    const int WatchdogSeconds = 1200;
    const int HostRestartWaitSeconds = 180;
    string root, requestToken, priorToken, logPath, saveFolder, stagePath;
    string priorSessionId, priorRunId, priorLoadEpoch, priorOperationId1, priorOperationId2, copiedSaveSha256;
    int priorProcessId;
    float startedAt;
    bool acceptedRequest, resumeMode;
    bool levelReadySeen, reloadLoadSeen, reloadFlightReadySeen;
    double? reloadLoadUt, reloadLevelReadyUt, reloadFlightReadyUt;
    string reloadLoadRunId, reloadLoadEpoch, reloadLevelReadyRunId, reloadLevelReadyEpoch, reloadFlightReadyRunId, reloadFlightReadyEpoch;

    void Start()
    {
        try
        {
            root = Path.GetFullPath(KSPUtil.ApplicationRootPath).TrimEnd('\\', '/');
            if (!string.Equals(root, RequiredRoot, StringComparison.OrdinalIgnoreCase)) return;
            AssertNoReparseAncestors(root);
            string smokeRequestPath = Path.Combine(root, RequestName);
            string resumeRequestPath = Path.Combine(root, ResumeRequestName);
            bool hasSmokeRequest = File.Exists(smokeRequestPath);
            bool hasResumeRequest = File.Exists(resumeRequestPath);
            if (!hasSmokeRequest && !hasResumeRequest) return;
            if (hasSmokeRequest && hasResumeRequest) throw new IOException("Refusing ambiguous simultaneous smoke and resume requests.");
            string requestPath = hasResumeRequest ? resumeRequestPath : smokeRequestPath;
            if ((File.GetAttributes(requestPath) & FileAttributes.ReparsePoint) != 0) return;
            AssertNoReparseAncestors(requestPath);
            if (new FileInfo(requestPath).Length > 256) throw new InvalidDataException("One-shot recovery fixture request exceeds its small input bound.");
            string request = File.ReadAllText(requestPath).Trim();
            if (hasResumeRequest)
            {
                const int expectedLength = 32 + 1 + 32;
                if (!request.StartsWith(ResumeRequestPrefix, StringComparison.Ordinal) || request.Length != ResumeRequestPrefix.Length + expectedLength) return;
                string fields = request.Substring(ResumeRequestPrefix.Length);
                if (fields[32] != ',' || !Guid.TryParseExact(fields.Substring(0, 32), "N", out var resumeId) || resumeId == Guid.Empty ||
                    !Guid.TryParseExact(fields.Substring(33, 32), "N", out var priorId) || priorId == Guid.Empty || resumeId == priorId) return;
                requestToken = resumeId.ToString("N");
                priorToken = priorId.ToString("N");
                resumeMode = true;
                logPath = Path.Combine(root, "ExpanseRecoveryM3aResume-" + requestToken + ".log");
                saveFolder = "ExpanseRecoveryM3aResume-" + requestToken;
            }
            else
            {
                if (!request.StartsWith(RequestPrefix, StringComparison.Ordinal) || request.Length != RequestPrefix.Length + 32 ||
                    !Guid.TryParseExact(request.Substring(RequestPrefix.Length), "N", out var token) || token == Guid.Empty) return;
                requestToken = token.ToString("N");
                logPath = Path.Combine(root, "ExpanseRecoveryM3aSmoke-" + requestToken + ".log");
                stagePath = Path.Combine(root, "ExpanseRecoveryM3aSmoke-" + requestToken + ".stage");
                AssertNoReparseAncestors(stagePath);
                if (File.Exists(stagePath)) throw new IOException("Restart stage control already exists for this one-shot token.");
                saveFolder = "ExpanseRecoveryM3aSmoke-" + requestToken;
            }
            AssertNoReparseAncestors(logPath);
            if (File.Exists(logPath) || Directory.Exists(logPath)) throw new IOException("One-shot fixture log path already exists; refusing to overwrite prior evidence.");
            string savesRoot = Path.GetFullPath(Path.Combine(root, "saves"));
            AssertNoReparseAncestors(savesRoot);
            string savePath = Path.GetFullPath(Path.Combine(savesRoot, saveFolder));
            if (!savePath.StartsWith(savesRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Derived save path escapes the development saves directory.");
            if (!resumeMode && (Directory.Exists(savePath) || File.Exists(savePath))) throw new IOException("Unique disposable save path already exists.");
            if (resumeMode)
            {
                if (!Directory.Exists(savePath) || File.Exists(savePath)) throw new DirectoryNotFoundException("Sol's token-derived copied resume save folder is missing or not a directory.");
                string priorFolder = "ExpanseRecoveryM3aSmoke-" + priorToken;
                string priorSavePath = Path.GetFullPath(Path.Combine(savesRoot, priorFolder));
                if (!priorSavePath.StartsWith(savesRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(priorSavePath))
                    throw new DirectoryNotFoundException("The token-derived original staged save folder is missing.");
                ValidateTreeNoReparse(priorSavePath, 4096, 128L * 1024 * 1024);
                ValidateTreeNoReparse(savePath, 4096, 128L * 1024 * 1024);
                string sourceSfs = Path.Combine(priorSavePath, SaveName + ".sfs");
                string copiedSfs = Path.Combine(savePath, SaveName + ".sfs");
                copiedSaveSha256 = FileSha256(sourceSfs);
                if (!string.Equals(copiedSaveSha256, FileSha256(copiedSfs), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Sol's copied save does not match the original staged save hash.");
                LoadPriorRunIdentity(priorToken);
            }
            if (!WorldBridgeAddon.ActivateRecoveryFixture(requestToken)) throw new InvalidOperationException("Bridge rejected the one-shot fixture activation token.");

            File.Delete(requestPath);
            acceptedRequest = true;
            AudioListener.volume = 0f;
            DontDestroyOnLoad(gameObject);
            startedAt = Time.realtimeSinceStartup;
            File.WriteAllText(logPath, "START utc=" + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + Environment.NewLine);
            StartCoroutine(Supervise(resumeMode ? RunResume() : Run()));
        }
        catch (Exception ex)
        {
            if (acceptedRequest) { Line("FAIL initialize " + ex); Application.Quit(); }
            else Debug.LogError("Expanse recovery fixture refused to start safely: " + ex.Message);
        }
    }

    IEnumerator Supervise(IEnumerator body)
    {
        Stack<IEnumerator> coroutines = new Stack<IEnumerator>();
        coroutines.Push(body);
        while (true)
        {
            if (Time.realtimeSinceStartup - startedAt > WatchdogSeconds)
            {
                Line("FAIL watchdog exceeded " + WatchdogSeconds + " seconds");
                Cleanup();
                Application.Quit();
                yield break;
            }
            IEnumerator currentCoroutine = coroutines.Peek();
            object current;
            bool more;
            try { more = currentCoroutine.MoveNext(); current = more ? currentCoroutine.Current : null; }
            catch (Exception ex) { Line("FAIL " + ex); Cleanup(); Application.Quit(); yield break; }
            if (!more) { coroutines.Pop(); if (coroutines.Count == 0) yield break; continue; }
            IEnumerator nested = current as IEnumerator;
            if (nested != null) { coroutines.Push(nested); continue; }
            yield return current;
        }
    }

    IEnumerator Run()
    {
        Line("REQUEST accepted root=" + root + " saveFolder=" + saveFolder + " fixture=disposable-counter-only");
        VerifyCapsuleBoundarySynthetic();
        yield return new WaitForSecondsRealtime(2f);
        HighLogic.SaveFolder = saveFolder;
        HighLogic.CurrentGame = GamePersistence.CreateNewGame(saveFolder, Game.Modes.SANDBOX, new GameParameters(), "Squad/Flags/default", GameScenes.SPACECENTER, EditorFacility.VAB);
        if (HighLogic.CurrentGame == null) throw new InvalidOperationException("CreateNewGame returned null.");
        HighLogic.CurrentGame.Start();
        yield return WaitForWorld(saveFolder, 90f);

        string craftPath = Path.GetFullPath(Path.Combine(root, "GameData", "SquadExpansion", "MakingHistory", "Ships", "VAB", "Muna 1.craft"));
        if (!craftPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(craftPath))
            throw new FileNotFoundException("Expected stock Muna 1 disposable fixture craft is missing from the dev install.", craftPath);
        FlightDriver.StartWithNewLaunch(craftPath, "Squad/Flags/default", "LaunchPad", new VesselCrewManifest());
        yield return WaitForScene(GameScenes.FLIGHT, 180f);
        yield return WaitForUsableVessel(180f);
        yield return WaitForFlightWorld(saveFolder, 90f);

        WorldBridgeAddon bridge = FindObjectOfType<WorldBridgeAddon>();
        if (bridge == null || !bridge.CanAcceptEffects) throw new InvalidOperationException("Bridge did not expose the explicitly enabled fixture capability.");
        AcceptedState state = ReadState();
        if (state == null || state.Counter != 0 || state.AcceptedSequence != 0 || state.Receipts.Length != 0) throw new InvalidOperationException("Disposable world did not start with an empty recovery projection.");
        string worldId = state.WorldId;
        string runId = bridge.CurrentRunId;
        string loadEpoch = bridge.CurrentLoadEpoch;
        double utBefore = Planetarium.GetUniversalTime();
        Line("READY worldId=" + worldId + " runId=" + runId + " sessionId=" + bridge.CurrentSessionId + " loadEpoch=" + loadEpoch + " counter=" + state.Counter + " saveFolder=" + HighLogic.SaveFolder + " ut=" + F(utBefore));

        float commandDeadline = Time.realtimeSinceStartup + 120f;
        state = null;
        while (Time.realtimeSinceStartup < commandDeadline)
        {
            state = ReadState();
            if (state != null && state.Counter > 1) throw new InvalidOperationException("Counter advanced more than once during duplicate command exercise.");
            if (state != null && state.Counter == 1 && state.AcceptedSequence == 1 && state.Receipts.Length == 1) break;
            yield return new WaitForSecondsRealtime(0.25f);
        }
        if (state == null || state.Counter != 1 || state.AcceptedSequence != 1 || state.Receipts.Length != 1) throw new TimeoutException("Host fixture did not settle exactly one counter operation within 120 seconds.");
        AcceptedReceipt receipt = state.Receipts[0];
        if (receipt.CounterDelta != 1 || receipt.Outcome != "accepted" || receipt.AppliedUt < 0) throw new InvalidOperationException("Accepted counter receipt fields do not match the fixture command.");
        Line("APPLIED worldId=" + state.WorldId + " runId=" + bridge.CurrentRunId + " revision=" + state.Revision + " sequence=" + state.AcceptedSequence + " counter=" + state.Counter + " operationId=" + receipt.OperationId + " receiptCount=" + state.Receipts.Length + " appliedUt=" + F(receipt.AppliedUt));

        if (!HighLogic.LoadedSceneIsFlight || HighLogic.CurrentGame == null || HighLogic.SaveFolder != saveFolder || FlightGlobals.ActiveVessel == null || !FlightGlobals.ActiveVessel.loaded)
            throw new InvalidOperationException("Refusing save outside the loaded Flight scene in the owned disposable KSP world.");
        string runBeforeReload = bridge.CurrentRunId;
        string epochBeforeReload = bridge.CurrentLoadEpoch;
        try { FlightDriver.SetPause(true, false); } catch { }
        yield return new WaitForSecondsRealtime(1f);
        if (!FlightDriver.Pause && !Planetarium.Pause) throw new InvalidOperationException("Could not pause the disposable Flight world for same-UT recovery reload.");
        double fixedUt = Planetarium.GetUniversalTime();
        Planetarium.SetUniversalTime(fixedUt);
        yield return new WaitForSecondsRealtime(2f);
        double utAtSave = Planetarium.GetUniversalTime();
        if (Math.Abs(utAtSave - fixedUt) > 0.001) throw new InvalidOperationException("UT did not remain fixed before saving the recovery witness.");
        Line("FIXED_UT preSaveBegin=" + F(fixedUt) + " preSaveEnd=" + F(utAtSave) + " flightDriverPause=" + FlightDriver.Pause + " planetariumPause=" + Planetarium.Pause + " timeScale=" + F(Time.timeScale));
        GamePersistence.SaveGame("recovery-m3a-smoke", saveFolder, SaveMode.OVERWRITE);
        Line("SAVED worldId=" + state.WorldId + " runId=" + runBeforeReload + " loadEpoch=" + epochBeforeReload + " counter=" + state.Counter + " ut=" + F(utAtSave) + " saveName=recovery-m3a-smoke");

        levelReadySeen = false;
        reloadFlightReadySeen = false;
        reloadLoadSeen = false;
        reloadLoadUt = reloadLevelReadyUt = reloadFlightReadyUt = null;
        reloadLoadRunId = reloadLoadEpoch = reloadLevelReadyRunId = reloadLevelReadyEpoch = reloadFlightReadyRunId = reloadFlightReadyEpoch = null;
        GameEvents.onGameStateLoad.Add(OnGameLoad);
        GameEvents.onLevelWasLoadedGUIReady.Add(OnLevelReady);
        GameEvents.onFlightReady.Add(OnFlightReady);
        Game game = GamePersistence.LoadGame("recovery-m3a-smoke", saveFolder, true, false);
        if (game == null) throw new InvalidOperationException("LoadGame returned null for the disposable recovery save.");
        HighLogic.CurrentGame = game;
        game.startScene = GameScenes.FLIGHT;
        game.Start();
        yield return WaitForReload(saveFolder, game, runBeforeReload, worldId, 1, 1, receipt.OperationId, false, bridge.CurrentSessionId, epochBeforeReload, state, 180f);
        GameEvents.onLevelWasLoadedGUIReady.Remove(OnLevelReady);
        GameEvents.onFlightReady.Remove(OnFlightReady);
        GameEvents.onGameStateLoad.Remove(OnGameLoad);

        bridge = FindObjectOfType<WorldBridgeAddon>();
        state = ReadState();
        if (bridge == null || state == null) throw new InvalidOperationException("Bridge or recovery capsule disappeared after reload.");
        Line("RELOAD_OBSERVED worldId=" + state.WorldId + " runId=" + bridge.CurrentRunId + " previousRunId=" + runBeforeReload +
            " loadEpoch=" + bridge.CurrentLoadEpoch + " counter=" + state.Counter + " revision=" + state.Revision + " sequence=" + state.AcceptedSequence +
            " receiptCount=" + state.Receipts.Length + " receiptOperationId=" + (state.Receipts.Length == 0 ? "none" : state.Receipts[0].OperationId) +
            " gameStateLoad=" + reloadLoadSeen.ToString().ToLowerInvariant() + " guiReady=" + levelReadySeen.ToString().ToLowerInvariant() + " flightReady=" + reloadFlightReadySeen.ToString().ToLowerInvariant());
        bool loadFenceAtSameUt = FenceAtSameUt(reloadLoadUt, reloadLoadRunId, reloadLoadEpoch, utAtSave, runBeforeReload, epochBeforeReload);
        bool guiFenceAtSameUt = FenceAtSameUt(reloadLevelReadyUt, reloadLevelReadyRunId, reloadLevelReadyEpoch, utAtSave, runBeforeReload, epochBeforeReload);
        bool flightFenceAtSameUt = FenceAtSameUt(reloadFlightReadyUt, reloadFlightReadyRunId, reloadFlightReadyEpoch, utAtSave, runBeforeReload, epochBeforeReload);
        Line("RELOAD_EVENT_TIMELINE savedUt=" + F(utAtSave) + " gameLoadUt=" + N(reloadLoadUt) + " gameLoadRunId=" + V(reloadLoadRunId) + " gameLoadEpoch=" + V(reloadLoadEpoch) +
            " guiReadyUt=" + N(reloadLevelReadyUt) + " guiReadyRunId=" + V(reloadLevelReadyRunId) + " guiReadyEpoch=" + V(reloadLevelReadyEpoch) +
            " flightReadyUt=" + N(reloadFlightReadyUt) + " flightReadyRunId=" + V(reloadFlightReadyRunId) + " flightReadyEpoch=" + V(reloadFlightReadyEpoch) +
            " gameLoadFenceAtSameUt=" + loadFenceAtSameUt.ToString().ToLowerInvariant() + " guiFenceAtSameUt=" + guiFenceAtSameUt.ToString().ToLowerInvariant() +
            " flightFenceAtSameUt=" + flightFenceAtSameUt.ToString().ToLowerInvariant());
        if (state.WorldId != worldId || state.Counter != 1 || state.AcceptedSequence != 1 || state.Receipts.Length != 1 || state.Receipts[0].OperationId != receipt.OperationId)
            throw new InvalidOperationException("Same-save reload did not preserve the exact accepted recovery prefix and receipt.");
        if (bridge.CurrentRunId == runBeforeReload) throw new InvalidOperationException("Same-save reload did not create a fresh execution run fence.");
        if (!reloadLoadSeen || (!levelReadySeen && !reloadFlightReadySeen)) throw new InvalidOperationException("Same-save reload did not emit game-load and Flight readiness events.");
        double utAtReady = Planetarium.GetUniversalTime();
        FlightDriver.SetPause(true, false);
        double utAfterReload = Planetarium.GetUniversalTime();
        bool sameUt = Math.Abs(utAfterReload - utAtSave) <= 0.001;
        Line("FIXED_UT postLoadSaved=" + F(utAtSave) + " atReadiness=" + F(utAtReady) + " afterPauseCall=" + F(utAfterReload) + " sameUt=" + sameUt.ToString().ToLowerInvariant() + " flightDriverPause=" + FlightDriver.Pause + " planetariumPause=" + Planetarium.Pause + " timeScale=" + F(Time.timeScale));
        Line("RELOADED worldId=" + state.WorldId + " runId=" + bridge.CurrentRunId + " previousRunId=" + runBeforeReload + " loadEpoch=" + bridge.CurrentLoadEpoch + " previousLoadEpoch=" + epochBeforeReload + " counter=" + state.Counter + " sequence=" + state.AcceptedSequence + " utBefore=" + F(utAtSave) + " utAfter=" + F(utAfterReload) + " sameUt=" + sameUt.ToString().ToLowerInvariant() + " gameStateLoad=" + reloadLoadSeen.ToString().ToLowerInvariant() + " guiReady=" + levelReadySeen.ToString().ToLowerInvariant() + " flightReady=" + reloadFlightReadySeen.ToString().ToLowerInvariant());
        if (!sameUt) Line("NOTE KSP advanced UT between saved snapshot and first stable Flight readiness; event timeline above retains the unmodified observations.");
        Line("HOST_RESTART_REQUIRED worldId=" + state.WorldId + " runId=" + bridge.CurrentRunId + " sessionId=" + bridge.CurrentSessionId + " loadEpoch=" + bridge.CurrentLoadEpoch +
            " counter=" + state.Counter + " sequence=" + state.AcceptedSequence + " stagePath=" + stagePath);
        yield return WaitForHostRestartSignal(HostRestartWaitSeconds);
        bridge = FindObjectOfType<WorldBridgeAddon>();
        state = ReadState();
        if (bridge == null || state == null || !bridge.CanAcceptEffects || state.Counter != 1 || state.AcceptedSequence != 1 || state.WorldId != worldId)
            throw new InvalidOperationException("Same-process KSP context did not remain writable at the seq1 prefix after Host restart signal.");
        Line("SECOND_READY worldId=" + state.WorldId + " runId=" + bridge.CurrentRunId + " sessionId=" + bridge.CurrentSessionId + " loadEpoch=" + bridge.CurrentLoadEpoch +
            " counter=" + state.Counter + " sequence=" + state.AcceptedSequence + " revision=" + state.Revision + " saveFolder=" + HighLogic.SaveFolder + " ut=" + F(Planetarium.GetUniversalTime()));

        float secondCommandDeadline = Time.realtimeSinceStartup + 120f;
        while (Time.realtimeSinceStartup < secondCommandDeadline)
        {
            state = ReadState();
            if (state != null && state.Counter > 2) throw new InvalidOperationException("Counter advanced more than twice during the Host restart scenario.");
            if (state != null && state.Counter == 2 && state.AcceptedSequence == 2 && state.Receipts.Length == 2) break;
            yield return new WaitForSecondsRealtime(0.25f);
        }
        if (state == null || state.Counter != 2 || state.AcceptedSequence != 2 || state.Receipts.Length != 2 || state.Receipts[1].CommandSequence != 2 ||
            state.Receipts[1].CounterDelta != 1 || state.Receipts[1].Outcome != "accepted")
            throw new TimeoutException("Host did not apply exactly one post-restart seq2 counter command within 120 seconds.");
        AcceptedReceipt secondReceipt = state.Receipts[1];
        Line("APPLIED_AFTER_HOST_RESTART worldId=" + state.WorldId + " runId=" + bridge.CurrentRunId + " revision=" + state.Revision + " sequence=" + state.AcceptedSequence +
            " counter=" + state.Counter + " operationId=" + secondReceipt.OperationId + " receiptCount=" + state.Receipts.Length + " appliedUt=" + F(secondReceipt.AppliedUt));

        string secondRunBeforeReload = bridge.CurrentRunId;
        string secondEpochBeforeReload = bridge.CurrentLoadEpoch;
        string sessionBeforeReload = bridge.CurrentSessionId;
        try { FlightDriver.SetPause(true, false); } catch { }
        yield return new WaitForSecondsRealtime(1f);
        if (!FlightDriver.Pause && !Planetarium.Pause) throw new InvalidOperationException("Could not pause the disposable Flight world before the post-restart recovery save.");
        double secondFixedUt = Planetarium.GetUniversalTime();
        Planetarium.SetUniversalTime(secondFixedUt);
        yield return new WaitForSecondsRealtime(2f);
        double secondUtAtSave = Planetarium.GetUniversalTime();
        if (Math.Abs(secondUtAtSave - secondFixedUt) > 0.001) throw new InvalidOperationException("UT did not remain fixed before saving the post-restart recovery witness.");
        GamePersistence.SaveGame("recovery-m3a-smoke", saveFolder, SaveMode.OVERWRITE);
        Line("SAVED_AFTER_HOST_RESTART worldId=" + state.WorldId + " runId=" + secondRunBeforeReload + " loadEpoch=" + secondEpochBeforeReload +
            " counter=" + state.Counter + " sequence=" + state.AcceptedSequence + " ut=" + F(secondUtAtSave) + " saveName=recovery-m3a-smoke");

        levelReadySeen = false;
        reloadFlightReadySeen = false;
        reloadLoadSeen = false;
        reloadLoadUt = reloadLevelReadyUt = reloadFlightReadyUt = null;
        reloadLoadRunId = reloadLoadEpoch = reloadLevelReadyRunId = reloadLevelReadyEpoch = reloadFlightReadyRunId = reloadFlightReadyEpoch = null;
        GameEvents.onGameStateLoad.Add(OnGameLoad);
        GameEvents.onLevelWasLoadedGUIReady.Add(OnLevelReady);
        GameEvents.onFlightReady.Add(OnFlightReady);
        Game secondLoadedGame = GamePersistence.LoadGame("recovery-m3a-smoke", saveFolder, true, false);
        if (secondLoadedGame == null) throw new InvalidOperationException("LoadGame returned null for the post-restart disposable recovery save.");
        HighLogic.CurrentGame = secondLoadedGame;
        secondLoadedGame.startScene = GameScenes.FLIGHT;
        secondLoadedGame.Start();
        yield return WaitForReload(saveFolder, secondLoadedGame, secondRunBeforeReload, worldId, 2, 2, secondReceipt.OperationId, true,
            sessionBeforeReload, secondEpochBeforeReload, state, 180f);
        GameEvents.onLevelWasLoadedGUIReady.Remove(OnLevelReady);
        GameEvents.onFlightReady.Remove(OnFlightReady);
        GameEvents.onGameStateLoad.Remove(OnGameLoad);

        bridge = FindObjectOfType<WorldBridgeAddon>();
        state = ReadState();
        if (bridge == null || state == null || state.Counter != 2 || state.AcceptedSequence != 2 || state.Receipts.Length != 2 || state.Receipts[1].OperationId != secondReceipt.OperationId)
            throw new InvalidOperationException("Post-restart same-save reload did not preserve the exact seq2 accepted recovery prefix and receipt.");
        if (bridge.CurrentRunId == secondRunBeforeReload) throw new InvalidOperationException("Post-restart same-save reload did not create a fresh execution run fence.");
        double utAtSecondReloadReady = Planetarium.GetUniversalTime();
        FlightDriver.SetPause(true, false);
        double utAfterSecondReloadPause = Planetarium.GetUniversalTime();
        bool secondSameUt = Math.Abs(utAfterSecondReloadPause - secondUtAtSave) <= 0.001;
        Line("RELOAD_AFTER_HOST_RESTART worldId=" + state.WorldId + " runId=" + bridge.CurrentRunId + " previousRunId=" + secondRunBeforeReload + " loadEpoch=" + bridge.CurrentLoadEpoch +
            " previousLoadEpoch=" + secondEpochBeforeReload + " counter=" + state.Counter + " sequence=" + state.AcceptedSequence + " receiptCount=" + state.Receipts.Length +
            " receiptOperationId=" + state.Receipts[1].OperationId + " utSaved=" + F(secondUtAtSave) + " utAtReady=" + F(utAtSecondReloadReady) +
            " utAfterPauseCall=" + F(utAfterSecondReloadPause) + " sameUt=" + secondSameUt.ToString().ToLowerInvariant() +
            " canAcceptEffects=" + bridge.CanAcceptEffects.ToString().ToLowerInvariant() + " loadUnresolved=" + bridge.IsLoadUnresolved.ToString().ToLowerInvariant());
        if (!secondSameUt) Line("NOTE post-restart reload UT drifted before the pause call; no post-load timestamp mutation was performed.");
        Line("PASS Host restart recovery, seq2 save/reload, fresh run fence and stale old-run main-thread rejection proved; save retained only at saves/" + saveFolder);
        Cleanup();
        Application.Quit();
    }

    IEnumerator RunResume()
    {
        int currentProcessId = System.Diagnostics.Process.GetCurrentProcess().Id;
        Line("REQUEST_RESUME accepted root=" + root + " resumeToken=" + requestToken + " priorToken=" + priorToken + " saveFolder=" + saveFolder +
            " fixedSaveName=" + SaveName + " worldId=" + ExpectedResumeWorldId + " sourceSaveSha256=" + copiedSaveSha256 + " priorKspPid=" + priorProcessId + " currentKspPid=" + currentProcessId);
        VerifyCapsuleBoundarySynthetic();
        yield return new WaitForSecondsRealtime(2f);

        levelReadySeen = false;
        reloadFlightReadySeen = false;
        reloadLoadSeen = false;
        reloadLoadUt = reloadLevelReadyUt = reloadFlightReadyUt = null;
        reloadLoadRunId = reloadLoadEpoch = reloadLevelReadyRunId = reloadLevelReadyEpoch = reloadFlightReadyRunId = reloadFlightReadyEpoch = null;
        GameEvents.onGameStateLoad.Add(OnGameLoad);
        GameEvents.onLevelWasLoadedGUIReady.Add(OnLevelReady);
        GameEvents.onFlightReady.Add(OnFlightReady);
        HighLogic.SaveFolder = saveFolder;
        Game selectedGame = GamePersistence.LoadGame(SaveName, saveFolder, true, false);
        if (selectedGame == null) throw new InvalidOperationException("Could not load the fixed staged recovery save from the token-derived copied folder.");
        HighLogic.CurrentGame = selectedGame;
        selectedGame.startScene = GameScenes.FLIGHT;
        selectedGame.Start();
        yield return WaitForResumeReady(selectedGame, currentProcessId, 180f);

        WorldBridgeAddon bridge = FindObjectOfType<WorldBridgeAddon>();
        AcceptedState state = ReadState();
        if (bridge == null || state == null) throw new InvalidOperationException("Resume load did not expose a bridge and accepted capsule.");
        if (currentProcessId == priorProcessId || bridge.CurrentSessionId == priorSessionId || bridge.CurrentRunId == priorRunId || bridge.CurrentLoadEpoch == priorLoadEpoch)
            throw new InvalidOperationException("Resume did not create a fresh KSP process, session, run and load epoch.");
        if (state.WorldId != ExpectedResumeWorldId || state.Counter != 2 || state.AcceptedSequence != 2 || state.Revision != 2 || state.Receipts.Length != 2 ||
            state.Receipts[0].OperationId != priorOperationId1 || state.Receipts[1].OperationId != priorOperationId2 || state.Receipts[0].CommandSequence != 1 ||
            state.Receipts[1].CommandSequence != 2 || state.Receipts[0].Outcome != "accepted" || state.Receipts[1].Outcome != "accepted")
            throw new InvalidOperationException("Selected copied save did not restore the exact expected world, counter=2 and accepted receipt pair.");
        Line("READY_RESUME worldId=" + state.WorldId + " processId=" + currentProcessId + " priorProcessId=" + priorProcessId + " sessionId=" + bridge.CurrentSessionId +
            " priorSessionId=" + priorSessionId + " runId=" + bridge.CurrentRunId + " priorRunId=" + priorRunId + " loadEpoch=" + bridge.CurrentLoadEpoch +
            " priorLoadEpoch=" + priorLoadEpoch + " saveFolder=" + saveFolder + " saveName=" + SaveName + " counter=" + state.Counter + " sequence=" + state.AcceptedSequence +
            " revision=" + state.Revision + " receipt1=" + state.Receipts[0].OperationId + " receipt2=" + state.Receipts[1].OperationId + " ut=" + F(Planetarium.GetUniversalTime()));

        float commandDeadline = Time.realtimeSinceStartup + 120f;
        while (Time.realtimeSinceStartup < commandDeadline)
        {
            state = ReadState();
            if (state != null && state.Counter > 3) throw new InvalidOperationException("Resume command advanced beyond the single expected seq3 increment.");
            if (state != null && state.Counter == 3 && state.AcceptedSequence == 3 && state.Receipts.Length == 3) break;
            yield return new WaitForSecondsRealtime(0.25f);
        }
        if (state == null || state.Counter != 3 || state.AcceptedSequence != 3 || state.Receipts.Length != 3 || state.Receipts[2].CommandSequence != 3 ||
            state.Receipts[2].CounterDelta != 1 || state.Receipts[2].Outcome != "accepted" || state.Receipts[0].OperationId != priorOperationId1 ||
            state.Receipts[1].OperationId != priorOperationId2)
            throw new TimeoutException("Fresh empty Host did not apply exactly one seq3 increment atop the restored seq2 capsule within 120 seconds.");
        AcceptedReceipt thirdReceipt = state.Receipts[2];
        Line("APPLIED_RESUME worldId=" + state.WorldId + " processId=" + currentProcessId + " sessionId=" + bridge.CurrentSessionId + " runId=" + bridge.CurrentRunId +
            " revision=" + state.Revision + " sequence=" + state.AcceptedSequence + " counter=" + state.Counter + " operationId=" + thirdReceipt.OperationId +
            " receiptCount=" + state.Receipts.Length + " appliedUt=" + F(thirdReceipt.AppliedUt));

        string runBeforeReload = bridge.CurrentRunId;
        string epochBeforeReload = bridge.CurrentLoadEpoch;
        string sessionBeforeReload = bridge.CurrentSessionId;
        try { FlightDriver.SetPause(true, false); } catch { }
        yield return new WaitForSecondsRealtime(1f);
        if (!FlightDriver.Pause && !Planetarium.Pause) throw new InvalidOperationException("Could not pause the resumed disposable Flight world before seq3 save.");
        double fixedUt = Planetarium.GetUniversalTime();
        yield return new WaitForSecondsRealtime(2f);
        double utAtSave = Planetarium.GetUniversalTime();
        if (Math.Abs(utAtSave - fixedUt) > 0.001) throw new InvalidOperationException("UT did not remain fixed before saving resumed seq3.");
        Line("FIXED_UT_RESUME preSaveBegin=" + F(fixedUt) + " preSaveEnd=" + F(utAtSave) + " flightDriverPause=" + FlightDriver.Pause + " planetariumPause=" + Planetarium.Pause + " timeScale=" + F(Time.timeScale));
        GamePersistence.SaveGame(SaveName, saveFolder, SaveMode.OVERWRITE);
        Line("SAVED_RESUME worldId=" + state.WorldId + " runId=" + runBeforeReload + " loadEpoch=" + epochBeforeReload + " counter=" + state.Counter +
            " sequence=" + state.AcceptedSequence + " ut=" + F(utAtSave) + " saveFolder=" + saveFolder + " saveName=" + SaveName);

        levelReadySeen = false;
        reloadFlightReadySeen = false;
        reloadLoadSeen = false;
        reloadLoadUt = reloadLevelReadyUt = reloadFlightReadyUt = null;
        reloadLoadRunId = reloadLoadEpoch = reloadLevelReadyRunId = reloadLevelReadyEpoch = reloadFlightReadyRunId = reloadFlightReadyEpoch = null;
        Game resumeReloadGame = GamePersistence.LoadGame(SaveName, saveFolder, true, false);
        if (resumeReloadGame == null) throw new InvalidOperationException("Could not reload the resumed seq3 save.");
        HighLogic.CurrentGame = resumeReloadGame;
        resumeReloadGame.startScene = GameScenes.FLIGHT;
        resumeReloadGame.Start();
        yield return WaitForReload(saveFolder, resumeReloadGame, runBeforeReload, ExpectedResumeWorldId, 3, 3, thirdReceipt.OperationId, false,
            sessionBeforeReload, epochBeforeReload, state, 180f);

        bridge = FindObjectOfType<WorldBridgeAddon>();
        state = ReadState();
        if (bridge == null || state == null || state.WorldId != ExpectedResumeWorldId || state.Counter != 3 || state.AcceptedSequence != 3 || state.Receipts.Length != 3 ||
            state.Receipts[0].OperationId != priorOperationId1 || state.Receipts[1].OperationId != priorOperationId2 || state.Receipts[2].OperationId != thirdReceipt.OperationId)
            throw new InvalidOperationException("Seq3 same-save reload did not preserve the exact accepted receipt chain.");
        if (bridge.CurrentRunId == runBeforeReload || bridge.CurrentLoadEpoch == epochBeforeReload || bridge.CurrentSessionId != sessionBeforeReload ||
            System.Diagnostics.Process.GetCurrentProcess().Id != currentProcessId)
            throw new InvalidOperationException("Seq3 same-process reload did not preserve process/session or fence a fresh run/load epoch.");
        double utAtReady = Planetarium.GetUniversalTime();
        FlightDriver.SetPause(true, false);
        double utAfterPause = Planetarium.GetUniversalTime();
        Line("RELOADED_RESUME worldId=" + state.WorldId + " processId=" + currentProcessId + " sessionId=" + bridge.CurrentSessionId + " runId=" + bridge.CurrentRunId +
            " previousRunId=" + runBeforeReload + " loadEpoch=" + bridge.CurrentLoadEpoch + " previousLoadEpoch=" + epochBeforeReload + " counter=" + state.Counter +
            " sequence=" + state.AcceptedSequence + " receipts=" + state.Receipts.Length + " utSaved=" + F(utAtSave) + " utAtReady=" + F(utAtReady) +
            " utAfterPauseCall=" + F(utAfterPause) + " sameUt=" + (Math.Abs(utAfterPause - utAtSave) <= 0.001).ToString().ToLowerInvariant() +
            " loadEvent=" + reloadLoadSeen.ToString().ToLowerInvariant() + " guiReady=" + levelReadySeen.ToString().ToLowerInvariant() + " flightReady=" + reloadFlightReadySeen.ToString().ToLowerInvariant());
        Line("PASS_RESUME priorToken=" + priorToken + " resumeToken=" + requestToken + " worldId=" + state.WorldId + " counter=" + state.Counter +
            " sequence=" + state.AcceptedSequence + " receiptChain=seq1,seq2,seq3 freshKspProcess=true selectedCopiedSave=" + saveFolder);
        Cleanup();
        Application.Quit();
    }

    IEnumerator WaitForWorld(string expectedFolder, float timeout)
    {
        float until = Time.realtimeSinceStartup + timeout;
        int stable = 0;
        while (Time.realtimeSinceStartup < until)
        {
            WorldBridgeAddon bridge = FindObjectOfType<WorldBridgeAddon>();
            RecoveryCapsuleModule recovery = RecoveryCapsuleModule.Instance;
            bool okay = HighLogic.LoadedScene == GameScenes.SPACECENTER && HighLogic.CurrentGame != null && HighLogic.SaveFolder == expectedFolder &&
                bridge != null && !bridge.IsLoadUnresolved && recovery != null && recovery.IsLoaded && !recovery.IsCorrupt && recovery.HasAcceptedState &&
                DepotRegistryModule.Instance != null && DepotRegistryModule.Instance.IsReady && !DepotRegistryModule.Instance.IsCorrupt && bridge.CanAcceptEffects;
            stable = okay ? stable + 1 : 0;
            if (stable >= 5) yield break;
            yield return null;
        }
        throw new TimeoutException("Disposable Space Center world or its recovery context did not become ready.");
    }

    IEnumerator WaitForResumeReady(Game expectedGame, int currentProcessId, float timeout)
    {
        float until = Time.realtimeSinceStartup + timeout;
        int stable = 0;
        while (Time.realtimeSinceStartup < until)
        {
            WorldBridgeAddon bridge = FindObjectOfType<WorldBridgeAddon>();
            RecoveryCapsuleModule recovery = RecoveryCapsuleModule.Instance;
            AcceptedState state = ReadState();
            bool acceptedPair = state != null && state.WorldId == ExpectedResumeWorldId && state.Counter == 2 && state.AcceptedSequence == 2 &&
                state.Revision == 2 && state.Receipts.Length == 2 && state.Receipts[0].CommandSequence == 1 && state.Receipts[1].CommandSequence == 2 &&
                state.Receipts[0].OperationId == priorOperationId1 && state.Receipts[1].OperationId == priorOperationId2 &&
                state.Receipts[0].Outcome == "accepted" && state.Receipts[1].Outcome == "accepted";
            bool eventsReady = reloadLoadSeen && (levelReadySeen || reloadFlightReadySeen);
            bool okay = HighLogic.LoadedScene == GameScenes.FLIGHT && HighLogic.CurrentGame == expectedGame && HighLogic.SaveFolder == saveFolder &&
                FlightGlobals.ActiveVessel != null && FlightGlobals.ActiveVessel.loaded && eventsReady && bridge != null && !bridge.IsLoadUnresolved &&
                bridge.CanAcceptEffects && recovery != null && recovery.IsLoaded && !recovery.IsCorrupt && recovery.HasAcceptedState &&
                DepotRegistryModule.Instance != null && DepotRegistryModule.Instance.IsReady && !DepotRegistryModule.Instance.IsCorrupt && acceptedPair &&
                currentProcessId != priorProcessId && bridge.CurrentSessionId != priorSessionId && bridge.CurrentRunId != priorRunId && bridge.CurrentLoadEpoch != priorLoadEpoch;
            stable = okay ? stable + 1 : 0;
            if (stable >= 5) yield break;
            yield return null;
        }
        WorldBridgeAddon lastBridge = FindObjectOfType<WorldBridgeAddon>();
        AcceptedState lastState = ReadState();
        Line("RESUME_READY_TIMEOUT scene=" + HighLogic.LoadedScene + " currentGameMatches=" + (HighLogic.CurrentGame == expectedGame).ToString().ToLowerInvariant() +
            " saveFolder=" + HighLogic.SaveFolder + " processId=" + currentProcessId + " priorProcessId=" + priorProcessId +
            " sessionId=" + (lastBridge == null ? "missing" : lastBridge.CurrentSessionId) + " runId=" + (lastBridge == null ? "missing" : lastBridge.CurrentRunId) +
            " loadEpoch=" + (lastBridge == null ? "missing" : lastBridge.CurrentLoadEpoch) + " loadUnresolved=" + (lastBridge == null ? "unknown" : lastBridge.IsLoadUnresolved.ToString().ToLowerInvariant()) +
            " canAcceptEffects=" + (lastBridge == null ? "unknown" : lastBridge.CanAcceptEffects.ToString().ToLowerInvariant()) +
            " worldId=" + (lastState == null ? "missing" : lastState.WorldId) + " counter=" + (lastState == null ? "missing" : lastState.Counter.ToString(CultureInfo.InvariantCulture)) +
            " sequence=" + (lastState == null ? "missing" : lastState.AcceptedSequence.ToString(CultureInfo.InvariantCulture)) +
            " receiptCount=" + (lastState == null ? "missing" : lastState.Receipts.Length.ToString(CultureInfo.InvariantCulture)) +
            " gameLoad=" + reloadLoadSeen.ToString().ToLowerInvariant() + " guiReady=" + levelReadySeen.ToString().ToLowerInvariant() + " flightReady=" + reloadFlightReadySeen.ToString().ToLowerInvariant());
        throw new TimeoutException("Copied selected save did not reach the exact seq2 capsule and fresh KSP process/session/run readiness.");
    }

    void LoadPriorRunIdentity(string token)
    {
        string path = Path.Combine(root, "ExpanseRecoveryM3aSmoke-" + token + ".log");
        AssertNoReparseAncestors(path);
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new FileNotFoundException("Prior staged run log is missing or unsafe.", path);
        FileInfo info = new FileInfo(path);
        if (info.Length < 1 || info.Length > 512 * 1024) throw new InvalidDataException("Prior staged run log exceeds its 512 KiB bound.");
        string log = File.ReadAllText(path);
        if (log.IndexOf("PASS Host restart recovery", StringComparison.Ordinal) < 0) throw new InvalidDataException("Prior run log does not contain the completed staged-recovery PASS marker.");
        Dictionary<string, string> ready = MarkerFields(log, "READY");
        Dictionary<string, string> restarted = MarkerFields(log, "HOST_RESTART_CONFIRMED");
        Dictionary<string, string> secondReady = MarkerFields(log, "SECOND_READY");
        Dictionary<string, string> applied1 = MarkerFields(log, "APPLIED");
        Dictionary<string, string> applied2 = MarkerFields(log, "APPLIED_AFTER_HOST_RESTART");
        Dictionary<string, string> reloaded = MarkerFields(log, "RELOAD_AFTER_HOST_RESTART");
        string worldId = Required(ready, "worldId");
        if (!string.Equals(worldId, ExpectedResumeWorldId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Required(restarted, "token"), token, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Required(secondReady, "worldId"), worldId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Required(reloaded, "worldId"), worldId, StringComparison.OrdinalIgnoreCase) ||
            Required(reloaded, "counter") != "2" || Required(reloaded, "sequence") != "2" ||
            !string.Equals(Required(applied2, "operationId"), Required(reloaded, "receiptOperationId"), StringComparison.Ordinal))
            throw new InvalidDataException("Prior run log markers do not prove the approved world and accepted seq2 save.");
        priorProcessId = int.Parse(Required(restarted, "kspPid"), NumberStyles.None, CultureInfo.InvariantCulture);
        if (priorProcessId <= 0) throw new InvalidDataException("Prior KSP process ID is invalid.");
        priorSessionId = Required(secondReady, "sessionId");
        priorRunId = Required(reloaded, "runId");
        priorLoadEpoch = Required(reloaded, "loadEpoch");
        Guid parsed;
        if (!Guid.TryParse(priorSessionId, out parsed) || !Guid.TryParse(priorRunId, out parsed) || !Guid.TryParse(priorLoadEpoch, out parsed))
            throw new InvalidDataException("Prior KSP process/session/run identity markers are invalid.");
        priorOperationId1 = Required(applied1, "operationId");
        priorOperationId2 = Required(applied2, "operationId");
        if (priorOperationId1.Length != 68 || priorOperationId2.Length != 68) throw new InvalidDataException("Prior accepted operation identifiers have invalid lengths.");
    }

    static Dictionary<string, string> MarkerFields(string text, string marker)
    {
        string needle = " " + marker + " ";
        string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            int index = lines[lineIndex].IndexOf(needle, StringComparison.Ordinal);
            if (index < 0) continue;
            string fieldsText = lines[lineIndex].Substring(index + needle.Length);
            Dictionary<string, string> fields = new Dictionary<string, string>(StringComparer.Ordinal);
            string[] pieces = fieldsText.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < pieces.Length; i++)
            {
                int equals = pieces[i].IndexOf('=');
                if (equals <= 0) continue;
                fields[pieces[i].Substring(0, equals)] = pieces[i].Substring(equals + 1);
            }
            return fields;
        }
        throw new InvalidDataException("Prior run log marker is missing: " + marker);
    }

    static string Required(Dictionary<string, string> fields, string key)
    {
        string value;
        if (fields == null || !fields.TryGetValue(key, out value) || string.IsNullOrWhiteSpace(value)) throw new InvalidDataException("Prior run log marker lacks " + key + ".");
        return value;
    }

    static string FileSha256(string path)
    {
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new FileNotFoundException("Expected regular save file is missing or unsafe.", path);
        FileInfo info = new FileInfo(path);
        if (info.Length < 1 || info.Length > 32L * 1024 * 1024) throw new InvalidDataException("Selected KSP save file is outside the 32 MiB bound.");
        using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (SHA256 sha = SHA256.Create())
        {
            byte[] hash = sha.ComputeHash(stream);
            return BitConverter.ToString(hash).Replace("-", string.Empty);
        }
    }

    static void ValidateTreeNoReparse(string rootPath, int maxEntries, long maxBytes)
    {
        string rootFull = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        AssertNoReparseAncestors(rootFull);
        if (!Directory.Exists(rootFull)) throw new DirectoryNotFoundException("Expected KSP save folder is absent: " + rootFull);
        Stack<string> pending = new Stack<string>();
        pending.Push(rootFull);
        int count = 0;
        long totalBytes = 0;
        while (pending.Count != 0)
        {
            string directory = pending.Pop();
            string[] entries = Directory.GetFileSystemEntries(directory);
            for (int i = 0; i < entries.Length; i++)
            {
                if (++count > maxEntries) throw new InvalidDataException("KSP save tree exceeds its entry bound.");
                string entry = Path.GetFullPath(entries[i]);
                if (!entry.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("KSP save tree entry escapes its token-derived root.");
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse point found inside a token-derived KSP save tree.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                else
                {
                    totalBytes += new FileInfo(entry).Length;
                    if (totalBytes > maxBytes) throw new InvalidDataException("KSP save tree exceeds its byte bound.");
                }
            }
        }
    }

    IEnumerator WaitForReload(string expectedFolder, Game expectedGame, string previousRunId, string expectedWorldId, long expectedCounter, long expectedSequence,
        string expectedOperationId, bool probeStaleRun, string previousSessionId, string previousEpoch, AcceptedState priorState, float timeout)
    {
        float until = Time.realtimeSinceStartup + timeout;
        int stable = 0;
        bool staleProbeDone = false;
        while (Time.realtimeSinceStartup < until)
        {
            WorldBridgeAddon bridge = FindObjectOfType<WorldBridgeAddon>();
            RecoveryCapsuleModule recovery = RecoveryCapsuleModule.Instance;
            AcceptedState state = ReadState();
            bool freshExecution = bridge != null && !string.Equals(bridge.CurrentRunId, previousRunId, StringComparison.OrdinalIgnoreCase);
            bool vesselReady = HighLogic.LoadedSceneIsFlight && FlightGlobals.ActiveVessel != null && FlightGlobals.ActiveVessel.loaded;
            bool flightReadyEvent = levelReadySeen || reloadFlightReadySeen;
            bool acceptedWitness = state != null && state.WorldId == expectedWorldId && state.Counter == expectedCounter && state.AcceptedSequence == expectedSequence &&
                state.Receipts.Length == expectedSequence && state.Receipts[state.Receipts.Length - 1].OperationId == expectedOperationId;
            bool probeContextReady = probeStaleRun && !staleProbeDone && HighLogic.LoadedScene == GameScenes.FLIGHT && HighLogic.CurrentGame == expectedGame &&
                reloadLoadSeen && flightReadyEvent && freshExecution && acceptedWitness && recovery != null && recovery.HasAcceptedState;
            if (probeContextReady)
            {
                ProbeStaleRunProposal(bridge, priorState, previousSessionId, previousRunId, previousEpoch);
                staleProbeDone = true;
            }
            bool ready = HighLogic.LoadedScene == GameScenes.FLIGHT && vesselReady && HighLogic.CurrentGame == expectedGame && HighLogic.SaveFolder == expectedFolder &&
                reloadLoadSeen && flightReadyEvent && freshExecution && bridge != null && !bridge.IsLoadUnresolved && recovery != null && recovery.IsLoaded && !recovery.IsCorrupt &&
                recovery.HasAcceptedState && DepotRegistryModule.Instance != null && DepotRegistryModule.Instance.IsReady && !DepotRegistryModule.Instance.IsCorrupt &&
                bridge.CanAcceptEffects && acceptedWitness && (!probeStaleRun || staleProbeDone);
            stable = ready ? stable + 1 : 0;
            if (stable >= 5) yield break;
            yield return null;
        }
        WorldBridgeAddon lastBridge = FindObjectOfType<WorldBridgeAddon>();
        AcceptedState lastState = ReadState();
        Line("RELOAD_WAIT_TIMEOUT scene=" + HighLogic.LoadedScene + " currentGameMatches=" + (HighLogic.CurrentGame == expectedGame).ToString().ToLowerInvariant() + " saveFolder=" + HighLogic.SaveFolder +
            " runId=" + (lastBridge == null ? "missing" : lastBridge.CurrentRunId) + " previousRunId=" + previousRunId +
            " loadUnresolved=" + (lastBridge == null ? "unknown" : lastBridge.IsLoadUnresolved.ToString().ToLowerInvariant()) +
            " worldId=" + (lastState == null ? "missing" : lastState.WorldId) + " counter=" + (lastState == null ? "missing" : lastState.Counter.ToString(CultureInfo.InvariantCulture)) +
            " sequence=" + (lastState == null ? "missing" : lastState.AcceptedSequence.ToString(CultureInfo.InvariantCulture)) +
            " receiptCount=" + (lastState == null ? "missing" : lastState.Receipts.Length.ToString(CultureInfo.InvariantCulture)) +
            " receiptOperationId=" + (lastState == null || lastState.Receipts.Length == 0 ? "none" : lastState.Receipts[0].OperationId) +
            " gameStateLoad=" + reloadLoadSeen.ToString().ToLowerInvariant() + " guiReady=" + levelReadySeen.ToString().ToLowerInvariant() + " flightReady=" + reloadFlightReadySeen.ToString().ToLowerInvariant());
        throw new TimeoutException("Same-save Flight reload did not restore the accepted witness on the selected Game after readiness events.");
    }

    void ProbeStaleRunProposal(WorldBridgeAddon bridge, AcceptedState priorState, string priorSessionId, string priorRunId, string priorEpoch)
    {
        if (bridge == null || priorState == null || priorState.AcceptedSequence != 2 || priorState.Counter != 2 || priorState.Receipts == null || priorState.Receipts.Length != 2)
            throw new InvalidOperationException("Stale-run probe requires the exact pre-reload seq2/counter2 accepted prefix.");
        RecoveryCapsuleModule recovery = RecoveryCapsuleModule.Instance;
        if (recovery == null) throw new InvalidOperationException("Recovery capsule module missing during stale-run probe.");
        byte[] before = recovery.GetAcceptedStateBytes();
        if (before == null || !BytesEqual(before, AcceptedStateCodec.Serialize(priorState)))
            throw new InvalidOperationException("Current capsule bytes do not match the seq2 state before stale-run proposal probe.");

        long sequence = priorState.AcceptedSequence + 1;
        string clientRequestId = "m3a-stale-run-" + requestToken;
        EffectProposal proposal = new EffectProposal
        {
            ProtocolVersion = 1,
            MessageType = "effectProposal",
            SessionId = Guid.Parse(priorSessionId),
            LoadEpoch = Guid.Parse(priorEpoch),
            InstallNamespace = Path.GetFullPath(KSPUtil.ApplicationRootPath),
            SaveFolder = saveFolder,
            WorldId = priorState.WorldId,
            RunId = priorRunId,
            OperationId = OperationIdentity.Create(priorState.WorldId, sequence, clientRequestId),
            ClientRequestId = clientRequestId,
            CommandSequence = sequence,
            ExpectedRevision = priorState.Revision,
            ExpectedStateHash = AcceptedStateCodec.ComputeHash(priorState),
            PayloadHash = OperationIdentity.CounterIncrementPayloadHash(1),
            OperationKind = "counterIncrement",
            CounterDelta = 1,
            TargetCompactionWatermark = 0
        };

        MethodInfo apply = typeof(WorldBridgeAddon).GetMethod("ApplyEffectProposal", BindingFlags.Instance | BindingFlags.NonPublic);
        if (apply == null) throw new MissingMethodException(typeof(WorldBridgeAddon).FullName, "ApplyEffectProposal");
        EffectReceipt receiptResult;
        try { receiptResult = apply.Invoke(bridge, new object[] { proposal }) as EffectReceipt; }
        catch (TargetInvocationException ex) { throw new InvalidOperationException("Stale-run proposal invocation failed.", ex.InnerException ?? ex); }
        if (receiptResult == null) throw new InvalidOperationException("Stale-run proposal returned no typed receipt.");
        if (receiptResult.Outcome != "rejected" && receiptResult.Outcome != "held")
            throw new InvalidOperationException("Stale-run proposal was not rejected/held: " + receiptResult.Outcome);
        byte[] after = recovery.GetAcceptedStateBytes();
        AcceptedState afterState = after == null ? null : AcceptedStateCodec.Deserialize(after);
        if (!BytesEqual(before, after) || afterState == null || afterState.Counter != 2 || afterState.AcceptedSequence != 2 || afterState.Receipts.Length != 2)
            throw new InvalidOperationException("Stale-run proposal changed accepted capsule bytes or seq2 counter state.");

        double ut;
        try { ut = Planetarium.GetUniversalTime(); } catch { ut = -1; }
        Line("STALE_RUN_PROBE outcome=" + receiptResult.Outcome + " reason=" + (receiptResult.Reason ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ') +
            " canAcceptEffects=" + bridge.CanAcceptEffects.ToString().ToLowerInvariant() + " loadUnresolved=" + bridge.IsLoadUnresolved.ToString().ToLowerInvariant() +
            " oldRunId=" + priorRunId + " liveRunId=" + bridge.CurrentRunId + " oldLoadEpoch=" + priorEpoch + " liveLoadEpoch=" + bridge.CurrentLoadEpoch +
            " ut=" + F(ut) + " counter=" + afterState.Counter + " sequence=" + afterState.AcceptedSequence + " capsuleBytesIdentical=true");
    }

    static bool BytesEqual(byte[] left, byte[] right)
    {
        if (left == null || right == null || left.Length != right.Length) return false;
        for (int i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
        return true;
    }

    IEnumerator WaitForFlightWorld(string expectedFolder, float timeout)
    {
        float until = Time.realtimeSinceStartup + timeout;
        int stable = 0;
        while (Time.realtimeSinceStartup < until)
        {
            WorldBridgeAddon bridge = FindObjectOfType<WorldBridgeAddon>();
            RecoveryCapsuleModule recovery = RecoveryCapsuleModule.Instance;
            bool okay = HighLogic.LoadedScene == GameScenes.FLIGHT && HighLogic.CurrentGame != null && HighLogic.SaveFolder == expectedFolder &&
                FlightGlobals.ActiveVessel != null && FlightGlobals.ActiveVessel.loaded && bridge != null && !bridge.IsLoadUnresolved &&
                recovery != null && recovery.IsLoaded && !recovery.IsCorrupt && recovery.HasAcceptedState &&
                DepotRegistryModule.Instance != null && DepotRegistryModule.Instance.IsReady && !DepotRegistryModule.Instance.IsCorrupt && bridge.CanAcceptEffects;
            stable = okay ? stable + 1 : 0;
            if (stable >= 5) yield break;
            yield return null;
        }
        throw new TimeoutException("Disposable Flight world or its recovery context did not become ready.");
    }

    IEnumerator WaitForHostRestartSignal(int timeoutSeconds)
    {
        string expected = "HOST_RESTARTED=" + requestToken;
        float until = Time.realtimeSinceStartup + timeoutSeconds;
        while (Time.realtimeSinceStartup < until)
        {
            if (File.Exists(stagePath))
            {
                AssertNoReparseAncestors(stagePath);
                string content = File.ReadAllText(stagePath).Trim();
                if (string.Equals(content, expected, StringComparison.Ordinal))
                {
                    File.Delete(stagePath);
                    if (File.Exists(stagePath)) throw new IOException("Could not consume the one-shot Host restart signal.");
                    Line("HOST_RESTART_CONFIRMED controlConsumed=true token=" + requestToken + " kspPid=" + System.Diagnostics.Process.GetCurrentProcess().Id +
                        " runId=" + (FindObjectOfType<WorldBridgeAddon>() == null ? "missing" : FindObjectOfType<WorldBridgeAddon>().CurrentRunId));
                    yield break;
                }
                if (content.StartsWith("HOST_RESTARTED=", StringComparison.Ordinal) && !string.Equals(content, expected, StringComparison.Ordinal))
                    throw new InvalidDataException("Host restart signal token does not match the consumed fixture request.");
            }
            yield return new WaitForSecondsRealtime(0.25f);
        }
        throw new TimeoutException("Timed out waiting for Sol's one-shot Host restart control file.");
    }

    IEnumerator WaitForScene(GameScenes scene, float seconds)
    {
        float until = Time.realtimeSinceStartup + seconds;
        while (Time.realtimeSinceStartup < until)
        {
            if (HighLogic.LoadedSceneIsGame && HighLogic.LoadedScene == scene) yield break;
            yield return null;
        }
        throw new TimeoutException("Timed out waiting for scene " + scene + "; current=" + HighLogic.LoadedScene);
    }

    IEnumerator WaitForUsableVessel(float seconds)
    {
        float until = Time.realtimeSinceStartup + seconds;
        while (Time.realtimeSinceStartup < until)
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (HighLogic.LoadedSceneIsFlight && vessel != null && vessel.loaded && !vessel.packed && vessel.parts != null && vessel.parts.Count > 0) yield break;
            yield return null;
        }
        throw new TimeoutException("No loaded, unpacked disposable fixture vessel became available.");
    }

    AcceptedState ReadState()
    {
        RecoveryCapsuleModule recovery = RecoveryCapsuleModule.Instance;
        if (recovery == null || !recovery.HasAcceptedState) return null;
        return AcceptedStateCodec.Deserialize(recovery.GetAcceptedStateBytes());
    }

    void VerifyCapsuleBoundarySynthetic()
    {
        string world = Guid.NewGuid().ToString("D");
        AcceptedState seed = new AcceptedState { WorldId = world, CheckpointId = Guid.NewGuid().ToString("N") };
        RecoveryCapsule capsule = AcceptedStateCodec.CreateCapsule(seed);
        ConfigNode validInput = new ConfigNode("MODULE");
        ConfigNode valid = validInput.AddNode("EXPANSE_RECOVERY");
        valid.AddValue("schemaVersion", capsule.SchemaVersion);
        valid.AddValue("worldId", capsule.WorldId);
        valid.AddValue("stateBytesBase64", capsule.StateBytesBase64);
        valid.AddValue("stateSha256", capsule.StateSha256);
        RecoveryCapsuleModule probe = new RecoveryCapsuleModule();
        probe.OnLoad(validInput);
        if (probe.IsCorrupt || !probe.HasAcceptedState || probe.WorldId != world) throw new InvalidOperationException("Synthetic valid capsule load failed.");
        ConfigNode saved = new ConfigNode("MODULE");
        probe.OnSave(saved);
        ConfigNode savedCapsule = saved.GetNode("EXPANSE_RECOVERY");
        if (savedCapsule == null || savedCapsule.GetValue("stateSha256") != capsule.StateSha256) throw new InvalidOperationException("Synthetic valid capsule save round-trip failed.");

        ConfigNode corruptInput = new ConfigNode("MODULE");
        ConfigNode unknown = corruptInput.AddNode("EXPANSE_RECOVERY");
        unknown.AddValue("schemaVersion", 77);
        unknown.AddValue("futureField", "must remain verbatim");
        ConfigNode nested = unknown.AddNode("futureNested");
        nested.AddValue("payload", "raw");
        probe.OnLoad(corruptInput);
        if (!probe.IsCorrupt || probe.HasAcceptedState || !probe.HoldReason.Contains("Unsupported recovery schema")) throw new InvalidOperationException("Synthetic unsupported capsule was not safely held.");
        ConfigNode preserved = new ConfigNode("MODULE");
        probe.OnSave(preserved);
        ConfigNode raw = preserved.GetNode("EXPANSE_RECOVERY");
        if (raw == null || raw.ToString() != unknown.ToString()) throw new InvalidOperationException("Synthetic unknown recovery node was not preserved verbatim.");
        probe.OnLoad(new ConfigNode("MODULE"));
        if (probe.IsCorrupt || probe.HasAcceptedState || probe.WorldId != null) throw new InvalidOperationException("Synthetic absent capsule did not clear previously loaded state.");
        Line("SYNTHETIC_CAPSULE validRoundTrip=true unknownSchemaPreserved=true corruptHeld=true absentCleared=true");
    }

    void OnLevelReady(GameScenes scene)
    {
        if (scene == GameScenes.FLIGHT)
        {
            levelReadySeen = true;
            CaptureReloadObservation(out reloadLevelReadyUt, out reloadLevelReadyRunId, out reloadLevelReadyEpoch);
        }
    }

    void OnGameLoad(ConfigNode ignored)
    {
        reloadLoadSeen = true;
        CaptureReloadObservation(out reloadLoadUt, out reloadLoadRunId, out reloadLoadEpoch);
    }

    void OnFlightReady()
    {
        if (!HighLogic.LoadedSceneIsFlight) return;
        reloadFlightReadySeen = true;
        CaptureReloadObservation(out reloadFlightReadyUt, out reloadFlightReadyRunId, out reloadFlightReadyEpoch);
    }

    void CaptureReloadObservation(out double? ut, out string runId, out string epoch)
    {
        ut = null; runId = null; epoch = null;
        try
        {
            double value = Planetarium.GetUniversalTime();
            if (!double.IsNaN(value) && !double.IsInfinity(value)) ut = value;
        }
        catch { }
        try
        {
            WorldBridgeAddon bridge = FindObjectOfType<WorldBridgeAddon>();
            if (bridge != null) { runId = bridge.CurrentRunId; epoch = bridge.CurrentLoadEpoch; }
        }
        catch { }
    }

    void Cleanup()
    {
        GameEvents.onGameStateLoad.Remove(OnGameLoad);
        GameEvents.onLevelWasLoadedGUIReady.Remove(OnLevelReady);
        GameEvents.onFlightReady.Remove(OnFlightReady);
    }

    void Line(string message)
    {
        string line = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " " + message;
        File.AppendAllText(logPath, line + Environment.NewLine);
        Debug.Log("[ExpanseRecoveryM3aSmoke] " + message);
    }

    static string F(double value) { return value.ToString("R", CultureInfo.InvariantCulture); }
    static string N(double? value) { return value.HasValue ? F(value.Value) : "unavailable"; }
    static string V(string value) { return string.IsNullOrEmpty(value) ? "unavailable" : value; }
    static bool FenceAtSameUt(double? observedUt, string observedRunId, string observedEpoch, double savedUt, string priorRunId, string priorEpoch)
    {
        return observedUt.HasValue && Math.Abs(observedUt.Value - savedUt) <= 0.001 &&
            !string.IsNullOrEmpty(observedRunId) && !string.Equals(observedRunId, priorRunId, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrEmpty(observedEpoch) && !string.Equals(observedEpoch, priorEpoch, StringComparison.OrdinalIgnoreCase);
    }

    static void AssertNoReparseAncestors(string path)
    {
        string full = Path.GetFullPath(path);
        string current = full;
        while (!string.IsNullOrEmpty(current))
        {
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Refusing a reparse point in fixture path: " + current);
            if (File.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Refusing a reparse point in fixture path: " + current);
            string parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent;
        }
    }
}
