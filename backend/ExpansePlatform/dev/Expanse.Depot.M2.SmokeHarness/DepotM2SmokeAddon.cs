using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

[KSPAddon(KSPAddon.Startup.MainMenu, true)]
public sealed class DepotM2SmokeAddon : MonoBehaviour
{
    const string RequiredRoot = @"C:\Users\griff\Documents\KSP-RMM-Dev";
    const string RequestName = "ExpanseDepotM2Smoke.request";
    string root, logPath, saveFolder, saveName, requestToken;
    bool started;
    float runStartedAt;
    bool reloadLoadObserved, reloadSceneReadyObserved, reloadFlightReadyObserved;

    sealed class ExpectedRow { public string Name; public double Amount, MaxAmount; }

    void Start()
    {
        bool accepted = false;
        try
        {
            root = Path.GetFullPath(KSPUtil.ApplicationRootPath).TrimEnd('\\', '/');
            if (!string.Equals(root, RequiredRoot, StringComparison.OrdinalIgnoreCase)) return;
            AssertNoReparseAncestors(root);
            var requestPath = Path.Combine(root, RequestName);
            if (!File.Exists(requestPath) || (File.GetAttributes(requestPath) & FileAttributes.ReparsePoint) != 0) return;
            AssertNoReparseAncestors(requestPath);
            var request = File.ReadAllText(requestPath).Trim();
            const string prefix = "RUN_EXPANSE_DEPOT_M2=";
            if (!request.StartsWith(prefix, StringComparison.Ordinal) || request.Length != prefix.Length + 32 || !Guid.TryParseExact(request.Substring(prefix.Length), "N", out var id)) return;
            requestToken = id.ToString("N");
            logPath = Path.Combine(root, "ExpanseDepotM2Smoke-" + requestToken + ".log");
            accepted = true;
            File.Delete(requestPath);
            File.WriteAllText(logPath, "START " + DateTime.UtcNow.ToString("O") + Environment.NewLine);
            saveFolder = "ExpanseDepotM2Smoke-" + requestToken;
            saveName = "depot-m2-smoke";
            var savesRoot = Path.GetFullPath(Path.Combine(root, "saves"));
            AssertNoReparseAncestors(savesRoot);
            var savePath = Path.GetFullPath(Path.Combine(savesRoot, saveFolder));
            if (!savePath.StartsWith(savesRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || Directory.Exists(savePath) || File.Exists(savePath))
                throw new IOException("Unique disposable save path exists or escapes the dev saves directory.");
            var craftPath = Path.GetFullPath(Path.Combine(root, "GameData", "SquadExpansion", "MakingHistory", "Ships", "VAB", "Muna 1.craft"));
            if (!craftPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(craftPath))
                throw new FileNotFoundException("Expected stock Muna 1 fixture craft is missing from the dev install.", craftPath);
            AudioListener.volume = 0f;
            DontDestroyOnLoad(gameObject);
            started = true;
            runStartedAt = Time.realtimeSinceStartup;
            StartCoroutine(Supervise(Run(craftPath)));
        }
        catch (Exception ex)
        {
            if (accepted) { Line("FAIL initialize " + ex); Application.Quit(); }
        }
    }

    IEnumerator Supervise(IEnumerator body)
    {
        while (true)
        {
            if (Time.realtimeSinceStartup - runStartedAt > 600f)
            {
                Line("FAIL global one-shot watchdog exceeded 600 seconds.");
                CleanupRealtime();
                Application.Quit();
                yield break;
            }
            object current; bool more;
            try { more = body.MoveNext(); current = more ? body.Current : null; }
            catch (Exception ex) { Line("FAIL " + ex); CleanupRealtime(); Application.Quit(); yield break; }
            if (!more) yield break;
            yield return current;
        }
    }

    IEnumerator Run(string craftPath)
    {
        Line("REQUEST accepted root=" + root + " saveFolder=" + saveFolder + " saveName=" + saveName + " fixture=stock-Muna-1");
        yield return new WaitForSecondsRealtime(3f);
        HighLogic.SaveFolder = saveFolder;
        HighLogic.CurrentGame = GamePersistence.CreateNewGame(saveFolder, Game.Modes.SANDBOX, new GameParameters(), "Squad/Flags/default", GameScenes.SPACECENTER, EditorFacility.VAB);
        if (HighLogic.CurrentGame == null) throw new InvalidOperationException("CreateNewGame returned null.");
        HighLogic.CurrentGame.Start();
        yield return WaitForScene(GameScenes.SPACECENTER, 120f);
        FlightDriver.StartWithNewLaunch(craftPath, "Squad/Flags/default", "LaunchPad", new VesselCrewManifest());
        yield return WaitForScene(GameScenes.FLIGHT, 180f);
        yield return WaitForUsableVessel(180f);

        var vessel = FlightGlobals.ActiveVessel;
        var resourceParts = vessel.parts.Where(p => p != null && p.persistentId != 0 && p.Resources != null && p.Resources.Count > 0).ToList();
        if (resourceParts.Count < 3) throw new InvalidOperationException("Fixture needs at least three resource-bearing parts to test two selected tanks and one unselected candidate.");
        var selectedParts = resourceParts.OrderByDescending(p => p.Resources.Count).Take(2).ToList();
        var selectedIds = selectedParts.Select(p => p.persistentId).ToArray();
        var excluded = resourceParts.First(p => !selectedIds.Contains(p.persistentId));
        var expected = new Dictionary<string, ExpectedRow>(StringComparer.Ordinal);
        foreach (var part in selectedParts) Merge(expected, CapturePart(part));
        var excludedRows = CapturePart(excluded);
        if (expected.Count < 1 || excludedRows.Count < 1) throw new InvalidOperationException("Selected and excluded fixture parts must both have resources.");

        var registry = Expanse.WorldBridge.DepotRegistryModule.Instance;
        if (registry == null || !registry.IsReady || registry.IsCorrupt || registry.MemberIds.Count != 0)
            throw new InvalidOperationException("Fresh save did not provide a ready, empty depot registry.");
        const string depotLabel = "M2 Smoke Depot";
        uint anchorId = selectedParts[0].persistentId;
        if (!registry.Register(depotLabel, anchorId, selectedIds)) throw new InvalidOperationException("Public DepotRegistryModule.Register rejected the known loaded resource parts.");
        var worldId = registry.WorldId;
        var depotId = registry.DepotId;
        if (string.IsNullOrEmpty(worldId) || string.IsNullOrEmpty(depotId)) throw new InvalidOperationException("Registration did not assign world and depot identities.");
        Line("REGISTERED label=" + depotLabel + " worldId=" + worldId + " depotId=" + depotId + " memberCount=" + registry.MemberIds.Count + " anchorPartId=" + anchorId + " selectedPartIds=" + string.Join(",", selectedIds) + " resourceBearingParts=" + resourceParts.Count);
        foreach (var row in expected.Values.OrderBy(r => r.Name, StringComparer.Ordinal)) Line("EXPECTED selected name=" + row.Name + " amount=" + F(row.Amount) + " maxAmount=" + F(row.MaxAmount));
        foreach (var row in excludedRows.Values.OrderBy(r => r.Name, StringComparer.Ordinal)) Line("UNSELECTED candidatePartId=" + excluded.persistentId + " name=" + row.Name + " excludedAmount=" + F(row.Amount) + " excludedMaxAmount=" + F(row.MaxAmount));

        yield return WaitForSnapshot(expected, 45f, "initial registration");
        var first = ReadBridgeDepot();
        VerifySnapshot(first, expected, worldId, depotId, depotLabel, "initial");

        string oldVesselName = vessel.vesselName;
        string testVesselName = "M2 Depot Vessel " + requestToken.Substring(0, 8);
        vessel.vesselName = testVesselName;
        Line("VESSEL_RENAME old=" + oldVesselName + " new=" + testVesselName + " method=programmatic-disposable-fixture");
        yield return WaitForRenamedSnapshot(expected, testVesselName, 30f);
        var renamed = ReadBridgeDepot();
        VerifySnapshot(renamed, expected, worldId, depotId, depotLabel, "renamed");
        if (!string.Equals(ReadString(renamed, "CurrentVesselName"), testVesselName, StringComparison.Ordinal)) throw new InvalidOperationException("Bridge did not publish the fixture vessel rename.");

        if (!HighLogic.LoadedSceneIsFlight || HighLogic.CurrentGame == null || HighLogic.SaveFolder != saveFolder || FlightGlobals.ActiveVessel == null || !FlightGlobals.ActiveVessel.loaded)
            throw new InvalidOperationException("Refusing to save outside the loaded owned Flight world.");
        string epochBeforeReload = ReadBridgeLoadEpoch();
        if (string.IsNullOrEmpty(epochBeforeReload)) throw new InvalidOperationException("Could not capture bridge loadEpoch from the active owned Flight world.");
        Line("PRE_RELOAD_CONTEXT saveFolder=" + HighLogic.SaveFolder + " epoch=" + epochBeforeReload + " vesselName=" + FlightGlobals.ActiveVessel.vesselName);
        HighLogic.SaveFolder = saveFolder;
        GamePersistence.SaveGame(saveName, saveFolder, SaveMode.OVERWRITE);
        Line("SAVED sameFolder=" + saveFolder + " saveName=" + saveName + " vesselName=" + testVesselName + " worldId=" + worldId + " depotId=" + depotId);
        reloadLoadObserved = reloadSceneReadyObserved = reloadFlightReadyObserved = false;
        GameEvents.onGameStateLoad.Add(OnReloadLoad);
        GameEvents.onLevelWasLoadedGUIReady.Add(OnReloadLevelReady);
        GameEvents.onFlightReady.Add(OnReloadFlightReady);
        var loaded = GamePersistence.LoadGame(saveName, saveFolder, true, false);
        if (loaded == null) throw new InvalidOperationException("LoadGame returned null for the owned disposable save.");
        HighLogic.CurrentGame = loaded;
        loaded.startScene = GameScenes.FLIGHT;
        loaded.Start();
        float reloadUntil = Time.realtimeSinceStartup + 180f;
        int stableFlightFrames = 0;
        string epochAfterReload = null;
        while (Time.realtimeSinceStartup < reloadUntil)
        {
            bool gameReady = HighLogic.LoadedSceneIsFlight && HighLogic.CurrentGame == loaded && FlightGlobals.ActiveVessel != null && FlightGlobals.ActiveVessel.loaded;
            stableFlightFrames = gameReady ? stableFlightFrames + 1 : 0;
            if (reloadLoadObserved && (reloadSceneReadyObserved || reloadFlightReadyObserved) && stableFlightFrames >= 2)
            {
                string currentEpoch = ReadBridgeLoadEpoch();
                if (HighLogic.SaveFolder == saveFolder && HighLogic.CurrentGame == loaded)
                {
                    if (!string.Equals(currentEpoch, epochBeforeReload, StringComparison.Ordinal)) { epochAfterReload = currentEpoch; break; }
                }
            }
            yield return null;
        }
        GameEvents.onGameStateLoad.Remove(OnReloadLoad);
        GameEvents.onLevelWasLoadedGUIReady.Remove(OnReloadLevelReady);
        GameEvents.onFlightReady.Remove(OnReloadFlightReady);
        if (!reloadLoadObserved || (!reloadSceneReadyObserved && !reloadFlightReadyObserved) || stableFlightFrames < 2 || string.IsNullOrEmpty(epochAfterReload))
            throw new TimeoutException("Same-save load, Flight readiness, stable CurrentGame, or fresh loadEpoch was not observed within 180 seconds.");
        Line("RELOAD_EVENTS onGameStateLoad=true levelWasLoadedGUIReady=" + reloadSceneReadyObserved + " flightReady=" + reloadFlightReadyObserved + " stableFlightFrames=" + stableFlightFrames + " epochBefore=" + epochBeforeReload + " epochAfter=" + epochAfterReload + " freshEpoch=true");
        yield return WaitForScene(GameScenes.FLIGHT, 180f);
        yield return WaitForUsableVessel(180f);
        yield return WaitForRegistry(30f);
        registry = Expanse.WorldBridge.DepotRegistryModule.Instance;
        if (registry == null || !registry.IsReady || registry.IsCorrupt) throw new InvalidOperationException("Registry was not ready after same-save reload.");
        if (registry.WorldId != worldId || registry.DepotId != depotId || registry.Label != depotLabel || registry.AnchorPartId != anchorId || registry.MemberIds.Count != selectedIds.Length || selectedIds.Any(id => !registry.MemberIds.Contains(id)))
            throw new InvalidOperationException("Registration identity or membership changed after same-save reload.");
        yield return WaitForSnapshot(expected, 45f, "reloaded registration");
        var afterLoad = ReadBridgeDepot();
        VerifySnapshot(afterLoad, expected, worldId, depotId, depotLabel, "reloaded");
        if (!string.Equals(ReadString(afterLoad, "CurrentVesselName"), testVesselName, StringComparison.Ordinal)) throw new InvalidOperationException("Vessel rename did not persist across same-save reload.");

        // The registered part alone must equal the sampled aggregate; the unselected candidate has positive stock.
        var published = ReadRows(ReadField(ReadField(afterLoad, "Snapshot"), "Resources"));
        foreach (var row in excludedRows.Values.Where(r => r.Amount > 0))
            if (!expected.ContainsKey(row.Name) && published.ContainsKey(row.Name)) throw new InvalidOperationException("An unselected-only resource leaked into the depot aggregate: " + row.Name);
        bool candidateWouldChangeAggregate = excludedRows.Values.Any(r => r.Amount > 0 || r.MaxAmount > 0);
        if (!candidateWouldChangeAggregate) throw new InvalidOperationException("Unselected resource-bearing candidate contributes no measurable amount or capacity.");
        Line("EXCLUSION_PROOF selectedAggregateMatches=true combinedSelectedAndCandidateWouldDiffer=true unselectedResourceBearingPartId=" + excluded.persistentId + " unselectedContributionLogged=true dockingTested=false");
        Line("PASS registration, resource totals, vessel rename, same-save identity and stock reload verified. Save retained only at saves/" + saveFolder);
        CleanupRealtime();
        Application.Quit();
    }

    Dictionary<string, ExpectedRow> CapturePart(Part part)
    {
        var result = new Dictionary<string, ExpectedRow>(StringComparer.Ordinal);
        foreach (PartResource resource in part.Resources)
        {
            if (string.IsNullOrWhiteSpace(resource.resourceName) || !Finite(resource.amount) || !Finite(resource.maxAmount) || resource.amount < 0 || resource.maxAmount < 0)
                throw new InvalidOperationException("Fixture part has invalid stock data.");
            ExpectedRow row;
            if (!result.TryGetValue(resource.resourceName, out row)) result.Add(resource.resourceName, row = new ExpectedRow { Name = resource.resourceName });
            row.Amount += resource.amount;
            row.MaxAmount += resource.maxAmount;
        }
        return result;
    }
    static void Merge(Dictionary<string, ExpectedRow> aggregate, Dictionary<string, ExpectedRow> partRows)
    {
        foreach (var pair in partRows)
        {
            ExpectedRow row;
            if (!aggregate.TryGetValue(pair.Key, out row)) aggregate.Add(pair.Key, row = new ExpectedRow { Name = pair.Key });
            row.Amount += pair.Value.Amount;
            row.MaxAmount += pair.Value.MaxAmount;
        }
    }

    IEnumerator WaitForUsableVessel(float seconds)
    {
        float until = Time.realtimeSinceStartup + seconds;
        while (Time.realtimeSinceStartup < until)
        {
            var v = FlightGlobals.ActiveVessel;
            if (HighLogic.LoadedSceneIsFlight && v != null && v.loaded && !v.packed && v.parts != null && v.parts.Count > 0 && v.parts.All(p => p != null && p.started)) yield break;
            yield return null;
        }
        throw new TimeoutException("No loaded, unpacked fixture vessel became available.");
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
    IEnumerator WaitForRegistry(float seconds)
    {
        float until = Time.realtimeSinceStartup + seconds;
        while (Time.realtimeSinceStartup < until)
        {
            var r = Expanse.WorldBridge.DepotRegistryModule.Instance;
            if (r != null && r.IsReady) yield break;
            yield return null;
        }
        throw new TimeoutException("Depot registry did not become ready after reload.");
    }
    IEnumerator WaitForSnapshot(Dictionary<string, ExpectedRow> expected, float seconds, string phase)
    {
        float until = Time.realtimeSinceStartup + seconds;
        while (Time.realtimeSinceStartup < until)
        {
            object depot = ReadBridgeDepot();
            if (ReadString(depot, "RegistryState") == "registered" && ReadString(depot, "ObservationState") == "complete")
            {
                VerifyRows(ReadField(ReadField(depot, "Snapshot"), "Resources"), expected, phase);
                yield break;
            }
            yield return null;
        }
        throw new TimeoutException("No complete depot snapshot during " + phase + ".");
    }
    IEnumerator WaitForRenamedSnapshot(Dictionary<string, ExpectedRow> expected, string name, float seconds)
    {
        float until = Time.realtimeSinceStartup + seconds;
        while (Time.realtimeSinceStartup < until)
        {
            object depot = ReadBridgeDepot();
            if (ReadString(depot, "ObservationState") == "complete" && ReadString(depot, "CurrentVesselName") == name)
            {
                VerifyRows(ReadField(ReadField(depot, "Snapshot"), "Resources"), expected, "renamed");
                yield break;
            }
            yield return null;
        }
        throw new TimeoutException("Bridge did not publish the renamed vessel with fresh stock.");
    }
    void VerifySnapshot(object depot, Dictionary<string, ExpectedRow> expected, string world, string id, string label, string phase)
    {
        if (ReadString(depot, "RegistryState") != "registered" || ReadString(depot, "ObservationState") != "complete") throw new InvalidOperationException("Depot state is not registered/complete at " + phase + ".");
        if (ReadString(depot, "WorldId") != world || ReadString(depot, "DepotId") != id || ReadString(depot, "Label") != label) throw new InvalidOperationException("Depot identity/label changed at " + phase + ".");
        VerifyRows(ReadField(ReadField(depot, "Snapshot"), "Resources"), expected, phase);
    }
    void VerifyRows(object source, Dictionary<string, ExpectedRow> expected, string phase)
    {
        var actual = ReadRows(source);
        if (actual.Count != expected.Count) throw new InvalidOperationException("Resource row count mismatch at " + phase + ": expected=" + expected.Count + " actual=" + actual.Count);
        foreach (var pair in expected)
        {
            ExpectedRow row;
            if (!actual.TryGetValue(pair.Key, out row) || Math.Abs(row.Amount - pair.Value.Amount) > Tolerance(pair.Value.Amount) || Math.Abs(row.MaxAmount - pair.Value.MaxAmount) > Tolerance(pair.Value.MaxAmount))
                throw new InvalidOperationException("Stock mismatch at " + phase + " resource=" + pair.Key + " expected=" + F(pair.Value.Amount) + "/" + F(pair.Value.MaxAmount) + " actual=" + (actual.ContainsKey(pair.Key) ? F(actual[pair.Key].Amount) + "/" + F(actual[pair.Key].MaxAmount) : "missing"));
            Line("ACTUAL " + phase + " name=" + row.Name + " amount=" + F(row.Amount) + " maxAmount=" + F(row.MaxAmount));
        }
    }
    Dictionary<string, ExpectedRow> ReadRows(object rows)
    {
        var result = new Dictionary<string, ExpectedRow>(StringComparer.Ordinal);
        foreach (object row in (IEnumerable)rows)
        {
            var item = new ExpectedRow { Name = ReadString(row, "Name"), Amount = Convert.ToDouble(ReadField(row, "Amount"), CultureInfo.InvariantCulture), MaxAmount = Convert.ToDouble(ReadField(row, "MaxAmount"), CultureInfo.InvariantCulture) };
            result.Add(item.Name, item);
        }
        return result;
    }
    object ReadBridgeDepot()
    {
        var registry = Expanse.WorldBridge.DepotRegistryModule.Instance;
        if (registry == null) throw new InvalidOperationException("DepotRegistryModule instance is missing.");
        var clone = registry.GetType().GetMethod("CloneView", BindingFlags.Instance | BindingFlags.NonPublic);
        if (clone == null) throw new MissingMethodException(registry.GetType().FullName, "CloneView");
        return clone.Invoke(registry, new object[] { true, false, (double)Time.realtimeSinceStartup });
    }
    string ReadBridgeLoadEpoch()
    {
        var type = Type.GetType("Expanse.WorldBridge.WorldBridgeAddon, Expanse.WorldBridge", false);
        if (type == null) throw new InvalidOperationException("WorldBridgeAddon type is not loaded.");
        var addon = UnityEngine.Object.FindObjectOfType(type);
        if (addon == null) throw new InvalidOperationException("WorldBridgeAddon instance is missing.");
        var epoch = type.GetField("loadEpoch", BindingFlags.Instance | BindingFlags.NonPublic);
        if (epoch == null) throw new MissingFieldException(type.FullName, "loadEpoch");
        return Convert.ToString(epoch.GetValue(addon), CultureInfo.InvariantCulture);
    }
    static object ReadField(object value, string name)
    {
        if (value == null) throw new InvalidOperationException("Missing reflected value while reading " + name + ".");
        var type = value.GetType();
        var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (field != null) return field.GetValue(value);
        var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (property != null) return property.GetValue(value, null);
        throw new MissingMemberException(type.FullName, name);
    }
    static string ReadString(object value, string name) { object item = ReadField(value, name); return item == null ? null : Convert.ToString(item, CultureInfo.InvariantCulture); }
    static bool Finite(double d) { return !double.IsNaN(d) && !double.IsInfinity(d); }
    static double Tolerance(double d) { return Math.Max(0.0001, Math.Abs(d) * 0.000001); }
    static void AssertNoReparseAncestors(string path)
    {
        string cursor = Path.GetFullPath(path);
        string volume = Path.GetPathRoot(cursor);
        while (!string.IsNullOrEmpty(cursor))
        {
            if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0) throw new IOException("Refusing a path through a reparse point: " + cursor);
            if (string.Equals(cursor, volume, StringComparison.OrdinalIgnoreCase)) break;
            cursor = Path.GetDirectoryName(cursor);
        }
    }
    static string F(double d) { return d.ToString("R", CultureInfo.InvariantCulture); }
    void OnReloadLoad(ConfigNode ignored) { reloadLoadObserved = true; }
    void OnReloadLevelReady(GameScenes scene) { if (scene == GameScenes.FLIGHT) reloadSceneReadyObserved = true; }
    void OnReloadFlightReady() { if (HighLogic.LoadedSceneIsFlight) reloadFlightReadyObserved = true; }
    void CleanupRealtime() { try { TimeWarp.SetRate(0, true); } catch { } try { FlightDriver.SetPause(false, false); } catch { } }
    void Line(string text) { try { var line = DateTime.UtcNow.ToString("O") + " " + text; File.AppendAllText(logPath, line + Environment.NewLine); Debug.Log("[ExpanseDepotM2Smoke] " + text); } catch { } }
    void OnDestroy()
    {
        GameEvents.onGameStateLoad.Remove(OnReloadLoad);
        GameEvents.onLevelWasLoadedGUIReady.Remove(OnReloadLevelReady);
        GameEvents.onFlightReady.Remove(OnReloadFlightReady);
        if (started) CleanupRealtime();
    }
}
