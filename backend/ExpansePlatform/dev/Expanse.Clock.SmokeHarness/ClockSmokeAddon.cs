using System;
using System.Globalization;
using System.IO;
using UnityEngine;

[KSPAddon(KSPAddon.Startup.MainMenu, true)]
public sealed class ClockSmokeAddon : MonoBehaviour
{
    const string RequiredRoot = @"C:\Users\griff\Documents\KSP-RMM-Dev";
    const string RequestName = "ExpanseClockSmoke.request";
    string root, logPath, saveFolder, saveName, craftPath;
    bool started;

    void Start()
    {
        bool requestAccepted = false;
        try
        {
            root = Path.GetFullPath(KSPUtil.ApplicationRootPath).TrimEnd('\\', '/');
            if (!string.Equals(root, RequiredRoot, StringComparison.OrdinalIgnoreCase)) return;
            string requestPath = Path.Combine(root, RequestName);
            if (!File.Exists(requestPath) || (File.GetAttributes(requestPath) & FileAttributes.ReparsePoint) != 0) return;
            string request = File.ReadAllText(requestPath).Trim();
            if (!request.StartsWith("RUN_EXPANSE_CLOCK_SMOKE=", StringComparison.Ordinal) || request.Length != "RUN_EXPANSE_CLOCK_SMOKE=".Length + 32) return;
            Guid requestId;
            if (!Guid.TryParseExact(request.Substring("RUN_EXPANSE_CLOCK_SMOKE=".Length), "N", out requestId)) return;
            logPath = Path.Combine(root, "ExpanseClockSmoke-" + requestId.ToString("N") + ".log");
            requestAccepted = true;
            File.Delete(requestPath); // one-shot request; exact path checked above
            File.WriteAllText(logPath, "START " + DateTime.UtcNow.ToString("O") + "\r\n");
            AudioListener.volume = 0f;
            saveFolder = "ExpanseClockSmoke-" + Guid.NewGuid().ToString("N");
            saveName = "clock-smoke";
            string requestIdText = requestId.ToString("N");
            string stockCraft = Path.GetFullPath(Path.Combine(root, "GameData", "SquadExpansion", "MakingHistory", "Ships", "VAB", "Muna 1.craft"));
            craftPath = Path.GetFullPath(Path.Combine(root, "ExpanseClockSmoke-" + requestIdText + ".craft"));
            if (!stockCraft.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(stockCraft)) throw new FileNotFoundException("Expected installed stock craft was not found.", stockCraft);
            if (File.Exists(craftPath) || Directory.Exists(craftPath)) throw new IOException("Unique disposable craft path already exists.");
            File.Copy(stockCraft, craftPath, false);
            string savesRoot = Path.GetFullPath(Path.Combine(root, "saves"));
            string newSavePath = Path.GetFullPath(Path.Combine(savesRoot, saveFolder));
            if (!newSavePath.StartsWith(savesRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || Directory.Exists(newSavePath) || File.Exists(newSavePath)) throw new IOException("Unique disposable save path collided or escaped the saves directory.");
            DontDestroyOnLoad(gameObject);
            GameEvents.onGameStateLoad.Add(OnGameStateLoad);
            GameEvents.onGameStatePostLoad.Add(OnGameStatePostLoad);
            started = true;
            StartCoroutine(Supervise(Run()));
        }
        catch (Exception ex)
        {
            if (requestAccepted) { TryRestoreRealtime(); Line("FAIL initialization " + ex); Application.Quit(); }
        }
    }

    void OnGameStateLoad(ConfigNode node) { Line("KSP_EVENT onGameStateLoad folder=" + HighLogic.SaveFolder + " scene=" + HighLogic.LoadedScene); }
    void OnGameStatePostLoad(ConfigNode node) { Line("KSP_EVENT onGameStatePostLoad folder=" + HighLogic.SaveFolder + " scene=" + HighLogic.LoadedScene); }
    void OnDestroy()
    {
        if (!started) return;
        GameEvents.onGameStateLoad.Remove(OnGameStateLoad); GameEvents.onGameStatePostLoad.Remove(OnGameStatePostLoad);
    }

    System.Collections.IEnumerator Supervise(System.Collections.IEnumerator routine)
    {
        while (true)
        {
            object current; bool more;
            try { more = routine.MoveNext(); current = more ? routine.Current : null; }
            catch (Exception ex)
            {
                TryRestoreRealtime();
                LogBridgeMetrics("FAIL");
                Line("FAIL " + ex);
                Application.Quit();
                yield break;
            }
            if (!more) yield break;
            yield return current;
        }
    }

    System.Collections.IEnumerator Run()
    {
        Line("REQUEST accepted; root=" + root + "; saveFolder=" + saveFolder + "; saveName=" + saveName);
        yield return new WaitForSecondsRealtime(4f);
        HighLogic.SaveFolder = saveFolder;
        HighLogic.CurrentGame = GamePersistence.CreateNewGame(saveFolder, Game.Modes.SANDBOX, new GameParameters(), "Squad/Flags/default", GameScenes.SPACECENTER, EditorFacility.VAB);
        if (HighLogic.CurrentGame == null) throw new InvalidOperationException("CreateNewGame returned null.");
        HighLogic.CurrentGame.Start();
        yield return WaitForScene(GameScenes.SPACECENTER, 120f, "initial new-world load");
        yield return new WaitForSecondsRealtime(2f);
        LogCheckpoint("new-world-space-center", "new-world epoch expected before stock craft launch");
        FlightDriver.StartWithNewLaunch(craftPath, "Squad/Flags/default", "LaunchPad", new VesselCrewManifest());
        yield return WaitForScene(GameScenes.FLIGHT, 180f, "stock craft launch");
        yield return WaitForFlightVessel(180f);
        LogCheckpoint("flight-craft-live", "launched copied stock Muna 1 craft from dev install");

        double beforePause = Planetarium.GetUniversalTime();
        FlightDriver.SetPause(true, false);
        yield return new WaitForSecondsRealtime(4f);
        bool pausedObserved = Planetarium.Pause;
        bool flightDriverPaused = FlightDriver.Pause;
        float timeScaleDuringPause = Time.timeScale;
        double pausedUt = Planetarium.GetUniversalTime();
        Line("PAUSE heartbeat window: flightDriverPause=" + flightDriverPaused + " planetariumPause=" + pausedObserved + " timeScale=" + F(timeScaleDuringPause) + " utBefore=" + F(beforePause) + " utAfter=" + F(pausedUt));
        if (!flightDriverPaused && !pausedObserved) throw new InvalidOperationException("Neither FlightDriver.Pause nor Planetarium.Pause became true after FlightDriver.SetPause(true,false).");
        FlightDriver.SetPause(false, false);
        yield return new WaitForSecondsRealtime(1f);
        LogCheckpoint("unpaused", "same load epoch; heartbeat should continue");

        double beforeWarp = Planetarium.GetUniversalTime();
        TimeWarp.SetRate(5, true);
        yield return new WaitForSecondsRealtime(3f);
        double warped = Planetarium.GetUniversalTime();
        TimeWarp.SetRate(0, true);
        yield return new WaitForSecondsRealtime(1f);
        double returnedToOneX = Planetarium.GetUniversalTime();
        Line("WARP checkpoints: before=" + F(beforeWarp) + " atWarp=" + F(warped) + " afterReturn1x=" + F(returnedToOneX) + " rateIndex=" + TimeWarp.CurrentRateIndex);
        if (TimeWarp.CurrentRateIndex != 0) throw new InvalidOperationException("Time warp did not return to rate index 0 (1x).");
        LogCheckpoint("warp-returned-1x", "same load epoch");

        // Freeze UT around persistence so the Host can show an epoch transition at exactly the same time.
        double fixedUt = Planetarium.GetUniversalTime();
        FlightDriver.SetPause(true, false);
        yield return new WaitForSecondsRealtime(1f);
        LogPauseSignals("fixed-ut-preload");
        if (!FlightDriver.Pause && !Planetarium.Pause) throw new InvalidOperationException("Could not pause the flight world for the fixed-time persistence check.");
        Planetarium.SetUniversalTime(fixedUt);
        yield return new WaitForSecondsRealtime(3f);
        double preSaveUt = Planetarium.GetUniversalTime();
        if (Math.Abs(preSaveUt - fixedUt) > 0.001) throw new InvalidOperationException("UT did not remain fixed during the pre-load observation window.");
        Line("FIXED_UT preLoadWindowBegin=" + F(fixedUt) + " preLoadWindowEnd=" + F(preSaveUt) + " flightDriverPause=" + FlightDriver.Pause + " planetariumPause=" + Planetarium.Pause + " timeScale=" + F(Time.timeScale) + " durationRealSeconds=3");

        HighLogic.SaveFolder = saveFolder;
        GamePersistence.SaveGame(saveName, saveFolder, SaveMode.OVERWRITE);
        LogCheckpoint("saved", "fixed UT persisted; next operation reloads the same save folder/name");
        double beforeReload = Planetarium.GetUniversalTime();
        Game reloaded = GamePersistence.LoadGame(saveName, saveFolder, true, false);
        if (reloaded == null) throw new InvalidOperationException("LoadGame returned null for the just-created disposable save.");
        Line("RELOAD requested: sameFolder=" + saveFolder + " sameName=" + saveName + " utBefore=" + F(beforeReload) + " fixedUt=" + F(fixedUt) + " expectedLoadEpochChange=true");
        HighLogic.CurrentGame = reloaded;
        reloaded.startScene = GameScenes.FLIGHT;
        reloaded.Start();
        yield return WaitForScene(GameScenes.FLIGHT, 180f, "same-world flight reload");
        yield return WaitForFlightVessel(180f);
        FlightDriver.SetPause(true, false);
        yield return new WaitForSecondsRealtime(1f);
        LogPauseSignals("fixed-ut-postload");
        if (!FlightDriver.Pause && !Planetarium.Pause) throw new InvalidOperationException("Could not pause the reloaded flight world.");
        Planetarium.SetUniversalTime(fixedUt);
        yield return new WaitForSecondsRealtime(3f);
        double postLoadUt = Planetarium.GetUniversalTime();
        if (Math.Abs(postLoadUt - fixedUt) > 0.001) throw new InvalidOperationException("UT did not match the exact pre-load value after reload.");
        Line("FIXED_UT postLoadWindowBegin=" + F(fixedUt) + " postLoadWindowEnd=" + F(postLoadUt) + " flightDriverPause=" + FlightDriver.Pause + " planetariumPause=" + Planetarium.Pause + " timeScale=" + F(Time.timeScale) + " durationRealSeconds=3");
        LogCheckpoint("same-world-reloaded", "same folder/name and exact fixed UT=" + F(fixedUt) + "; observer should show a fresh loadEpoch");
        TryRestoreRealtime();
        LogBridgeMetrics("PASS");
        Line("PASS all KSP clock smoke checkpoints. Disposable save retained at saves/" + saveFolder + "; remove manually after review.");
        Application.Quit();
    }

    System.Collections.IEnumerator WaitForScene(GameScenes expected, float timeoutSeconds, string label)
    {
        float until = Time.realtimeSinceStartup + timeoutSeconds;
        while (!HighLogic.LoadedSceneIsGame || HighLogic.LoadedScene != expected)
        {
            if (Time.realtimeSinceStartup >= until) throw new TimeoutException("Timed out waiting for " + label + "; current scene=" + HighLogic.LoadedScene);
            yield return null;
        }
    }

    System.Collections.IEnumerator WaitForFlightVessel(float timeoutSeconds)
    {
        float until = Time.realtimeSinceStartup + timeoutSeconds;
        while (true)
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (HighLogic.LoadedSceneIsFlight && vessel != null && vessel.loaded && !vessel.packed && vessel.parts != null && vessel.parts.Count > 0)
            {
                bool ready = true;
                foreach (Part part in vessel.parts) if (part == null || !part.started) { ready = false; break; }
                if (ready) yield break;
            }
            if (Time.realtimeSinceStartup >= until) throw new TimeoutException("Active stock craft did not become fully loaded in Flight.");
            yield return null;
        }
    }

    void LogCheckpoint(string label, string note)
    {
        Line("CHECKPOINT " + label + " utc=" + DateTime.UtcNow.ToString("O") + " scene=" + HighLogic.LoadedScene + " saveFolder=" + HighLogic.SaveFolder + " ut=" + SafeUt() + " flightDriverPause=" + FlightDriver.Pause + " planetariumPause=" + Planetarium.Pause + " timeScale=" + F(Time.timeScale) + " warpIndex=" + TimeWarp.CurrentRateIndex + " note=" + note);
    }
    void LogPauseSignals(string label)
    {
        Line("PAUSE_SIGNALS " + label + " flightDriverPause=" + FlightDriver.Pause + " planetariumPause=" + Planetarium.Pause + " timeScale=" + F(Time.timeScale) + " ut=" + SafeUt());
    }
    string SafeUt() { try { return F(Planetarium.GetUniversalTime()); } catch { return "unavailable"; } }
    static string F(double value) { return value.ToString("R", CultureInfo.InvariantCulture); }
    void Line(string line)
    {
        try { File.AppendAllText(logPath, DateTime.UtcNow.ToString("O") + " " + line + Environment.NewLine); Debug.Log("[ExpanseClockSmoke] " + line); } catch { }
    }
    void TryRestoreRealtime()
    {
        try { TimeWarp.SetRate(0, true); } catch { }
        try { FlightDriver.SetPause(false, false); } catch { }
    }
    void LogBridgeMetrics(string outcome)
    {
        try
        {
            Type type = Type.GetType("Expanse.WorldBridge.WorldBridgeAddon, Expanse.WorldBridge", false);
            if (type == null) { Line("BRIDGE_METRICS " + outcome + " unavailable=bridge-type-not-loaded"); return; }
            object bridge = UnityEngine.Object.FindObjectOfType(type);
            if (bridge == null) { Line("BRIDGE_METRICS " + outcome + " unavailable=addon-not-found"); return; }
            var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;
            long count = Convert.ToInt64(type.GetProperty("CompletedSampleCount", flags).GetValue(bridge, null), CultureInfo.InvariantCulture);
            long totalTicks = Convert.ToInt64(type.GetProperty("TotalSampleElapsedStopwatchTicks", flags).GetValue(bridge, null), CultureInfo.InvariantCulture);
            long maxTicks = Convert.ToInt64(type.GetProperty("MaxSampleElapsedStopwatchTicks", flags).GetValue(bridge, null), CultureInfo.InvariantCulture);
            long frequency = Convert.ToInt64(type.GetProperty("SampleStopwatchFrequency", flags).GetValue(bridge, null), CultureInfo.InvariantCulture);
            double meanMs = count > 0 && frequency > 0 ? totalTicks * 1000d / frequency / count : 0d;
            double maxMs = frequency > 0 ? maxTicks * 1000d / frequency : 0d;
            Line("BRIDGE_METRICS " + outcome + " count=" + count.ToString(CultureInfo.InvariantCulture) + " meanMs=" + F(meanMs) + " maxMs=" + F(maxMs) + " stopwatchFrequency=" + frequency.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception ex) { Line("BRIDGE_METRICS " + outcome + " unavailable=" + ex.GetType().Name + ":" + ex.Message); }
    }
}
