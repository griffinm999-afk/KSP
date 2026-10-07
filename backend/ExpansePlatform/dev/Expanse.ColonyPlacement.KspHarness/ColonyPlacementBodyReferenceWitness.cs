using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Expanse.WorldBridge;
using UnityEngine;

// Explicit isolated fixture exception: only a crewless one-part stock probe.
// Colony buildings are never created here; all package placement uses Enqueue.
[KSPAddon(KSPAddon.Startup.MainMenu, true)]
public sealed class ColonyPlacementBodyReferenceWitness : MonoBehaviour
{
    private const string Root = @"C:\Users\griff\Documents\Codex\KSP-Colony-Demo";
    private string log;
    private void Start()
    {
        if (Path.GetFullPath(KSPUtil.ApplicationRootPath).TrimEnd('\\', '/') != Root) return;
        log = Path.Combine(Root, "colony-placement-body-reference-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + ".txt");
        DontDestroyOnLoad(this); StartCoroutine(Poll());
    }
    private IEnumerator Poll()
    {
        while (true)
        {
            if (HighLogic.LoadedSceneIsFlight && FlightGlobals.ready && HighLogic.CurrentGame != null && FlightGlobals.ActiveVessel != null && FlightGlobals.ActiveVessel.loaded && TimeWarp.CurrentRate == 1 && !FlightDriver.Pause)
            {
                string watch = Path.Combine(Root, "colony-placement-body-watch.cfg");
                if (File.Exists(watch)) try { Consume(watch); } catch (Exception ex) { File.AppendAllText(log, DateTime.UtcNow.ToString("o") + " REFUSED " + ex + Environment.NewLine); }
            }
            yield return new WaitForSecondsRealtime(1);
        }
    }
    private void Consume(string watchPath)
    {
        CheckPath(watchPath); Need(new FileInfo(watchPath).Length <= 2048, "Body watcher exceeds 2KiB");
        var watch = ConfigNode.Load(watchPath); Need(watch != null && watch.nodes.Count == 0 && watch.values.Count == 2, "Body watcher requires exact payload name/hash");
        string name = watch.GetValue("payloadName"), hash = watch.GetValue("payloadSha256");
        Need(name != null && name.StartsWith("colony-placement-body-request-", StringComparison.Ordinal) && name.EndsWith(".cfg", StringComparison.Ordinal) && Safe(name.Substring(0, name.Length - 4)), "Body payload name invalid");
        string path = Path.Combine(Root, name); CheckPath(path); Need(File.Exists(path) && new FileInfo(path).Length <= 4096, "Body payload absent/oversized");
        byte[] bytes = File.ReadAllBytes(path); Need(Sha(bytes) == hash, "Body payload hash differs"); var request = ConfigNode.Parse(Encoding.UTF8.GetString(bytes));
        Need(request != null && request.nodes.Count == 0 && (request.values.Count == 10 && request.GetValue("authorization") == "empty-body-reference-fixture-no-colony-buildings" || EdgeHeadingSchema(request) || request.values.Count == 11 && request.GetValue("mode") == "loaded-operation-diagnostic" && request.GetValue("authorization") == "read-only-existing-held-native-geometry-no-effects"), "Exact body fixture/diagnostic authorization absent");
        string folder = request.GetValue("saveFolder"), world = request.GetValue("worldId"), mode = request.GetValue("mode"), bodyName = request.GetValue("body"), saveName = request.GetValue("saveName"); Guid operation;
        Need(Guid.TryParse(request.GetValue("operationId"), out operation) && operation != Guid.Empty && folder == HighLogic.SaveFolder && Safe(folder) && folder.StartsWith("ColonyBuild-", StringComparison.Ordinal), "Body fixture identity/selected save mismatch");
        Need(mode == "scout" || mode == "create-reference" || mode == "loaded-survey" || mode == "loaded-grid-service" || mode == "loaded-grid-housing" || mode == "loaded-edge-service" || mode == "loaded-edge-housing" || mode == "loaded-operation-diagnostic", "Unknown bounded body fixture mode"); Need(bodyName == "Mun" || bodyName == "Duna", "Only explicitly authorized test bodies permitted");
        Need(Safe(saveName) && saveName.StartsWith("colony-test-body-", StringComparison.Ordinal), "Native body save name invalid");
        var scenario = ColonyPlacementScenario.Instance; Need(scenario != null && scenario.Ready && scenario.WorldId == world, "Current native world authority differs");
        string authPath = Path.Combine(Root, "colony-development-authorization.cfg"); CheckPath(authPath);
        var auth = ConfigNode.Load(authPath); string pipe = auth == null ? null : auth.GetValue("colonyPipe");
        Need(auth != null && auth.GetValue("root") == Root && auth.GetValue("saveFolder") == folder && pipe != null && pipe.StartsWith("ExpanseFoundations.Colonies.dev.", StringComparison.Ordinal) && Environment.GetCommandLineArgs().Contains("-expanseColonyPipe=" + pipe), "Body fixture isolated runtime authorization mismatch");
        foreach (string prefix in new[] { "-expanseClockPublisherPipe=ExpanseFoundations.Clock.Publisher.dev.", "-expanseEffectsPipe=ExpanseFoundations.Effects.dev.", "-expanseWolfPipe=ExpanseFoundations.WOLF.Admin.dev." }) Need(Environment.GetCommandLineArgs().Any(a => a.StartsWith(prefix, StringComparison.Ordinal)), "Body fixture IPC namespace incomplete");
        string persistent = Path.Combine(Root, "saves", folder, "persistent.sfs"); CheckPath(persistent);
        Need(Sha(File.ReadAllBytes(persistent)) == request.GetValue("seedSha256"), "Body fixture seed native bytes changed");
        double latitude = Number(request.GetValue("latitude")), longitude = Number(request.GetValue("longitude")); Need(Math.Abs(latitude) < 80 && longitude >= -180 && longitude <= 180, "Body fixture coordinates invalid/polar");
        var body = FlightGlobals.Bodies.Single(b => b.bodyName == bodyName); Need(body.pqsController != null && body.Radius > 1000, "Actual native solid terrain provider missing");
        string evidencePath = Path.Combine(Root, "colony-placement-body-evidence-" + operation.ToString("D") + ".cfg"); CheckPath(evidencePath); Need(!File.Exists(evidencePath), "Body operation already has evidence; no repeat creation");
        // Claim before any effect. A refused/ambiguous effect never runs twice.
        File.Move(watchPath, watchPath + ".claimed-" + operation.ToString("D"));
        var evidence = new ConfigNode("COLONY_BODY_REFERENCE_WITNESS"); evidence.AddValue("operationId", operation); evidence.AddValue("body", bodyName); evidence.AddValue("saveFolder", folder); evidence.AddValue("worldId", world); evidence.AddValue("payloadSha256", hash); evidence.AddValue("seedSha256", request.GetValue("seedSha256"));
        evidence.AddValue("bodyRadiusMetres", R(body.Radius)); evidence.AddValue("surfaceGravityMetresPerSecondSquared", R(body.gravParameter / (body.Radius * body.Radius))); evidence.AddValue("method", "Actual installed native PQS estimate only; loaded collider survey/footing/deployed sweep remains mandatory");
        if (mode == "scout") Scout(body, latitude, longitude, evidence);
        else if (mode == "loaded-survey") LoadedSurvey(body, latitude, longitude, evidence);
        else if (mode == "loaded-grid-service" || mode == "loaded-grid-housing") ColonyPlacementSiteGridDiagnostic.Write(body, latitude, longitude, mode == "loaded-grid-service" ? "service-kpbs-v1" : "housing-kpbs-v1", evidence);
        else if (mode == "loaded-edge-service" || mode == "loaded-edge-housing")
        {
            double heading = request.HasValue("headingDegrees") ? Number(request.GetValue("headingDegrees")) : 0;
            ColonyPlacementTerrainEdgesDiagnostic.Write(body, latitude, longitude, mode == "loaded-edge-service" ? "service-kpbs-v1" : "housing-kpbs-v1", evidence, heading);
        }
        else if (mode == "loaded-operation-diagnostic")
        {
            Guid target; Need(Guid.TryParse(request.GetValue("targetOperationId"), out target) && target != Guid.Empty, "Exact held target operation required");
            var status = scenario.GetStatus(target.ToString("D")); Need(status != null && status.Stage == ColonyPlacementStage.RecoveryHold && status.AssemblyAttempted, "Diagnostic requires original existing held assembly");
            var records = (System.Collections.IDictionary)typeof(ColonyPlacementScenario).GetField("Records",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).GetValue(scenario);
            object record=records[target.ToString("D")]; var original=((ColonyPlacementRequest)record.GetType().GetField("Request").GetValue(record)).Copy();
            Need(original.WorldId==world&&original.BodyName==bodyName&&original.Fingerprint()==status.RequestFingerprint,"Held target lineage differs");
            var vessel=FlightGlobals.Vessels.Single(v=>v.id.ToString("D")==status.VesselId); Need(vessel.loaded&&vessel.parts.All(p=>p.started)&&vessel.mainBody==body,"Held native geometry must actually be loaded on selected body");
            ColonyPlacementColliderShapeWitness.Diagnose(vessel,original,status,evidence);
        }
        else CreateReference(body, latitude, longitude, saveName, evidence);
        File.WriteAllText(evidencePath, evidence.ToString()); File.AppendAllText(log, DateTime.UtcNow.ToString("o") + " PASS " + mode + " evidence=" + Path.GetFileName(evidencePath) + Environment.NewLine);
    }
    private static bool EdgeHeadingSchema(ConfigNode request)
    {
        if (request.values.Count != 11 || request.GetValue("authorization") != "empty-body-reference-fixture-no-colony-buildings" ||
            (request.GetValue("mode") != "loaded-edge-service" && request.GetValue("mode") != "loaded-edge-housing")) return false;
        var keys = new[] { "authorization", "operationId", "saveFolder", "worldId", "mode", "body", "latitude", "longitude", "saveName", "seedSha256", "headingDegrees" };
        return request.values.Cast<ConfigNode.Value>().Select(v => v.name).OrderBy(v => v, StringComparer.Ordinal)
            .SequenceEqual(keys.OrderBy(v => v, StringComparer.Ordinal), StringComparer.Ordinal);
    }
    private static void LoadedSurvey(CelestialBody body, double latitude, double longitude, ConfigNode evidence)
    {
        Need(FlightGlobals.ActiveVessel != null && FlightGlobals.ActiveVessel.mainBody == body && ColonyRuntime.Current != null, "Loaded survey requires actual selected body reference/context");
        string[] ids = { "housing-kpbs-v1", "service-kpbs-v1", "storage-kpbs-v1", "lamp-stock-v1", "power-duna-v1", "wolf-hoppers-v1", "fertilizer-tundra-v1", "agriculture-duna-v1", "cultivation-duna-v1", "cultivation-feeds-v1", "power-ranger-bank-v1" };
        var catalog = ColonyTemplateCatalog.LoadInstalled();
        Need(catalog.Issues.Count == 0 && catalog.Templates.Select(t => t.Id).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(ids.OrderBy(x => x, StringComparer.Ordinal)), "Exact installed current eleven-template candidate catalog does not qualify");
        evidence.AddValue("method", "Actual product read-only ColonySiteSurvey: <=1m complete declared footprint, independent loaded terrain normals/heights, deployed/access hardware clearance; preview only");
        for (int index = 0; index < ids.Length; index++)
        {
            var template = catalog.Templates.Single(t => t.Id == ids[index]); var location = Offset(body, latitude, longitude, (index % 4 - 1.5) * 45, (2 * (index / 4) - 1) * 35);
            var result = ColonySiteSurvey.Survey(template, body.bodyName, location.x, location.y, 0, new Expanse.Domain.Colonies.ColonyPlot[0], ColonyRuntime.Current.ContextKey);
            var row = evidence.AddNode("ACTUAL_LOADED_SURVEY"); row.AddValue("templateId", template.Id); row.AddValue("templateHash", template.Hash); row.AddValue("craftSha256", template.CraftSha256);
            row.AddValue("latitude", R(location.x)); row.AddValue("longitude", R(location.y)); row.AddValue("clear", result.Clear); row.AddValue("reason", result.Reason); row.AddValue("sampleCount", result.SampleCount);
            row.AddValue("actualMaximumSlopeDegrees", R(result.MaximumSlopeDegrees)); row.AddValue("actualSupportGapMetres", R(result.MaximumSupportGapMetres)); row.AddValue("terrainWitness", result.TerrainWitness); row.AddValue("contextKey", result.ContextKey);
            if (result.Plot != null) row.AddValue("trustedSurveyHash", result.Plot.SurveyHash);
            ColonyPlacementTerrainDiagnostic.Add(body, template, location.x, location.y, result.TerrainHeight, row);
        }
    }
    private static void Scout(CelestialBody body, double latitude, double longitude, ConfigNode evidence)
    {
        // 49 bounded candidate centers, nine independent footprint samples each.
        // No estimated result grants terrain clearance or a package certificate.
        for (int x = -3; x <= 3; x++) for (int z = -3; z <= 3; z++)
        {
            var center = Offset(body, latitude, longitude, x * 250, z * 250); double height = Height(body, center.x, center.y);
            var position = body.GetWorldSurfacePosition(center.x, center.y, height); var radial = (position - body.position).normalized; var normal = Normal(body, center.x, center.y);
            double slope = Vector3d.Angle(radial, normal), min = double.PositiveInfinity, max = double.NegativeInfinity, variation = 0;
            var row = evidence.AddNode("PQS_ESTIMATED_CANDIDATE"); row.AddValue("latitude", R(center.x)); row.AddValue("longitude", R(center.y)); row.AddValue("height", R(height)); row.AddValue("centerSlopeDegrees", R(slope)); row.AddValue("actualNativeBiome", ScienceUtil.GetExperimentBiome(body, center.x, center.y));
            for (int dx = -1; dx <= 1; dx++) for (int dz = -1; dz <= 1; dz++)
            {
                var location = Offset(body, center.x, center.y, dx * 10, dz * 10); var point = body.GetWorldSurfacePosition(location.x, location.y, Height(body, location.x, location.y));
                double residual = Vector3d.Dot(point - position, normal); min = Math.Min(min, residual); max = Math.Max(max, residual); variation = Math.Max(variation, Vector3d.Angle(normal, Normal(body, location.x, location.y)));
                var sample = row.AddNode("PQS_ESTIMATE"); sample.AddValue("eastMetres", dx * 10); sample.AddValue("northMetres", dz * 10); sample.AddValue("residualToCenterPlaneMetres", R(residual));
            }
            row.AddValue("estimatedPlaneResidualVariationMetres", R(max - min)); row.AddValue("estimatedIndependentNormalVariationDegrees", R(variation)); row.AddValue("loadedColliderClear", false);
        }
    }
    private static void CreateReference(CelestialBody body, double latitude, double longitude, string saveName, ConfigNode evidence)
    {
        var game = HighLogic.CurrentGame; var active = FlightGlobals.ActiveVessel; var before = FlightGlobals.Vessels.Select(v => v.id).ToArray(); double funds = Funding.Instance == null ? 0 : Funding.Instance.Funds;
        Need(before.Length <= 4095 && !File.Exists(Path.Combine(Root, "saves", HighLogic.SaveFolder, saveName + ".sfs")), "Fixture world capacity/native save overwrite refused");
        var available = PartLoader.getPartInfoByName("probeStackLarge"); Need(available != null && available.partPrefab != null && available.partPrefab.CrewCapacity == 0, "Actual installed empty stock probe missing");
        var part = ProtoVessel.CreatePartNode(available.name, FlightGlobals.GetUniquepersistentId(), new ProtoCrewMember[0]);
        foreach (var tank in part.GetNodes("RESOURCE")) tank.SetValue("amount", "0", true);
        double terrain = Height(body, latitude, longitude); var orbit = new Orbit(0, 0, body.Radius + Math.Max(0, terrain) + 1, 0, 0, 0, Planetarium.GetUniversalTime(), body);
        var node = ProtoVessel.CreateVesselNode("Empty colony test reference " + body.bodyName + " " + Guid.NewGuid().ToString("N"), VesselType.Probe, orbit, 0, new[] { part });
        var proto = new ProtoVessel(node, game); proto.situation = Vessel.Situations.LANDED; proto.landed = true; proto.splashed = false; proto.latitude = latitude; proto.longitude = longitude; proto.altitude = terrain + .35; proto.height = .35f; proto.skipGroundPositioning = false;
        var position = body.GetWorldSurfacePosition(latitude, longitude, terrain); var up = Normal(body, latitude, longitude);
        var northLocation = Offset(body, latitude, longitude, 0, 2); var north = Vector3.ProjectOnPlane((Vector3)(body.GetWorldSurfacePosition(northLocation.x, northLocation.y, terrain) - position), (Vector3)up).normalized;
        proto.normal = (Vector3)up; proto.rotation = Quaternion.Inverse(body.bodyTransform.rotation) * Quaternion.LookRotation(north, (Vector3)up);
        var native = new ConfigNode("VESSEL"); proto.Save(native); var actual = game.AddVessel(native);
        Need(actual != null && actual.vesselRef != null && actual.vesselRef.id == proto.vesselID && FlightGlobals.ActiveVessel == active && before.All(id => FlightGlobals.Vessels.Any(v => v.id == id)), "Native test reference registration changed unrelated vessel control/membership");
        Need(Funding.Instance == null || Funding.Instance.Funds == funds, "Native test reference triggered unplanned funds effect");
        GamePersistence.SaveGame(saveName, HighLogic.SaveFolder, SaveMode.OVERWRITE); string path = Path.Combine(Root, "saves", HighLogic.SaveFolder, saveName + ".sfs");
        var saved = ConfigNode.Load(path).GetNode("GAME").GetNode("FLIGHTSTATE").GetNodes("VESSEL").Single(v => v.GetValue("pid") == actual.vesselID.ToString("N"));
        Need(saved.GetNodes("PART").Length == 1 && saved.GetValue("sit") == "LANDED" && saved.GetNodes("PART").All(p => p.GetValues("crew").Length == 0 && p.GetNodes("RESOURCE").All(r => Number(r.GetValue("amount")) == 0)), "Actual native fixture save is not one empty landed probe");
        evidence.AddValue("referenceVesselId", actual.vesselID); evidence.AddValue("referencePartPersistentId", actual.protoPartSnapshots.Single().persistentId); evidence.AddValue("nativeSaveName", saveName); evidence.AddValue("nativeSaveSha256", Sha(File.ReadAllBytes(path))); evidence.AddValue("latitude", R(latitude)); evidence.AddValue("longitude", R(longitude)); evidence.AddValue("pqsHeight", R(terrain)); evidence.AddValue("loadedTerrainQualified", false); evidence.AddValue("colonyBuildingsCreated", 0); evidence.AddValue("runtimeCertified", false);
    }
    private static Vector3d Normal(CelestialBody body, double latitude, double longitude)
    {
        var east = Offset(body, latitude, longitude, 1, 0); var west = Offset(body, latitude, longitude, -1, 0); var north = Offset(body, latitude, longitude, 0, 1); var south = Offset(body, latitude, longitude, 0, -1);
        Vector3d e = body.GetWorldSurfacePosition(east.x, east.y, Height(body, east.x, east.y)), w = body.GetWorldSurfacePosition(west.x, west.y, Height(body, west.x, west.y));
        Vector3d n = body.GetWorldSurfacePosition(north.x, north.y, Height(body, north.x, north.y)), s = body.GetWorldSurfacePosition(south.x, south.y, Height(body, south.x, south.y));
        var normal = Vector3d.Cross(n - s, e - w).normalized; var radial = body.GetWorldSurfacePosition(latitude, longitude, 0) - body.position;
        return Vector3d.Dot(normal, radial) < 0 ? -normal : normal;
    }
    private static Vector2d Offset(CelestialBody body, double latitude, double longitude, double east, double north)
    { double lat = latitude + north / body.Radius * 180 / Math.PI, lon = longitude + east / (body.Radius * Math.Cos(latitude * Math.PI / 180)) * 180 / Math.PI; return new Vector2d(lat, (lon + 540) % 360 - 180); }
    private static double Height(CelestialBody body, double latitude, double longitude)
    { var radial = QuaternionD.AngleAxis(longitude, Vector3d.down) * QuaternionD.AngleAxis(latitude, Vector3d.forward) * Vector3d.right; double result = body.pqsController.GetSurfaceHeight(radial) - body.Radius; Need(!double.IsNaN(result) && !double.IsInfinity(result), "Native PQS is not finite"); return result; }
    private static double Number(string value) { double result; Need(double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result) && !double.IsNaN(result) && !double.IsInfinity(result), "Finite native number required"); return result; }
    private static string R(double value) { return value.ToString("R", CultureInfo.InvariantCulture); }
    private static bool Safe(string value) { return !string.IsNullOrEmpty(value) && value.Length <= 120 && value.All(c => char.IsLetterOrDigit(c) || c == '-'); }
    private static void CheckPath(string value)
    { string path = Path.GetFullPath(value); Need(path.StartsWith(Root + "\\", StringComparison.OrdinalIgnoreCase), "Fixture path leaves isolated root"); for (string p = path; p != null; p = Path.GetDirectoryName(p)) Need(!(File.Exists(p) || Directory.Exists(p)) || (File.GetAttributes(p) & FileAttributes.ReparsePoint) == 0, "Fixture reparse path refused"); }
    private static string Sha(byte[] bytes) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
    private static void Need(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
