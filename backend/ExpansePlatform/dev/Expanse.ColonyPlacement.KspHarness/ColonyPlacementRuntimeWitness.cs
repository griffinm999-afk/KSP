using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Expanse.WorldBridge;
using UnityEngine;

// Development-only product API probe. It never constructs game objects or edits a
// save fixture. An explicitly armed isolated test may enqueue exact hash-bound
// adapter terms; this is not an economic or commissioning certificate.
[KSPAddon(KSPAddon.Startup.MainMenu, true)]
public sealed partial class ColonyPlacementRuntimeWitness : MonoBehaviour
{
    private const string ApprovedRoot = @"C:\Users\griff\Documents\Codex\KSP-Colony-Demo";
    private string root, expectedFolder, operation, saveName, log, lastStage;
    private int checks;
    private Vessel reference;
    private double referenceLatitude, referenceLongitude;
    private uint[] referenceParts;
    private string injectPhase;
    private bool injected;
    private ColonyPlacementRequest pendingRequest;
    private bool enqueued;
    private string requestHash, expectedFingerprint;
    private bool controlChanged;
    private bool coldReload;
    private bool recoverExisting, reconciledExisting;
    private string reloadSourceName, reloadSourceHash;
    private int priorProcess;
    private ColonyPlacementRequest reloadRequest;
    private ColonyPlacementStatus reloadStatus;
    private readonly Dictionary<uint, uint> reloadCraftPartIds = new Dictionary<uint, uint>();
    private string watchMode;
    private string terrainRejectKind;
    private string[] referenceWorldIds;
    private ConfigNode[] batchCases;
    private bool coldBatch;
    private bool streetGroup;
    private string unloadedSaveName;
    private readonly Dictionary<string, ColonyPlacementStatus> batchStatuses = new Dictionary<string, ColonyPlacementStatus>(StringComparer.Ordinal);
    private bool requireStrictEnvelope;
    private Vessel referenceCandidate;
    private double referenceCandidateLatitude, referenceCandidateLongitude, referenceStableSince = -1;

    private void Start()
    {
        root = Path.GetFullPath(KSPUtil.ApplicationRootPath).TrimEnd('\\', '/');
        if (!string.Equals(root, ApprovedRoot, StringComparison.OrdinalIgnoreCase)) return;
        log = Path.Combine(root, "colony-placement-witness-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + ".txt");
        try { Arm(); }
        catch (Exception e) { Line("REFUSED " + e); ClearInjector(); }
    }

    private void Arm()
    {
        string requestFile = Path.Combine(root, "colony-placement-watch.cfg");
        if (!File.Exists(requestFile)) return;
        CheckPath(requestFile);
        Check(new FileInfo(requestFile).Length <= 4096, "Oversized watcher authorization");
        var request = ConfigNode.Load(requestFile);
        Check(request != null && request.GetValue("authorization") == "verified-isolated-development-only", "Missing isolated watcher authorization");
        expectedFolder = request.GetValue("saveFolder"); operation = request.GetValue("operationId"); saveName = request.GetValue("saveName"); injectPhase = request.GetValue("injectPhase");
        Check(SafeName(expectedFolder) && expectedFolder.StartsWith("ColonyBuild-", StringComparison.Ordinal) && SafeName(operation) && SafeName(saveName) && saveName.StartsWith("colony-placement-", StringComparison.Ordinal), "Invalid watcher identity or disposable witness save name");
        CheckPath(Path.Combine(root, "saves", expectedFolder));
        Check(Directory.Exists(Path.Combine(root, "saves", expectedFolder)), "Selected disposable save is absent");
        CheckDevelopmentPipe("-expanseClockPublisherPipe=", "ExpanseFoundations.Clock.Publisher.dev.");
        CheckDevelopmentPipe("-expanseEffectsPipe=", "ExpanseFoundations.Effects.dev.");
        CheckDevelopmentPipe("-expanseWolfPipe=", "ExpanseFoundations.WOLF.Admin.dev.");
        string enqueueName = request.GetValue("enqueueRequestName");
        watchMode = request.GetValue("mode") ?? "native-placement";
        string geometryAuthorization = request.GetValue("geometryAuthorization");
        Check(string.IsNullOrEmpty(geometryAuthorization) || geometryAuthorization == "actual-native-shape-sweep02m", "Unknown native geometry observation authorization");
        requireStrictEnvelope = (watchMode == "native-placement" || watchMode == "cold-native-reload") && geometryAuthorization == "actual-native-shape-sweep02m";
        Check(watchMode == "native-placement" || watchMode == "cold-native-reload" || watchMode == "recover-existing-native-building" || watchMode == "native-clearance-rejection" || watchMode == "native-terrain-rejection" || watchMode == "native-failure-phase" || watchMode == "native-package-batch" || watchMode == "cold-package-batch" || watchMode == "native-street-group" || watchMode == "cold-street-group", "Unsupported watcher mode");
        if (watchMode == "native-package-batch" || watchMode == "cold-package-batch" || watchMode == "native-street-group" || watchMode == "cold-street-group")
        {
            streetGroup = watchMode == "native-street-group" || watchMode == "cold-street-group";
            Check(string.IsNullOrEmpty(enqueueName) && string.IsNullOrEmpty(injectPhase) && request.GetValue("batchAuthorization") == (streetGroup ? "five-exact-street-packages-no-economic-certification" : "eight-exact-package-probes-no-economic-certification"), "Package batch lacks bounded isolated authorization");
            coldBatch = watchMode == "cold-package-batch" || watchMode == "cold-street-group";
            string batchName = request.GetValue("batchRequestName");
            Check(batchName != null && batchName.StartsWith("colony-placement-batch-", StringComparison.Ordinal) && batchName.EndsWith(".cfg", StringComparison.Ordinal) && SafeName(batchName.Substring(0, batchName.Length - 4)), "Invalid bounded batch name");
            ArmPackageBatch(request, batchName);
            DontDestroyOnLoad(this);
            Line("START root=" + root + " save=" + expectedFolder + " operation=" + operation);
            StartCoroutine(Supervise(RunPackageBatch()));
            return;
        }
        coldReload = watchMode == "cold-native-reload";
        recoverExisting = watchMode == "recover-existing-native-building";
        if (coldReload || recoverExisting)
        {
            Check(string.IsNullOrEmpty(enqueueName) && string.IsNullOrEmpty(injectPhase), "Cold reload cannot enqueue a new operation or inject failure");
            Check(recoverExisting ? request.GetValue("recoveryAuthorization") == "existing-marked-building-native-reconcile-only-no-respawn" : request.GetValue("reloadAuthorization") == "cold-native-reload-no-new-spawn", "Cold reload/recovery lacks exact isolated authorization");
            reloadSourceName = request.GetValue("reloadSourceName"); reloadSourceHash = request.GetValue("reloadSourceSha256");
            Check(SafeNativeSourceName(reloadSourceName) && reloadSourceName != saveName, "Invalid native reload source or attempted source overwrite");
            Check(int.TryParse(request.GetValue("reloadPriorProcessId"), NumberStyles.None, CultureInfo.InvariantCulture, out priorProcess) && priorProcess > 0 && priorProcess != System.Diagnostics.Process.GetCurrentProcess().Id,
                "Cold reload requires the independently witnessed previous process identity");
            ReadColdReloadSource();
            if (recoverExisting) { pendingRequest = reloadRequest.Copy(); requireStrictEnvelope = true; }
            Line("ARMED cold-native-reload source=" + reloadSourceName + " sha256=" + reloadSourceHash + " previousProcess=" + priorProcess + "; no new enqueue or economy certificate");
        }
        if (!string.IsNullOrEmpty(enqueueName))
        {
            Check(request.GetValue("enqueueAuthorization") == "adapter-probe-only-no-economic-certification", "Adapter enqueue lacks explicit isolated test authorization");
            Check(enqueueName.StartsWith("colony-placement-request-", StringComparison.Ordinal) && enqueueName.EndsWith(".cfg", StringComparison.Ordinal) && SafeName(enqueueName.Substring(0, enqueueName.Length - 4)), "Invalid adapter request file name");
            string path = Path.Combine(root, enqueueName); CheckPath(path);
            Check(File.Exists(path) && new FileInfo(path).Length <= 65536, "Adapter request absent or oversized");
            byte[] bytes = File.ReadAllBytes(path); requestHash = Sha(bytes);
            Check(string.Equals(requestHash, request.GetValue("enqueueRequestSha256"), StringComparison.OrdinalIgnoreCase), "Adapter request bytes do not match authorized SHA-256");
            var tree = ConfigNode.Parse(System.Text.Encoding.UTF8.GetString(bytes));
            Check(tree != null && tree.values.Count == 0 && tree.nodes.Count == 1 && tree.GetNodes("REQUEST").Length == 1, "Adapter file requires exactly one REQUEST node");
            var codec = typeof(ColonyPlacementScenario).Assembly.GetType("Expanse.WorldBridge.ColonyPlacementCodec");
            pendingRequest = (ColonyPlacementRequest)codec.GetMethod("ReadRequest", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { tree.GetNode("REQUEST") });
            string error = pendingRequest.Validate(); Check(error == null, "Invalid authorized adapter request: " + error);
            Check(pendingRequest.OperationId == operation, "Enqueue identity disagrees with watcher operation");
            expectedFingerprint = pendingRequest.Fingerprint();
            string authorizedFingerprint = request.GetValue("enqueueRequestFingerprint");
            Check(string.IsNullOrEmpty(authorizedFingerprint) || string.Equals(expectedFingerprint, authorizedFingerprint, StringComparison.OrdinalIgnoreCase), "Authorized adapter request terms fingerprint disagrees");
            Line("ARMED adapter-only request sha256=" + requestHash + " fingerprint=" + expectedFingerprint + "; no economy or commissioning claim");
        }
        Check(string.IsNullOrEmpty(injectPhase) || new[] { "before-assembly", "after-assembly", "before-anchor", "after-anchor" }.Contains(injectPhase), "Unsupported injected failure phase");
        if (watchMode == "native-clearance-rejection")
            Check(pendingRequest != null && string.IsNullOrEmpty(injectPhase) && request.GetValue("expectedHoldAuthorization") == "clearance-rejection-no-new-spawn", "Clearance rejection requires an exact authorized request and no injected failure");
        if (watchMode == "native-terrain-rejection")
        {
            terrainRejectKind = request.GetValue("terrainRejectKind");
            Check(pendingRequest != null && string.IsNullOrEmpty(injectPhase) && request.GetValue("expectedHoldAuthorization") == "terrain-rejection-before-assembly-no-new-spawn" && (terrainRejectKind == "slope" || terrainRejectKind == "support-gap" || terrainRejectKind == "latent-scatter"), "Terrain rejection requires an exact authorized request/category and no injected failure");
        }
        if (watchMode == "native-failure-phase")
            Check(pendingRequest != null && !string.IsNullOrEmpty(injectPhase) && request.GetValue("failureAuthorization") == "catchable-native-failure-no-recovery", "Failure phase lacks exact bounded request/injection authorization");
        else Check(string.IsNullOrEmpty(injectPhase), "Injected failure is allowed only in separately authorized failure-phase mode");
        string contactAuthorization = request.GetValue("contactDiagnosticAuthorization");
        if (!string.IsNullOrEmpty(contactAuthorization))
        {
            Check(contactAuthorization == "exact-service-native-contact-observation-no-physics-changes" && watchMode == "native-placement" && pendingRequest != null && pendingRequest.TemplateRelativePath == "service-kpbs-v1.craft" && pendingRequest.ExplicitSandboxUnlockOverride,
                "Contact diagnosis requires exact explicitly authorized new service candidate");
            contactDiagnostic = new ColonyPlacementContactDiagnostic(root, pendingRequest);
        }
        DontDestroyOnLoad(this);
        Line("START root=" + root + " save=" + expectedFolder + " operation=" + operation);
        if (!string.IsNullOrEmpty(injectPhase))
        {
            typeof(ColonyPlacementRuntime).GetField("FailureInjector", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null,
                (Action<string, string>)((phase, id) => { if (!injected && id == operation && phase == injectPhase) { injected = true; throw new InvalidOperationException("ARMED_TEST_FAILURE " + phase); } }));
        }
        StartCoroutine(Supervise(Run()));
    }

    private IEnumerator Supervise(IEnumerator work)
    {
        while (true)
        {
            object next = null; bool more;
            try { more = work.MoveNext(); if (more) next = work.Current; }
            catch (Exception e) { Line("FAIL " + e); ClearHandlers(); yield break; }
            if (!more) { ClearHandlers(); yield break; }
            yield return next;
        }
    }

    private IEnumerator Run()
    {
        ResetSweep();
        double deadline = Time.realtimeSinceStartup + 900;
        ColonyPlacementStatus status = null;
        while (status == null || status.Stage != ColonyPlacementStage.Anchored || coldReload && !FullyLoadedMarked(operation))
        {
            Check(Time.realtimeSinceStartup < deadline, "Product operation timed out before anchored readback");
            if (HighLogic.LoadedSceneIsFlight && FlightGlobals.ready && HighLogic.SaveFolder == expectedFolder && FlightGlobals.ActiveVessel != null && FlightGlobals.ActiveVessel.loaded && FlightGlobals.ActiveVessel.parts.Count > 0 && FlightGlobals.ActiveVessel.parts.All(p => p.started))
            {
                if (reference == null)
                {
                    var active = FlightGlobals.ActiveVessel;
                    // A new empty body reference also undergoes ordinary stock
                    // easing. Capture its control/pose witness only after that
                    // ends and its real pose has remained stationary for2s.
                    // Never move, pack, anchor or alter the fixture to pass.
                    if (!active.Landed || active.Splashed || active.HoldPhysics || active.easingInToSurface ||
                        !ColonyPlacementRequest.Finite(active.srfSpeed) || active.srfSpeed > .005 || FlightDriver.Pause || Time.timeScale <= 0 || TimeWarp.CurrentRate != 1)
                    { referenceCandidate = null; referenceStableSince = -1; yield return null; continue; }
                    if (referenceCandidate != active || referenceStableSince < 0 ||
                        Math.Abs(active.latitude-referenceCandidateLatitude) > .000001 || Math.Abs(active.longitude-referenceCandidateLongitude) > .000001)
                    { referenceCandidate = active; referenceCandidateLatitude = active.latitude; referenceCandidateLongitude = active.longitude; referenceStableSince = Time.realtimeSinceStartup; }
                    if (Time.realtimeSinceStartup-referenceStableSince < 2) { yield return null; continue; }
                    reference = FlightGlobals.ActiveVessel; referenceLatitude = reference.latitude; referenceLongitude = reference.longitude;
                    referenceParts = reference.parts.Select(x => x.persistentId).OrderBy(x => x).ToArray();
                    Check(FlightGlobals.Vessels != null && FlightGlobals.Vessels.Count <= 4096, "Reference world vessel inventory exceeds bounds");
                    referenceWorldIds = FlightGlobals.Vessels.Where(v => v != null).Select(v => v.id.ToString("D")).OrderBy(id => id, StringComparer.Ordinal).ToArray();
                    GameEvents.onVesselChange.Add(ControlChanged);
                    Line("REFERENCE id=" + reference.id + " latitude=" + referenceLatitude.ToString("R", CultureInfo.InvariantCulture) + " longitude=" + referenceLongitude.ToString("R", CultureInfo.InvariantCulture));
                }
                Check(!controlChanged && FlightGlobals.ActiveVessel == reference, "Product assembly changed the active vessel, including a transient native event");
                var scenario = ColonyPlacementScenario.Instance;
                if (scenario != null && scenario.Ready)
                {
                    if (pendingRequest != null && !enqueued && !recoverExisting)
                    {
                        DumpLoadedPartDatabase();
                        var catalog = ColonyTemplateCatalog.LoadInstalled();
                        foreach (var issue in catalog.Issues) Line("CATALOG_HOLD " + issue.TemplateId + " " + issue.Reason);
                        Check(catalog.Templates.Any(t => t.CraftRelativePath == pendingRequest.TemplateRelativePath && t.CraftSha256 == pendingRequest.TemplateSha256), "Authorized probe package does not qualify against current installed part definitions");
                        Line("PASS installed package qualification candidates=" + catalog.Templates.Count + " catalogHash=" + catalog.PackageHash);
                        Check(string.IsNullOrEmpty(scenario.WorldId) || scenario.WorldId == pendingRequest.WorldId, "Authorized request world disagrees with selected save's placement authority");
                        if (watchMode == "native-clearance-rejection" || watchMode == "native-terrain-rejection" || watchMode == "native-failure-phase")
                            Check(scenario.GetStatus(operation) == null && CountAllMarked(operation) == 0, "Expected rejection/failure must use a fresh operation in this native world");
                        string enqueueReason;
                        Check(scenario.Enqueue(pendingRequest.Copy(), out status, out enqueueReason), "Product adapter enqueue refused: " + enqueueReason);
                        enqueued = true; Line("ENQUEUED through ColonyPlacementScenario fingerprint=" + expectedFingerprint);
                    }
                    status = scenario.GetStatus(operation);
                    if (recoverExisting && !reconciledExisting && status != null && FullyLoadedMarked(operation))
                    {
                        Check(scenario.WorldId == reloadRequest.WorldId && status.Stage == ColonyPlacementStage.RecoveryHold && status.RequestFingerprint == expectedFingerprint, "Existing recovery no longer binds original held receipt/world");
                        CheckRecoveryBirth(status, Marked(operation).Single());
                        string recoveryReason;
                        Check(scenario.ReconcileExistingBuilding(operation, out recoveryReason), "Exact existing-building reconciliation refused: " + recoveryReason);
                        reconciledExisting = true; status = scenario.GetStatus(operation);
                        Line("RECONCILED original marked building through product API; no new enqueue/assembly; vessel=" + status.VesselId);
                    }
                    if (coldReload && status != null)
                        Check(scenario.WorldId == reloadRequest.WorldId && status.RequestFingerprint == expectedFingerprint && status.VesselId == reloadStatus.VesselId,
                            "Reloaded selected save changed the committed physical operation/world/vessel");
                }
                if (status != null)
                {
                    string stage = status.Stage + " " + status.Reason;
                    if (stage != lastStage) { Line("STATUS " + stage); Snapshot(status); lastStage = stage; }
                    if (requireStrictEnvelope && !coldReload && status.AssemblyAttempted && status.Stage != ColonyPlacementStage.RecoveryHold)
                        ObserveDeploymentSweep(status);
                    if (status.Stage == ColonyPlacementStage.RecoveryHold && (!recoverExisting || reconciledExisting))
                    {
                        if (watchMode == "native-clearance-rejection" || watchMode == "native-terrain-rejection" || watchMode == "native-failure-phase")
                        {
                            // Move the inner iterator here so Supervise catches every
                            // failure after waits, rather than losing nested errors.
                            var qualification = QualifyExpectedHold(status);
                            while (qualification.MoveNext()) yield return qualification.Current;
                            yield break;
                        }
                        ExportUnexpectedHold(status);
                        throw new Exception("Placement blocked: " + status.Reason);
                    }
                }
            }
            yield return null;
        }
        Check(watchMode == "native-placement" || coldReload || recoverExisting && reconciledExisting, "Armed clearance/failure unexpectedly reached Anchored; expected rejection was not proved");
        Check(HighLogic.SaveFolder == expectedFolder, "Save selection changed during placement");
        var buildings = Marked(operation); Check(buildings.Length == 1, "Operation has zero or duplicate marked vessels");
        if (recoverExisting) CheckRecoveryBirth(status,buildings[0]);
        var building = buildings[0];
        if (coldReload) CheckColdReload(status, building);
        Check(building.vesselType == VesselType.Base, "Placed vessel is not classified Base");
        Check(building.GetCrewCount() == 0, "Placement assigned unexpected crew");
        Check(building.packed && building.parts.All(p => p.rb == null || p.rb.isKinematic), "Anchor did not hold physical building");
        Check(building.parts.Select(p => p.persistentId).Distinct().Count() == status.PartCount, "Persistent part identities are not unique");
        Check(building.parts.Select(p => p.flightID).Distinct().Count() == status.PartCount && building.parts.All(p => p.flightID != 0), "Flight identities are zero or duplicated");
        Check(building.parts.All(p => p.Modules.OfType<ColonyPlacementMarker>().Count(m => m.operationId == operation) == 1), "Every part must retain one operation marker");
        Check(reference.parts.Select(x => x.persistentId).OrderBy(x => x).SequenceEqual(referenceParts), "Reference asset membership changed");
        Check(Math.Abs(reference.latitude - referenceLatitude) <= 0.00001 && Math.Abs(reference.longitude - referenceLongitude) <= 0.00001, "Reference base moved during product placement");
        Line("PASS native placement/anchor; vessel=" + building.id + " parts=" + status.PartCount + " slope=" + status.ActualMaximumSlopeDegrees + " gap=" + status.ActualMaximumSupportGapMetres);

        var scenarioInstance = ColonyPlacementScenario.Instance;
        var recordDictionary = (System.Collections.IDictionary)typeof(ColonyPlacementScenario).GetField("Records", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(scenarioInstance);
        object record = recordDictionary[operation];
        var originalRequest = ((ColonyPlacementRequest)record.GetType().GetField("Request").GetValue(record)).Copy();
        WritePhysicalEnvelope(building, originalRequest, status);
        if (requireStrictEnvelope) ExportQualifiedGeometry(record, originalRequest, status);
        if (coldReload) WritePackagePhysicalWitness(building, originalRequest, status);
        if (coldReload) Check(originalRequest.Fingerprint() == expectedFingerprint, "Reloaded queue terms differ from the exact previous native save");
        ColonyPlacementStatus repeated; string reason;
        Check(scenarioInstance.Enqueue(originalRequest, out repeated, out reason), "Duplicate product request rejected equivalent terms: " + reason);
        Check(repeated.VesselId == status.VesselId && repeated.Stage == ColonyPlacementStage.Anchored, "Duplicate request changed its physical receipt");
        for (int i = 0; i < 120; i++) { yield return new WaitForFixedUpdate(); Check(Marked(operation).Length == 1 && !controlChanged && FlightGlobals.ActiveVessel == reference, "Duplicate processing spawned another building or changed control"); }
        Line("PASS repeated operation produced exactly one held building across 120 physics frames");

        // Native ConfigNode serialization is exercised without replacing the active game.
        var codec = typeof(ColonyPlacementScenario).Assembly.GetType("Expanse.WorldBridge.ColonyPlacementCodec");
        var write = codec.GetMethod("WriteRequest", BindingFlags.Static | BindingFlags.NonPublic);
        var read = codec.GetMethod("ReadRequest", BindingFlags.Static | BindingFlags.NonPublic);
        string roundTripFingerprint = originalRequest.Fingerprint();
        for (int i = 0; i < 1000; i++)
        {
            var node = (ConfigNode)write.Invoke(null, new object[] { originalRequest });
            originalRequest = (ColonyPlacementRequest)read.Invoke(null, new object[] { ConfigNode.Parse(node.ToString()).GetNode("REQUEST") });
            Check(originalRequest.Fingerprint() == roundTripFingerprint, "Native ConfigNode changed committed placement terms");
        }
        Line("PASS 1,000 native request round trips");
        Check(!File.Exists(Path.Combine(root, "saves", expectedFolder, saveName + ".sfs")), "Witness save already exists; choose a new acceptance save name");
        GamePersistence.SaveGame(saveName, expectedFolder, SaveMode.OVERWRITE);
        var raw = ConfigNode.Load(Path.Combine(root, "saves", expectedFolder, saveName + ".sfs")).GetNode("GAME");
        var placementScenario = raw.GetNodes("SCENARIO").Single(x => x.GetValue("name") == "ColonyPlacementScenario");
        var savedOperation = placementScenario.GetNodes("PLACEMENT").Single(x => x.GetNode("REQUEST").GetValue("OperationId") == operation);
        Check(savedOperation.GetNode("WITNESS").GetValue("Stage") == "Anchored", "Durable save lost anchor placement receipt");
        var savedVessels = raw.GetNode("FLIGHTSTATE").GetNodes("VESSEL").Where(v => v.GetNodes("PART").Any(p => p.GetNodes("MODULE").Any(m => m.GetValue("name") == "ColonyPlacementMarker" && m.GetValue("operationId") == operation))).ToArray();
        Check(savedVessels.Length == 1, "Durable save contains zero or duplicate marked buildings");
        Check(savedVessels[0].GetNodes("PART").Length == status.PartCount, "Durable save changed physical membership");
        Check(raw.GetNodes("SCENARIO").Any(x => x.GetValue("name") == "FoundationRegistry" && x.GetNodes("ANCHOR").Any(a => a.GetValue("id") == status.FoundationId)), "Durable save lost Foundations anchor");
        Line("PASS actual KSP save readback " + saveName + "; checks=" + checks + "; process=" + System.Diagnostics.Process.GetCurrentProcess().Id);
        Snapshot(scenarioInstance.GetStatus(operation));
        if (coldReload)
        {
            string source = Path.Combine(root, "saves", expectedFolder, reloadSourceName + ".sfs");
            Check(Sha(File.ReadAllBytes(source)) == reloadSourceHash, "Cold acceptance modified its original native source save");
            Line("PASS cold-process native reload retained exact vessel/part/flight/marker/Foundation identities; source process=" + priorProcess + "; current process=" + System.Diagnostics.Process.GetCurrentProcess().Id);
        }
        Line("REMAINING " + (coldReload ? "" : "cold-process reload, ") + "scene transition, floating-origin, paid commissioning and every failure phase require independent follow-up runs");
    }

    private void ArmPackageBatch(ConfigNode authorization, string name)
    {
        string path = Path.Combine(root, name); CheckPath(path);
        Check(File.Exists(path) && new FileInfo(path).Length <= 8192, "Batch absent or exceeds 8 KiB");
        byte[] bytes = File.ReadAllBytes(path);
        Check(Sha(bytes) == authorization.GetValue("batchRequestSha256"), "Exact package batch hash differs");
        var node = ConfigNode.Parse(Encoding.UTF8.GetString(bytes));
        Check(node != null && node.values.Count == 0 && node.nodes.Count == 1 && node.GetNodes("BATCH").Length == 1, "Batch requires exactly one BATCH");
        var batch = node.GetNode("BATCH"); batchCases = batch.GetNodes("CASE");
        int expectedCount = streetGroup ? 5 : 8;
        Check(batch.values.Count == 0 && batch.nodes.Count == expectedCount && batchCases.Length == expectedCount && batchCases.Select(c => c.GetValue("operationId")).Distinct(StringComparer.Ordinal).Count() == expectedCount, "Exact bounded unique package operations required");
        Check(batchCases[0].GetValue("operationId") == operation, "Batch first operation differs from explicit watcher identity");
        foreach (var entry in batchCases)
        {
            Check(SafeName(entry.GetValue("operationId")) && SafeName(entry.GetValue("saveName")) && entry.GetValue("saveName").StartsWith("colony-placement-", StringComparison.Ordinal), "Invalid batch operation or native witness save name");
            bool originalHousing = streetGroup && !coldBatch && entry == batchCases[0] && entry.GetValue("existingAuthorization") == "original-anchored-housing-no-new-enqueue";
            Check(entry.nodes.Count == 0 && entry.values.Count == (originalHousing ? 6 : coldBatch ? 5 : 4), "Unexpected package batch fields");
            if (coldBatch || originalHousing)
                Check(SafeNativeSourceName(entry.GetValue("reloadSourceName")), "Invalid original native package save name");
            else
            {
                string file = entry.GetValue("enqueueRequestName");
                Check(file != null && file.StartsWith("colony-placement-request-", StringComparison.Ordinal) && file.EndsWith(".cfg", StringComparison.Ordinal) && SafeName(file.Substring(0, file.Length - 4)), "Invalid exact package request file name");
            }
        }
        unloadedSaveName = authorization.GetValue("unloadedSaveName");
        Check(authorization.GetValue("unloadedAuthorization") == "stock-scene-exit-native-proto-readback" && SafeName(unloadedSaveName) && unloadedSaveName.StartsWith("colony-test-", StringComparison.Ordinal), "Package batch needs exact stock-exit unloaded witness authorization");
        Line("ARMED " + (coldBatch ? "cold" : "new") + " " + expectedCount + "-package batch sha256=" + Sha(bytes) + "; no economics, template certification or direct object creation");
    }

    private IEnumerator RunPackageBatch()
    {
        var templatePaths = new HashSet<string>(StringComparer.Ordinal);
        string streetBody = null;
        foreach (var entry in batchCases)
        {
            operation = entry.GetValue("operationId"); saveName = entry.GetValue("saveName");
            bool originalHousing = streetGroup && !coldBatch && entry == batchCases[0] && entry.GetValue("existingAuthorization") == "original-anchored-housing-no-new-enqueue";
            pendingRequest = null; enqueued = false; lastStage = null; coldReload = coldBatch || originalHousing; requireStrictEnvelope = true;
            if (coldReload)
            {
                watchMode = "cold-native-reload"; reloadSourceName = entry.GetValue("reloadSourceName"); reloadSourceHash = entry.GetValue("reloadSourceSha256");
                Check(int.TryParse(entry.GetValue("reloadPriorProcessId"), out priorProcess) && priorProcess > 0 && priorProcess != System.Diagnostics.Process.GetCurrentProcess().Id, "Cold batch needs independent previous process");
                reloadCraftPartIds.Clear(); ReadColdReloadSource();
                Check(templatePaths.Add(reloadRequest.TemplateRelativePath), "Cold batch repeats a package rather than qualifying distinct packages");
                if (originalHousing) Check(reloadRequest.TemplateRelativePath == "housing-kpbs-v1.craft", "Street original readback is not the exact housing package");
            }
            else
            {
                watchMode = "native-placement";
                string path = Path.Combine(root, entry.GetValue("enqueueRequestName")); CheckPath(path);
                Check(File.Exists(path) && new FileInfo(path).Length <= 65536, "Exact batch request absent or exceeds 64 KiB");
                byte[] bytes = File.ReadAllBytes(path); requestHash = Sha(bytes);
                Check(requestHash == entry.GetValue("enqueueRequestSha256"), "Exact batch request hash differs");
                var tree = ConfigNode.Parse(Encoding.UTF8.GetString(bytes));
                Check(tree != null && tree.values.Count == 0 && tree.nodes.Count == 1 && tree.GetNodes("REQUEST").Length == 1, "Exact batch request requires one REQUEST");
                var codec = typeof(ColonyPlacementScenario).Assembly.GetType("Expanse.WorldBridge.ColonyPlacementCodec");
                pendingRequest = (ColonyPlacementRequest)codec.GetMethod("ReadRequest", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { tree.GetNode("REQUEST") });
                Check(pendingRequest.Validate() == null && pendingRequest.OperationId == operation && templatePaths.Add(pendingRequest.TemplateRelativePath), "Invalid/repeated package terms in bounded batch");
                expectedFingerprint = pendingRequest.Fingerprint();
            }
            if (streetGroup)
            {
                string[] streetPaths = { "housing-kpbs-v1.craft", "service-kpbs-v1.craft", "storage-kpbs-v1.craft", "lamp-stock-v1.craft", "power-duna-v1.craft" };
                var terms = coldReload ? reloadRequest : pendingRequest;
                Check(terms != null && terms.TemplateRelativePath == streetPaths[Array.IndexOf(batchCases, entry)] && (terms.BodyName == "Mun" || terms.BodyName == "Duna"), "Street must contain the exact ordered five packages on Mun or Duna");
                Check(streetBody == null || streetBody == terms.BodyName, "Street packages changed body");
                streetBody = terms.BodyName;
            }
            Line("START root=" + root + " save=" + expectedFolder + " operation=" + operation);
            var single = Run(); while (single.MoveNext()) yield return single.Current;
            var status = ColonyPlacementScenario.Instance.GetStatus(operation);
            Check(status != null && status.Stage == ColonyPlacementStage.Anchored, "Batch package lacks actual anchored receipt");
            WritePackagePhysicalWitness(Marked(operation).Single(), coldReload ? reloadRequest : pendingRequest, status);
            batchStatuses.Add(operation, status);
        }
        Line("PASS BATCH_LOADED " + batchCases.Length + " distinct actual product packages; final native source=" + saveName + "; process=" + System.Diagnostics.Process.GetCurrentProcess().Id);
        ClearHandlers();
        var unloaded = QualifyUnloadedBatch(); while (unloaded.MoveNext()) yield return unloaded.Current;
    }

    private void WritePackagePhysicalWitness(Vessel vessel, ColonyPlacementRequest request, ColonyPlacementStatus status)
    {
        var catalog = ColonyTemplateCatalog.LoadInstalled();
        var template = catalog.Templates.Single(t => t.CraftRelativePath == request.TemplateRelativePath && t.CraftSha256 == request.TemplateSha256);
        var source = ConfigNode.Load(Path.Combine(root, "GameData", "ExpanseWorldBridge", "Templates", request.TemplateRelativePath));
        Check(source != null && source.GetNodes("PART").Length == template.ExpectedPartCount && vessel.parts.Count == template.ExpectedPartCount, "Actual package part count differs from exact craft");
        var parts = source.GetNodes("PART").ToDictionary(p => uint.Parse(p.GetValue("part").Substring(p.GetValue("part").LastIndexOf('_') + 1), CultureInfo.InvariantCulture));
        var witness = new ConfigNode("COLONY_PACKAGE_PHYSICAL_WITNESS");
        witness.AddValue("operationId", operation); witness.AddValue("vesselId", vessel.id); witness.AddValue("foundationId", status.FoundationId); witness.AddValue("craftSha256", request.TemplateSha256); witness.AddValue("templateHash", template.Hash);
        var electricity = PartResourceLibrary.Instance.GetDefinition("ElectricCharge");
        Check(electricity != null, "Actual native ElectricCharge definition missing");
        witness.AddValue("actualElectricChargeResourceFlowMode", electricity.resourceFlowMode);
        witness.AddValue("observedUt", Planetarium.GetUniversalTime().ToString("R", CultureInfo.InvariantCulture)); witness.AddValue("runtimeCertified", false);
        foreach (var part in vessel.parts)
        {
            var marker = part.Modules.OfType<ColonyPlacementMarker>().Single(); ConfigNode original;
            Check(parts.TryGetValue(marker.craftPartId, out original), "Actual birth marker does not map to exact source craft part");
            string sourceName = original.GetValue("part").Substring(0, original.GetValue("part").LastIndexOf('_')).Replace('_', '.');
            Check(part.partInfo != null && part.partInfo.name == sourceName && marker.templateSha256 == request.TemplateSha256 && marker.requestFingerprint == expectedFingerprint, "Actual package part identity/marker differs from hash-bound craft");
            var row = witness.AddNode("PART"); row.AddValue("craftPartId", marker.craftPartId); row.AddValue("persistentId", part.persistentId); row.AddValue("flightId", part.flightID); row.AddValue("partName", part.partInfo.name); row.AddValue("crewCapacity", part.CrewCapacity);
            row.AddValue("partStarted", part.started); row.AddValue("primaryColliderPresent", part.collider != null);
            if (part.collider != null) { row.AddValue("primaryColliderEnabled", part.collider.enabled); row.AddValue("primaryColliderIsTrigger", part.collider.isTrigger); row.AddValue("primaryColliderLayer", part.collider.gameObject.layer); }
            row.AddValue("explicitHome", template.HomeCraftPartIds.Contains(marker.craftPartId)); row.AddValue("actualHomeQualification", ColonyRuntime.QualifyHomePart(vessel, part, template));
            row.AddValue("temperature", part.temperature.ToString("R", CultureInfo.InvariantCulture)); row.AddValue("maximumTemperature", part.maxTemp.ToString("R", CultureInfo.InvariantCulture));
            foreach (var resource in part.Resources) { Check(resource.amount >= 0 && resource.amount <= resource.maxAmount + .000001, "Actual package tank is outside native capacity"); var tank = row.AddNode("RESOURCE"); tank.AddValue("name", resource.resourceName); tank.AddValue("amount", resource.amount.ToString("R", CultureInfo.InvariantCulture)); tank.AddValue("capacity", resource.maxAmount.ToString("R", CultureInfo.InvariantCulture)); tank.AddValue("flowState", resource.flowState); tank.AddValue("definitionFlowMode", resource.info.resourceFlowMode); }
            foreach (PartModule module in part.Modules) { var actual = row.AddNode("MODULE"); actual.AddValue("name", module.moduleName); var deployment = module.Fields["moduleStatus"]; if (deployment != null) actual.AddValue("moduleStatus", deployment.GetValue(module)); }
        }
        var qualification = ColonyRuntime.QualifyPlacedFacility(vessel, template); var q = witness.AddNode("UTILITY_AND_HOME_OBSERVATION");
        foreach (var property in qualification.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)) q.AddValue(property.Name, property.GetValue(qualification, null));
        // This dev collector is bound to reviewed immutable native14. Private
        // provider shape changes must refuse, never guess arguments from count.
        Check(Sha(File.ReadAllBytes(typeof(ColonyRuntime).Assembly.Location)) == "30dcb250680c952ad41d3b7090e1324a28fe4519926482b35344991a08b3b607", "Full utility collector requires exact reviewed native14 Bridge");
        var observationType = typeof(ColonyRuntime).Assembly.GetType("Expanse.WorldBridge.ColonyUtilityInstructionObservation", false);
        Check(observationType != null, "Reviewed native14 actual instruction observation type missing");
        var utilityReader = typeof(ColonyRuntime).GetMethod("ReadUtilities", BindingFlags.Static | BindingFlags.NonPublic, null,
            new[] { typeof(Vessel), typeof(string), typeof(string), typeof(double), typeof(ConfigNode[]), observationType }, null);
        var adapters = typeof(ColonyRuntime).GetMethod("ReadUtilityAdapterConfiguration", BindingFlags.Static | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
        var instructions = typeof(ColonyRuntime).GetMethod("CreateUtilityInstructionObservation", BindingFlags.Static | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
        Check(utilityReader != null && utilityReader.ReturnType.FullName == "Expanse.Domain.Colonies.ColonyUtilityReport" && adapters != null && adapters.ReturnType == typeof(ConfigNode[]) &&
            instructions != null && instructions.ReturnType == observationType && ColonyRuntime.Current != null && vessel.loaded, "Exact reviewed native14 loaded utility provider/factories unavailable");
        var utilityReport = utilityReader.Invoke(null, new object[] { vessel, "", ColonyRuntime.Current.ContextKey, Planetarium.GetUniversalTime(), adapters.Invoke(null, null), instructions.Invoke(null, null) });
        Check(utilityReport != null, "Actual native full utility report missing");
        var full = witness.AddNode("FULL_NATIVE_UTILITY_REPORT");
        foreach (var property in utilityReport.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)) full.AddValue(property.Name, property.GetValue(utilityReport, null));
        string path = Path.Combine(root, (coldReload ? "colony-placement-package-reload-" : "colony-placement-package-") + operation + ".cfg"); CheckPath(path); File.WriteAllText(path, witness.ToString());
        Line("PACKAGE_PHYSICAL_WITNESS " + template.Id + " parts=" + vessel.parts.Count + "; explicitHomeParts=" + template.HomeCraftPartIds.Count + "; operational qualification remains separate");
    }

    private static bool SafeNativeSourceName(string name)
    { return SafeName(name) && (name.StartsWith("colony-placement-", StringComparison.Ordinal) || name.StartsWith("colony-test-", StringComparison.Ordinal)); }

    private IEnumerator QualifyUnloadedBatch()
    {
        double deadline = Time.realtimeSinceStartup + 1200;
        while (!HighLogic.LoadedSceneIsGame || HighLogic.LoadedScene != GameScenes.SPACECENTER || HighLogic.CurrentGame == null || ColonyPlacementScenario.Instance == null || !ColonyPlacementScenario.Instance.Ready)
        { Check(Time.realtimeSinceStartup < deadline, "Stock scene-exit unloaded witness timed out"); yield return null; }
        Check(HighLogic.SaveFolder == expectedFolder, "Stock scene exit changed selected native save");
        var game = HighLogic.CurrentGame;
        Check(game.flightState != null && game.flightState.protoVessels != null && game.flightState.protoVessels.Count <= 4096, "Actual unloaded game lacks bounded proto inventory");
        var foundationType = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("Expanse.Foundations.FoundationRegistry", false)).FirstOrDefault(t => t != null);
        Check(foundationType != null, "Actual unloaded Foundation provider is absent");
        var registry = foundationType.GetField("Instance", BindingFlags.Static | BindingFlags.Public).GetValue(null);
        Check(registry != null && (bool)foundationType.GetField("Ready", BindingFlags.Instance | BindingFlags.Public).GetValue(registry), "Actual unloaded Foundation registry is not ready");
        var anchors = (System.Collections.IDictionary)foundationType.GetField("Anchors", BindingFlags.Instance | BindingFlags.Public).GetValue(registry);
        var loadedSource = ConfigNode.Load(Path.Combine(root, "saves", expectedFolder, saveName + ".sfs")).GetNode("GAME");
        var evidence = new ConfigNode("COLONY_PACKAGE_UNLOADED_WITNESS"); evidence.AddValue("saveFolder", expectedFolder); evidence.AddValue("scene", HighLogic.LoadedScene); evidence.AddValue("observedUt", Planetarium.GetUniversalTime().ToString("R", CultureInfo.InvariantCulture));
        foreach (var pair in batchStatuses)
        {
            var expected = pair.Value; var current = ColonyPlacementScenario.Instance.GetStatus(pair.Key);
            Check(current != null && current.Stage == ColonyPlacementStage.Anchored && current.VesselId == expected.VesselId && current.RequestFingerprint == expected.RequestFingerprint && current.FoundationId == expected.FoundationId && current.PartPersistentIds.SequenceEqual(expected.PartPersistentIds), "Actual unloaded queue lost immutable physical lineage");
            var proto = game.flightState.protoVessels.Single(v => v.vesselID.ToString("D") == expected.VesselId);
            Check(proto.protoPartSnapshots.Count == expected.PartCount && proto.protoPartSnapshots.Select(p => p.persistentId).OrderBy(x => x).SequenceEqual(expected.PartPersistentIds.OrderBy(x => x)) && proto.protoPartSnapshots.Select(p => p.flightID).OrderBy(x => x).SequenceEqual(expected.FlightIds.OrderBy(x => x)), "Actual unloaded proto part/flight identity differs");
            Check(proto.protoPartSnapshots.All(p => p.modules.Count(m => m.moduleName == "ColonyPlacementMarker" && m.moduleValues.GetValue("operationId") == pair.Key && m.moduleValues.GetValue("requestFingerprint") == expected.RequestFingerprint) == 1), "Actual unloaded proto lost exact birth markers");
            Check(anchors.Contains(expected.FoundationId), "Actual unloaded Foundation registry lost original anchor");
            object anchor = anchors[expected.FoundationId];
            var actualAnchor = (ConfigNode)anchor.GetType().GetMethod("Save", BindingFlags.Instance | BindingFlags.Public).Invoke(anchor, null);
            var originalAnchor = loadedSource.GetNodes("SCENARIO").Single(n => n.GetValue("name") == "FoundationRegistry").GetNodes("ANCHOR").Single(n => n.GetValue("id") == expected.FoundationId);
            Check(actualAnchor.ToString() == originalAnchor.ToString(), "Actual stock unloaded Foundation pose/membership differs from original loaded native save");
            var row = evidence.AddNode("PACKAGE"); row.AddValue("operationId", pair.Key); row.AddValue("vesselId", expected.VesselId); row.AddValue("foundationId", expected.FoundationId); row.AddValue("partCount", proto.protoPartSnapshots.Count); row.AddNode(actualAnchor.CreateCopy());
        }
        string evidencePath = Path.Combine(root, "colony-placement-unloaded-" + batchCases[0].GetValue("operationId") + ".cfg"); CheckPath(evidencePath); File.WriteAllText(evidencePath, evidence.ToString());
        Line("PASS actual stock SpaceCenter unloaded proto inventory retained all " + batchCases.Length + " queue/vessel/part/flight/marker identities; awaiting independently requested native save " + unloadedSaveName);
        string path = Path.Combine(root, "saves", expectedFolder, unloadedSaveName + ".sfs"); CheckPath(path);
        while (!File.Exists(path)) { Check(Time.realtimeSinceStartup < deadline, "Independent unloaded native save witness timed out"); yield return null; }
        var raw = ConfigNode.Load(path).GetNode("GAME");
        foreach (var pair in batchStatuses)
        {
            var expected = pair.Value;
            var vessel = raw.GetNode("FLIGHTSTATE").GetNodes("VESSEL").Single(v => Guid.Parse(v.GetValue("pid")).ToString("D") == expected.VesselId);
            Check(vessel.GetNodes("PART").Select(p => uint.Parse(p.GetValue("persistentId"), CultureInfo.InvariantCulture)).OrderBy(x => x).SequenceEqual(expected.PartPersistentIds.OrderBy(x => x)), "Unloaded native save lost package membership");
            Check(vessel.GetNodes("PART").All(p => p.GetNodes("MODULE").Count(m => m.GetValue("name") == "ColonyPlacementMarker" && m.GetValue("operationId") == pair.Key && m.GetValue("requestFingerprint") == expected.RequestFingerprint) == 1), "Unloaded native save lost exact operation markers");
            var receipt = raw.GetNodes("SCENARIO").Single(n => n.GetValue("name") == "ColonyPlacementScenario").GetNodes("PLACEMENT").Single(n => n.GetNode("REQUEST").GetValue("OperationId") == pair.Key).GetNode("WITNESS");
            Check(receipt.GetValue("Stage") == "Anchored" && receipt.GetValue("VesselId") == expected.VesselId && receipt.GetValue("FoundationId") == expected.FoundationId && receipt.GetValue("RequestFingerprint") == expected.RequestFingerprint, "Unloaded native save changed the queue lineage");
            var anchor = raw.GetNodes("SCENARIO").Single(n => n.GetValue("name") == "FoundationRegistry").GetNodes("ANCHOR").Single(n => n.GetValue("id") == expected.FoundationId);
            Check(anchor.GetNodes("MEMBER").Select(n => uint.Parse(n.GetValue("id"), CultureInfo.InvariantCulture)).OrderBy(x => x).SequenceEqual(expected.PartPersistentIds.OrderBy(x => x)), "Unloaded native save lost original Foundation membership");
        }
        Line("PASS BATCH_UNLOADED all " + batchCases.Length + " original packages in actual stock scene and native save " + unloadedSaveName + "; sha256=" + Sha(File.ReadAllBytes(path)) + "; checks=" + checks + "; process=" + System.Diagnostics.Process.GetCurrentProcess().Id);
    }

    private IEnumerator QualifyExpectedHold(ColonyPlacementStatus status)
    {
        bool clearance = watchMode == "native-clearance-rejection";
        bool terrainRejection = watchMode == "native-terrain-rejection";
        bool beforeAssembly = clearance || terrainRejection || injectPhase == "before-assembly";
        bool afterAnchor = injectPhase == "after-anchor";
        int expectedBuildings = beforeAssembly ? 0 : 1;
        Check(clearance ? status.Reason.Contains("Deployment or access envelope intersects existing hardware") : terrainRejection ? status.Reason.Contains(terrainRejectKind == "slope" ? "Surveyed slope exceeds the template's certified limit:" : terrainRejectKind == "latent-scatter" ? "Complete deployment/support/access envelope intersects latent native Parallax scatter" : "Terrain support gap exceeds certified supports:") : injected && status.Reason.Contains("ARMED_TEST_FAILURE " + injectPhase),
            "Held operation did not report the exact armed physical clearance/failure phase");
        Check(status.Stage == ColonyPlacementStage.RecoveryHold && status.AssemblyAttempted == !beforeAssembly && CountAllMarked(operation) == expectedBuildings,
            "Expected hold has different assembly/marked-building evidence");
        var scenario = ColonyPlacementScenario.Instance;
        var dictionary = (System.Collections.IDictionary)typeof(ColonyPlacementScenario).GetField("Records", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(scenario);
        var record = dictionary[operation]; var original = ((ColonyPlacementRequest)record.GetType().GetField("Request").GetValue(record)).Copy();
        Check(original.Fingerprint() == expectedFingerprint, "Held immutable request terms changed");
        if (terrainRejection)
        {
            var template = ColonyTemplateCatalog.LoadInstalled().Templates.Single(t => t.CraftRelativePath == original.TemplateRelativePath && t.CraftSha256 == original.TemplateSha256);
            var measured = ColonySiteSurvey.Survey(template, original.BodyName, original.Latitude, original.Longitude, original.HeadingDegrees, new Expanse.Domain.Colonies.ColonyPlot[0], ColonyRuntime.Current.ContextKey);
            Check(!measured.Clear && (terrainRejectKind == "slope" ? measured.MaximumSlopeDegrees > template.MaximumSlopeDegrees : terrainRejectKind == "latent-scatter" ? measured.Reason.Contains("Complete deployment/support/access envelope intersects latent native Parallax scatter") && measured.SurfaceCollisionWitness.Length == 64 && status.SurfaceCollisionWitness.Length == 64 : measured.MaximumSupportGapMetres > template.MaximumSupportGapMetres), "Independent actual loaded terrain survey does not corroborate the armed rejection category");
            var terrain = new ConfigNode("COLONY_TERRAIN_REJECTION_WITNESS");
            terrain.AddValue("operationId", operation); terrain.AddValue("requestFingerprint", expectedFingerprint); terrain.AddValue("body", original.BodyName);
            terrain.AddValue("latitude", original.Latitude.ToString("R", CultureInfo.InvariantCulture)); terrain.AddValue("longitude", original.Longitude.ToString("R", CultureInfo.InvariantCulture));
            terrain.AddValue("maximumSlopeDegrees", measured.MaximumSlopeDegrees.ToString("R", CultureInfo.InvariantCulture)); terrain.AddValue("maximumSupportGapMetres", measured.MaximumSupportGapMetres.ToString("R", CultureInfo.InvariantCulture));
            terrain.AddValue("sampleCount", measured.SampleCount); terrain.AddValue("terrainWitness", measured.TerrainWitness); terrain.AddValue("contextKey", measured.ContextKey); terrain.AddValue("reason", status.Reason); terrain.AddValue("assemblyAttempted", false);
            terrain.AddValue("surfaceCollisionWitness", measured.SurfaceCollisionWitness); terrain.AddValue("rejectionKind", terrainRejectKind);
            terrain.AddValue("heldSurfaceCollisionWitness", status.SurfaceCollisionWitness);
            string terrainPath = Path.Combine(root, "colony-placement-terrain-rejection-" + operation + ".cfg"); CheckPath(terrainPath); Check(!File.Exists(terrainPath), "Original terrain rejection witness exists"); terrain.Save(terrainPath);
        }
        string originalVesselId = status.VesselId; uint[] originalParts = (uint[])status.PartPersistentIds.Clone();
        string actualFoundation = "";
        for (int frame = 0; frame < 120; frame++)
        {
            ColonyPlacementStatus repeated; string reason;
            Check(scenario.Enqueue(original.Copy(), out repeated, out reason), "Exact held duplicate request was rejected: " + reason);
            Check(repeated.Stage == ColonyPlacementStage.RecoveryHold && repeated.RequestFingerprint == expectedFingerprint && repeated.VesselId == originalVesselId && repeated.PartPersistentIds.SequenceEqual(originalParts) &&
                repeated.AssemblyAttempted == !beforeAssembly && repeated.SurfaceCollisionWitness == status.SurfaceCollisionWitness, "Duplicate request changed the held external witness");
            yield return new WaitForFixedUpdate();
            Check(HighLogic.SaveFolder == expectedFolder && !controlChanged && FlightGlobals.ActiveVessel == reference && CountAllMarked(operation) == expectedBuildings,
                "Held duplicate processing created another building or changed selected control");
            Check(reference.parts.Select(p => p.persistentId).OrderBy(id => id).SequenceEqual(referenceParts) && Math.Abs(reference.latitude - referenceLatitude) <= .00001 && Math.Abs(reference.longitude - referenceLongitude) <= .00001,
                "Expected failure changed reference membership/pose");
            var unrelated = FlightGlobals.Vessels.Where(v => v != null && v.id.ToString("D") != originalVesselId).Select(v => v.id.ToString("D")).OrderBy(id => id, StringComparer.Ordinal).ToArray();
            Check(unrelated.SequenceEqual(referenceWorldIds), "Failure changed world vessel membership outside its one original operation");
        }
        if (!beforeAssembly)
        {
            var buildings = Marked(operation); Check(buildings.Length == 1 && buildings[0].parts.All(p => p.started), "Failure's original marked vessel is not fully loaded");
            var vessel = buildings[0];
            Check(vessel.id.ToString("D") == originalVesselId && vessel.parts.Select(p => p.persistentId).OrderBy(id => id).SequenceEqual(status.PartPersistentIds.OrderBy(id => id)) &&
                vessel.parts.Select(p => p.flightID).OrderBy(id => id).SequenceEqual(status.FlightIds.OrderBy(id => id)) && vessel.GetCrewCount() == 0, "Failure lost original native vessel/part/flight/crew evidence");
            Check(vessel.parts.All(p => p.Modules.OfType<ColonyPlacementMarker>().Count(m => m.operationId == operation && m.requestFingerprint == expectedFingerprint && m.templateSha256 == original.TemplateSha256) == 1), "Failure lost exact birth markers");
            var foundations = typeof(ColonyPlacementScenario).Assembly.GetType("Expanse.WorldBridge.ColonyPlacementFoundations");
            bool held = (bool)foundations.GetMethod("IsHeld", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { vessel });
            Check(held == afterAnchor, "Failure physical Foundation state differs from armed phase");
            if (afterAnchor)
            {
                var evidence = new object[] { vessel, null, 0.0, 0.0 };
                Check((bool)foundations.GetMethod("ReadWitness", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, evidence) && (double)evidence[2] <= .01 && (double)evidence[3] <= .01,
                    "Interrupted anchor lacks actual physical Foundation witness");
                actualFoundation = (string)evidence[1];
                // Exception occurs after AnchorBase and before CompleteAnchor: do
                // not patch the product receipt to pretend callback completion.
                Check(string.IsNullOrEmpty(status.FoundationId), "After-anchor interruption unexpectedly claimed completed queue handoff");
            }
            WritePhysicalEnvelope(vessel, original, status);
        }
        Check(!File.Exists(Path.Combine(root, "saves", expectedFolder, saveName + ".sfs")), "Failure witness native save already exists");
        GamePersistence.SaveGame(saveName, expectedFolder, SaveMode.OVERWRITE);
        var game = ConfigNode.Load(Path.Combine(root, "saves", expectedFolder, saveName + ".sfs")).GetNode("GAME");
        var saved = game.GetNodes("SCENARIO").Single(n => n.GetValue("name") == "ColonyPlacementScenario").GetNodes("PLACEMENT").Single(n => n.GetNode("REQUEST").GetValue("OperationId") == operation);
        Check(saved.GetNode("WITNESS").GetValue("Stage") == "RecoveryHold" && bool.Parse(saved.GetNode("WITNESS").GetValue("AssemblyAttempted")) == !beforeAssembly, "Native save lost held receipt phase/effect ambiguity");
        Check(saved.GetNode("WITNESS").GetValue("SurfaceCollisionWitness") == status.SurfaceCollisionWitness, "Native save lost the exact held collision-provider witness");
        int nativeBuildings = game.GetNode("FLIGHTSTATE").GetNodes("VESSEL").Count(v => v.GetNodes("PART").Any(p => p.GetNodes("MODULE").Any(m => m.GetValue("name") == "ColonyPlacementMarker" && m.GetValue("operationId") == operation)));
        Check(nativeBuildings == expectedBuildings, "Native save duplicated/lost failure's marked building");
        if (afterAnchor) Check(game.GetNodes("SCENARIO").Any(n => n.GetValue("name") == "FoundationRegistry" && n.GetNodes("ANCHOR").Any(a => a.GetValue("id") == actualFoundation)), "Native save lost actual interrupted anchor");
        Snapshot(scenario.GetStatus(operation));
        Line("PASS native " + (clearance ? "clearance rejection before assembly" : terrainRejection ? "terrain " + terrainRejectKind + " rejection before assembly" : "catchable injected " + injectPhase) + "; held duplicate120frames; buildings=" + expectedBuildings + "; actualFoundation=" + actualFoundation + "; save=" + saveName + "; checks=" + checks + "; process=" + System.Diagnostics.Process.GetCurrentProcess().Id);
        Line("REMAINING independent cold reload and explicit provider/paid recovery qualification; no retry, reconciliation, cancellation, refund or replacement attempted");
    }

    private void WritePhysicalEnvelope(Vessel vessel, ColonyPlacementRequest request, ColonyPlacementStatus status)
    {
        var body = vessel.mainBody;
        var center = body.GetWorldSurfacePosition(request.Latitude, request.Longitude, status.SurveyTerrainHeight);
        var radial = (center - body.position).normalized; RaycastHit terrain;
        Check(Physics.Raycast((Vector3)(center + radial * 50), (Vector3)(-radial), out terrain, 100, 1 << 15, QueryTriggerInteraction.Ignore), "Deployed envelope diagnostic lacks actual loaded center terrain");
        var north = Vector3.ProjectOnPlane((Vector3)(body.GetWorldSurfacePosition(request.Latitude + .001, request.Longitude, status.SurveyTerrainHeight) - center), terrain.normal).normalized;
        Check(north.sqrMagnitude > .9f, "Deployed envelope north frame is unavailable");
        var inverse = Quaternion.Inverse(Quaternion.LookRotation(Quaternion.AngleAxis((float)request.HeadingDegrees, terrain.normal) * north, terrain.normal));
        double minX = double.PositiveInfinity, maxX = double.NegativeInfinity, minZ = double.PositiveInfinity, maxZ = double.NegativeInfinity, minY = double.PositiveInfinity, maxY = double.NegativeInfinity;
        int colliders = 0;
        foreach (var part in vessel.parts) foreach (var collider in part.GetComponentsInChildren<Collider>())
        {
            if (!collider.enabled || collider.isTrigger || collider.gameObject.layer == 21) continue;
            Check(++colliders <= 4096, "Deployed envelope collider diagnostic exceeds bound");
            var bounds = ColonyPlacementColliderShapeWitness.Read(collider,terrain.point,inverse);
            minX = Math.Min(minX,bounds.min.x); maxX = Math.Max(maxX,bounds.max.x); minZ = Math.Min(minZ,bounds.min.z); maxZ = Math.Max(maxZ,bounds.max.z); minY = Math.Min(minY,bounds.min.y); maxY = Math.Max(maxY,bounds.max.y);
        }
        Check(colliders > 0, "Deployed envelope contains no enabled physical colliders");
        var report = new ConfigNode("COLONY_DEPLOYED_ENVELOPE_WITNESS");
        report.AddValue("operationId", operation); report.AddValue("requestFingerprint", request.Fingerprint()); report.AddValue("vesselId", vessel.id.ToString("D")); report.AddValue("saveFolder", expectedFolder);
        report.AddValue("body", body.bodyName); report.AddValue("observedUt", Planetarium.GetUniversalTime().ToString("R", CultureInfo.InvariantCulture)); report.AddValue("stage", status.Stage); report.AddValue("colliderCount", colliders);
        report.AddValue("method", "Native box geometry and conservative complete mesh local bounds transformed into measured surface frame; sphere/capsule analytic support intervals; unknown shapes hold; diagnostic, not certificate");
        report.AddValue("actualMinX", minX.ToString("R", CultureInfo.InvariantCulture)); report.AddValue("actualMaxX", maxX.ToString("R", CultureInfo.InvariantCulture)); report.AddValue("actualMinZ", minZ.ToString("R", CultureInfo.InvariantCulture)); report.AddValue("actualMaxZ", maxZ.ToString("R", CultureInfo.InvariantCulture));
        report.AddValue("actualMinY", minY.ToString("R", CultureInfo.InvariantCulture)); report.AddValue("actualMaxY", maxY.ToString("R", CultureInfo.InvariantCulture));
        bool strict = minX >= request.MinX - .02 && maxX <= request.MaxX + .02 && minZ >= request.MinZ - .02 && maxZ <= request.MaxZ + .02 && maxY <= request.MaximumHeight + .02;
        report.AddValue("strictEnvelopeWithin02m", strict);
        report.AddValue("declaredMinX", request.MinX); report.AddValue("declaredMaxX", request.MaxX); report.AddValue("declaredMinZ", request.MinZ); report.AddValue("declaredMaxZ", request.MaxZ); report.AddValue("declaredMaxY", request.MaximumHeight); report.AddValue("accessClearance", request.ClearanceMetres);
        int supportHits = 0; double minimumActualSurface = double.PositiveInfinity;
        var frame = Quaternion.Inverse(inverse);
        foreach (var part in vessel.parts) foreach (var collider in part.GetComponentsInChildren<Collider>())
        {
            if (!collider.enabled || collider.isTrigger || collider.gameObject.layer == 21) continue;
            var bounds = collider.bounds; double lowX=double.PositiveInfinity, highX=double.NegativeInfinity, lowZ=double.PositiveInfinity, highZ=double.NegativeInfinity;
            for (int x=-1;x<=1;x+=2) for (int y=-1;y<=1;y+=2) for (int z=-1;z<=1;z+=2)
            { var point=inverse*(bounds.center+Vector3.Scale(bounds.extents,new Vector3(x,y,z))-terrain.point);lowX=Math.Min(lowX,point.x);highX=Math.Max(highX,point.x);lowZ=Math.Min(lowZ,point.z);highZ=Math.Max(highZ,point.z); }
            var row=report.AddNode("COLLIDER_SURFACE_PROBE");row.AddValue("partPersistentId",part.persistentId);row.AddValue("colliderType",collider.GetType().Name);row.AddValue("colliderName",collider.name);row.AddValue("layer",collider.gameObject.layer);
            int hits=0;double lowest=double.PositiveInfinity;
            foreach(float fx in new[]{.125f,.5f,.875f}) foreach(float fz in new[]{.125f,.5f,.875f})
            {
                var point=terrain.point+frame*new Vector3((float)(lowX+(highX-lowX)*fx),0,(float)(lowZ+(highZ-lowZ)*fz)); RaycastHit actualTerrain,actualSurface;
                if(!Physics.Raycast(point+terrain.normal*50,-terrain.normal,out actualTerrain,100,1<<15,QueryTriggerInteraction.Ignore))continue;
                if(!collider.Raycast(new Ray(actualTerrain.point-terrain.normal*50,terrain.normal),out actualSurface,100))continue;
                double height=Vector3.Dot(actualSurface.point-actualTerrain.point,terrain.normal);lowest=Math.Min(lowest,height);minimumActualSurface=Math.Min(minimumActualSurface,height);hits++;supportHits++;
            }
            row.AddValue("nativeRayHits",hits);if(hits>0)row.AddValue("lowestActualSurfaceHeight",lowest.ToString("R",CultureInfo.InvariantCulture));
        }
        report.AddValue("nativeSurfaceProbeHits",supportHits);if(supportHits>0)report.AddValue("minimumSampledActualSurfaceHeight",minimumActualSurface.ToString("R",CultureInfo.InvariantCulture));
        report.AddValue("surfaceProbeMethod","Direct native Collider.Raycast upward from 50m below actual terrain at nine interior footprint locations per enabled non-trigger collider; bounded sampled diagnostic, not complete contact certification");
        string path = Path.Combine(root, "colony-placement-envelope-" + operation + ".cfg"); CheckPath(path); File.WriteAllText(path, report.ToString());
        Line("DEPLOYED_ENVELOPE_WITNESS path=" + Path.GetFileName(path) + "; x=" + minX + ":" + maxX + "; z=" + minZ + ":" + maxZ + "; y=" + minY + ":" + maxY);
        Line("ACTUAL_COLLIDER_SURFACE_PROBE hits="+supportHits+"; minimumSampledSurfaceHeight="+minimumActualSurface+"; sampled evidence does not prove complete ground contact");
        if (requireStrictEnvelope) Check(strict, "Actual deployed physical geometry exceeds declared package envelope (strict 0.02 m allowance)");
    }

    private void ReadColdReloadSource()
    {
        string path = Path.Combine(root, "saves", expectedFolder, reloadSourceName + ".sfs"); CheckPath(path);
        Check(File.Exists(path) && new FileInfo(path).Length > 0 && new FileInfo(path).Length <= 64L * 1024 * 1024, "Native reload source absent or exceeds 64 MiB bound");
        byte[] bytes = File.ReadAllBytes(path);
        Check(reloadSourceHash != null && reloadSourceHash.Length == 64 && reloadSourceHash.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f') && Sha(bytes) == reloadSourceHash,
            "Native source save bytes differ from the independently authorized SHA-256");
        var tree = ConfigNode.Parse(Encoding.UTF8.GetString(bytes));
        Check(tree != null && tree.GetNodes("GAME").Length == 1, "Native source must contain exactly one GAME");
        var game = tree.GetNode("GAME");
        var scenarios = game.GetNodes("SCENARIO").Where(n => n.GetValue("name") == "ColonyPlacementScenario").ToArray();
        Check(scenarios.Length == 1, "Native source placement scenario missing/duplicated");
        var operations = scenarios[0].GetNodes("PLACEMENT").Where(n => n.GetNode("REQUEST") != null && n.GetNode("REQUEST").GetValue("OperationId") == operation).ToArray();
        Check(operations.Length == 1, "Native source committed operation missing/duplicated");
        var codec = typeof(ColonyPlacementScenario).Assembly.GetType("Expanse.WorldBridge.ColonyPlacementCodec");
        reloadRequest = (ColonyPlacementRequest)codec.GetMethod("ReadRequest", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { operations[0].GetNode("REQUEST") });
        reloadStatus = (ColonyPlacementStatus)codec.GetMethod("ReadStatus", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { operations[0].GetNode("WITNESS") });
        expectedFingerprint = reloadRequest.Fingerprint(); requestHash = reloadSourceHash;
        Check(reloadRequest.Validate() == null && reloadRequest.WorldId == scenarios[0].GetValue("worldId") && reloadStatus.OperationId == operation && reloadStatus.RequestFingerprint == expectedFingerprint &&
            reloadStatus.Stage == (recoverExisting ? ColonyPlacementStage.RecoveryHold : ColonyPlacementStage.Anchored) && reloadStatus.AssemblyAttempted && reloadStatus.IncludedInSaveSerialization && reloadStatus.SaveGeneration > 0,
            "Native source lacks the original exact anchored saved receipt");
        var vessels = game.GetNode("FLIGHTSTATE").GetNodes("VESSEL").Where(v => v.GetNodes("PART").Any(p => p.GetNodes("MODULE").Any(m => m.GetValue("name") == "ColonyPlacementMarker" && m.GetValue("operationId") == operation))).ToArray();
        Check(vessels.Length == 1 && Guid.Parse(vessels[0].GetValue("pid")).ToString("D") == reloadStatus.VesselId && uint.Parse(vessels[0].GetValue("persistentId"), CultureInfo.InvariantCulture) == reloadStatus.VesselPersistentId,
            "Native source building identity missing/duplicated or disagrees with saved receipt");
        var parts = vessels[0].GetNodes("PART");
        Check(parts.Length == reloadStatus.PartCount && parts.Select(p => uint.Parse(p.GetValue("persistentId"), CultureInfo.InvariantCulture)).OrderBy(id => id).SequenceEqual(reloadStatus.PartPersistentIds.OrderBy(id => id)) &&
            parts.Select(p => uint.Parse(p.GetValue("uid"), CultureInfo.InvariantCulture)).OrderBy(id => id).SequenceEqual(reloadStatus.FlightIds.OrderBy(id => id)), "Native source part/flight membership changed");
        foreach (var part in parts)
        {
            var markers = part.GetNodes("MODULE").Where(m => m.GetValue("name") == "ColonyPlacementMarker").ToArray();
            Check(markers.Length == 1 && markers[0].GetValue("operationId") == operation && markers[0].GetValue("worldId") == reloadRequest.WorldId && markers[0].GetValue("colonyId") == reloadRequest.ColonyId &&
                markers[0].GetValue("plotId") == reloadRequest.PlotId && markers[0].GetValue("requestFingerprint") == expectedFingerprint && markers[0].GetValue("templateSha256") == reloadRequest.TemplateSha256,
                "Native source marker has different immutable birth terms");
            reloadCraftPartIds.Add(uint.Parse(markers[0].GetValue("craftPartId"), CultureInfo.InvariantCulture), uint.Parse(part.GetValue("persistentId"), CultureInfo.InvariantCulture));
        }
        if (!recoverExisting)
        {
            var anchors = game.GetNodes("SCENARIO").Where(n => n.GetValue("name") == "FoundationRegistry").SelectMany(n => n.GetNodes("ANCHOR")).Where(n => n.GetValue("id") == reloadStatus.FoundationId).ToArray();
            Check(anchors.Length == 1 && anchors[0].GetNodes("MEMBER").Select(n => uint.Parse(n.GetValue("id"), CultureInfo.InvariantCulture)).OrderBy(id => id).SequenceEqual(reloadStatus.PartPersistentIds.OrderBy(id => id)), "Native source Foundation anchor membership disagrees");
        }
        else
        {
            Check(string.IsNullOrEmpty(reloadStatus.FoundationId), "This bounded recovery only resumes an existing assembly without an original anchor");
            Check(!game.GetNodes("SCENARIO").Where(n=>n.GetValue("name")=="FoundationRegistry").SelectMany(n=>n.GetNodes("ANCHOR")).SelectMany(n=>n.GetNodes("MEMBER")).Any(n=>reloadStatus.PartPersistentIds.Contains(uint.Parse(n.GetValue("id"),CultureInfo.InvariantCulture))),"Native source has an ambiguous physical anchor despite empty receipt; this recovery mode cannot resume it");
        }
    }

    private void CheckRecoveryBirth(ColonyPlacementStatus current,Vessel vessel)
    {
        Check(CountAllMarked(operation)==1&&current.VesselId==reloadStatus.VesselId&&current.VesselPersistentId==reloadStatus.VesselPersistentId&&current.PartPersistentIds.OrderBy(x=>x).SequenceEqual(reloadStatus.PartPersistentIds.OrderBy(x=>x))&&current.FlightIds.OrderBy(x=>x).SequenceEqual(reloadStatus.FlightIds.OrderBy(x=>x)),"Recovery changed/duplicated original receipt identities");
        Check(vessel.id.ToString("D")==reloadStatus.VesselId&&vessel.persistentId==reloadStatus.VesselPersistentId&&vessel.parts.Select(p=>p.persistentId).OrderBy(x=>x).SequenceEqual(reloadStatus.PartPersistentIds.OrderBy(x=>x))&&vessel.parts.Select(p=>p.flightID).OrderBy(x=>x).SequenceEqual(reloadStatus.FlightIds.OrderBy(x=>x)),"Recovery changed actual original native vessel membership");
        foreach(var part in vessel.parts){var marker=part.Modules.OfType<ColonyPlacementMarker>().Single();uint original;Check(reloadCraftPartIds.TryGetValue(marker.craftPartId,out original)&&original==part.persistentId&&marker.operationId==operation&&marker.worldId==reloadRequest.WorldId&&marker.requestFingerprint==expectedFingerprint&&marker.templateSha256==reloadRequest.TemplateSha256,"Recovery lost exact original craft-to-part birth mapping");}
    }

    private void CheckColdReload(ColonyPlacementStatus current, Vessel vessel)
    {
        Check(CountAllMarked(operation) == 1, "Native reload has duplicate operation across loaded/proto vessels");
        Check(current.VesselId == reloadStatus.VesselId && current.VesselPersistentId == reloadStatus.VesselPersistentId && current.FoundationId == reloadStatus.FoundationId && current.RequestFingerprint == expectedFingerprint &&
            current.PartPersistentIds.OrderBy(id => id).SequenceEqual(reloadStatus.PartPersistentIds.OrderBy(id => id)) && current.FlightIds.OrderBy(id => id).SequenceEqual(reloadStatus.FlightIds.OrderBy(id => id)),
            "Cold reload changed the original receipt's vessel/part/flight/Foundation identities");
        Check(vessel.id.ToString("D") == reloadStatus.VesselId && vessel.persistentId == reloadStatus.VesselPersistentId && vessel.parts.Select(p => p.persistentId).OrderBy(id => id).SequenceEqual(reloadStatus.PartPersistentIds.OrderBy(id => id)) &&
            vessel.parts.Select(p => p.flightID).OrderBy(id => id).SequenceEqual(reloadStatus.FlightIds.OrderBy(id => id)), "Cold loaded building differs from original actual native membership");
        foreach (var part in vessel.parts)
        {
            var markers = part.Modules.OfType<ColonyPlacementMarker>().ToArray(); uint original;
            Check(markers.Length == 1 && reloadCraftPartIds.TryGetValue(markers[0].craftPartId, out original) && original == part.persistentId && markers[0].operationId == operation && markers[0].worldId == reloadRequest.WorldId &&
                markers[0].colonyId == reloadRequest.ColonyId && markers[0].plotId == reloadRequest.PlotId && markers[0].requestFingerprint == expectedFingerprint && markers[0].templateSha256 == reloadRequest.TemplateSha256,
                "Cold reload lost exact craft-to-persistent marker identity");
        }
        var foundations = typeof(ColonyPlacementScenario).Assembly.GetType("Expanse.WorldBridge.ColonyPlacementFoundations");
        var witness = new object[] { vessel, null, 0.0, 0.0 };
        Check((bool)foundations.GetMethod("IsHeld", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { vessel }) &&
            (bool)foundations.GetMethod("ReadWitness", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, witness) && (string)witness[1] == reloadStatus.FoundationId && (double)witness[2] <= .01 && (double)witness[3] <= .01,
            "Cold native reload did not restore the same physical Foundation hold/pose");
        Line("PASS cold native original identities and Foundation pose read back; vessel=" + vessel.id + " source=" + reloadSourceName);
    }

    private static bool FullyLoadedMarked(string id)
    { var vessels = Marked(id); return vessels.Length == 1 && vessels[0].parts.Count > 0 && vessels[0].parts.All(p => p.started); }

    private static int CountAllMarked(string id)
    {
        if (FlightGlobals.Vessels == null || FlightGlobals.Vessels.Count > 4096) throw new InvalidOperationException("Bounded vessel inventory absent");
        return FlightGlobals.Vessels.Count(v => v != null && (v.loaded && v.parts != null ? v.parts.Any(p => p.Modules.OfType<ColonyPlacementMarker>().Any(m => m.operationId == id)) :
            v.protoVessel != null && v.protoVessel.protoPartSnapshots.Any(p => p.modules.Any(m => m.moduleName == "ColonyPlacementMarker" && m.moduleValues.GetValue("operationId") == id))));
    }

    private static Vessel[] Marked(string operationId)
    { return FlightGlobals.Vessels.Where(v => v != null && v.loaded && v.parts != null && v.parts.Any(p => p.Modules.OfType<ColonyPlacementMarker>().Any(m => m.operationId == operationId))).ToArray(); }
    private static bool SafeName(string value) { return !string.IsNullOrEmpty(value) && value.Length <= 100 && value.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_'); }
    private void ControlChanged(Vessel vessel) { if (reference != null && vessel != reference) controlChanged = true; }
    private void ClearHandlers() { GameEvents.onVesselChange.Remove(ControlChanged); ClearInjector(); if (contactDiagnostic != null) { contactDiagnostic.Dispose(); contactDiagnostic = null; } }
    private void OnDestroy() { ClearHandlers(); }
    private void CheckDevelopmentPipe(string marker, string prefix)
    {
        var args = Environment.GetCommandLineArgs().Where(x => x.StartsWith(marker, StringComparison.OrdinalIgnoreCase)).ToArray();
        string required = prefix + Environment.UserName + ".";
        Check(args.Length == 1 && args[0].Substring(marker.Length).StartsWith(required, StringComparison.Ordinal) && SafeName(args[0].Substring(marker.Length + required.Length)), "Missing unique current-user development IPC namespace " + marker);
    }
    private void CheckPath(string path)
    {
        string value = Path.GetFullPath(path);
        Check(value.StartsWith(ApprovedRoot + "\\", StringComparison.OrdinalIgnoreCase), "Path leaves approved isolated installation");
        for (string current = value; current != null; current = Path.GetDirectoryName(current))
            Check(!(File.Exists(current) || Directory.Exists(current)) || (File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0, "Reparse path refused");
    }
    private void Snapshot(ColonyPlacementStatus status)
    {
        var node = new ConfigNode("COLONY_PLACEMENT_DEV_STATUS");
        node.AddValue("authorization", "adapter-probe-only-no-economic-certification"); node.AddValue("saveFolder", expectedFolder);
        node.AddValue("requestSha256", requestHash ?? "product-created"); node.AddValue("process", System.Diagnostics.Process.GetCurrentProcess().Id);
        var codec = typeof(ColonyPlacementScenario).Assembly.GetType("Expanse.WorldBridge.ColonyPlacementCodec");
        node.AddNode((ConfigNode)codec.GetMethod("WriteStatus", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { status }));
        string target = Path.Combine(root, "colony-placement-status-" + operation + ".cfg"), temp = target + ".tmp";
        CheckPath(target); CheckPath(temp); File.WriteAllText(temp, node.ToString());
        if (File.Exists(target)) File.Replace(temp, target, null); else File.Move(temp, target);
    }
    private void DumpLoadedPartDatabase()
    {
        string folder = Path.Combine(root, "GameData", "ExpanseWorldBridge", "Templates"); CheckPath(folder);
        var paths = Directory.GetFiles(folder, "*.manifest.json"); Check(paths.Length <= 128, "Oversized installed manifest list for diagnostics");
        var names = paths.SelectMany(path => { CheckPath(path); Check(new FileInfo(path).Length <= 1048576, "Oversized diagnostic manifest"); return ColonyTemplateCatalog.ReadManifest(File.ReadAllBytes(path)).RequiredPartNames; }).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Check(names.Length <= 256, "Too many selected installed part diagnostics");
        var report = new ConfigNode("COLONY_LOADED_PART_DATABASE_WITNESS");
        report.AddValue("operationId", operation); report.AddValue("saveFolder", expectedFolder); report.AddValue("observedUt", Planetarium.GetUniversalTime().ToString("R", CultureInfo.InvariantCulture));
        var canonical = typeof(ColonyTemplateCatalog).GetMethod("ConfigTerms", BindingFlags.Static | BindingFlags.NonPublic);
        foreach (var config in GameDatabase.Instance.GetConfigNodes("KISConfig"))
        {
            var row = report.AddNode("COMPILER_CONFIGURATION"); row.AddValue("name", "KISConfig");
            string terms = (string)canonical.Invoke(null, new object[] { config, 0 });
            row.AddValue("canonicalTermsBase64", Convert.ToBase64String(Encoding.UTF8.GetBytes(terms))); row.AddValue("canonicalTermsSha256", Sha(Encoding.UTF8.GetBytes(terms)));
        }
        foreach (var name in names)
        {
            var node = report.AddNode("AVAILABLE_PART"); node.AddValue("requestedName", name);
            var info = PartLoader.getPartInfoByName(name.Replace('_', '.'));
            if (info == null) { node.AddValue("reason", "Current AvailablePart missing"); continue; }
            node.AddValue("loadedName", info.name); node.AddValue("prefabName", info.partPrefab == null ? "<missing>" : info.partPrefab.name);
            node.AddValue("configName", info.partConfig == null ? "<missing>" : info.partConfig.GetValue("name") ?? "<removed>");
            node.AddValue("techRequired", info.TechRequired); node.AddValue("cost", info.cost.ToString("R", CultureInfo.InvariantCulture));
            node.AddValue("rawEntryCost", AvailablePart._GetEntryCost(info)); node.AddValue("entryCost", info.entryCost);
            if (info.partConfig != null)
            {
                node.AddNode(info.partConfig.CreateCopy());
                // Native text output contains literal translated newlines, so
                // retain a reversible canonical witness alongside readable CFG.
                string terms = (string)canonical.Invoke(null, new object[] { info.partConfig, 0 });
                node.AddValue("canonicalTermsBase64", Convert.ToBase64String(Encoding.UTF8.GetBytes(terms)));
                node.AddValue("canonicalTermsSha256", Sha(Encoding.UTF8.GetBytes(terms)));
            }
            if (info.partPrefab == null) continue;
            node.AddValue("prefabDryMass", info.partPrefab.mass.ToString("R", CultureInfo.InvariantCulture)); node.AddValue("prefabCrewCapacity", info.partPrefab.CrewCapacity);
            node.AddValue("modules", string.Join(",", info.partPrefab.Modules.Cast<PartModule>().Select(m => m.moduleName)));
            foreach (var resource in info.partPrefab.Resources)
            {
                var row = node.AddNode("PREFAB_TANK"); row.AddValue("name", resource.resourceName); row.AddValue("amount", resource.amount.ToString("R", CultureInfo.InvariantCulture)); row.AddValue("capacity", resource.maxAmount.ToString("R", CultureInfo.InvariantCulture));
                if (resource.info != null) { row.AddValue("density", resource.info.density.ToString("R", CultureInfo.InvariantCulture)); row.AddValue("unitCost", resource.info.unitCost.ToString("R", CultureInfo.InvariantCulture)); row.AddValue("flowMode", resource.info.resourceFlowMode); }
            }
        }
        string pathOut = Path.Combine(root, "colony-placement-loaded-parts-" + operation + ".cfg"); CheckPath(pathOut); File.WriteAllText(pathOut, report.ToString());
        Line("PART_DATABASE_WITNESS parts=" + names.Length + " path=" + Path.GetFileName(pathOut));
    }
    private static string Sha(byte[] bytes) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
    private void Check(bool success, string message) { checks++; if (!success) throw new InvalidOperationException(message); }
    private void Line(string message) { File.AppendAllText(log, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " " + message + Environment.NewLine); Debug.Log("[ColonyPlacementWitness] " + message); }
    private static void ClearInjector() { typeof(ColonyPlacementRuntime).GetField("FailureInjector", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, null); }
}
