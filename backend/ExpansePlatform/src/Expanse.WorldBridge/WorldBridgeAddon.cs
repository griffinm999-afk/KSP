using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using UnityEngine;

namespace Expanse.WorldBridge
{
    internal sealed class ClockSample
    {
        public long Sequence;
        public string WorldId;
        public string RunId;
        public string SessionId;
        public string LoadEpoch;
        public string InstallNamespace;
        public string SaveFolder;
        public string SaveTitle;
        public double? UtSeconds;
        public bool ActiveWorld;
        public string Scene;
        public bool? Paused;
        public string FormattedDate;
        public DepotView Depot;
        public DepotSummary[] Depots;
        public ColonySnapshot Colony;
        internal ColonyCapture Capture;
        internal long ContextGeneration;
    }

    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public sealed partial class WorldBridgeAddon : MonoBehaviour
    {
        public static WorldBridgeAddon Current { get; private set; }
        private const string PipePrefix = "ExpanseFoundations.Clock.Publisher.v1.";
        private const double SampleIntervalSeconds = 0.5;
        private readonly string sessionId = Guid.NewGuid().ToString("D");
        private string loadEpoch = Guid.NewGuid().ToString("D");
        // Transient writable-execution fence; never persisted in KSP save data.
        private string runId = Guid.NewGuid().ToString("D");
        private string installNamespace;
        private long sequence;
        private double lastSampleAt;
        private double? previousUt;
        private bool loadUnresolved;
        private bool loadSceneReadyObserved;
        private bool loadPostLoadObserved;
        private string readinessCandidate;
        private object readinessGame;
        private int readinessStableSamples;
        private object observedGame;
        private object loadPreviousGame;
        private bool loadEpochChangePending;
        private bool? paused;
        private ClockSample latest;
        private AutoResetEvent wakeWorker;
        private Thread worker;
        private volatile bool stopping;
        private NamedPipeClientStream activePipe;
        private Stopwatch clock;
        private long completedSampleCount;
        private long totalSampleElapsedStopwatchTicks;
        private long maxSampleElapsedStopwatchTicks;
        private string workerDiagnosticLogPath;
        private string lastWorkerDiagnostic;
        private string pendingWorkerLog;
        private string pendingTimingLog;
        private string pendingLogFailure;
        private long nextLogFailureReport;
        private string lastRecoveryInitializationReason;
        private ColonyCapture lastColonyCapture;
        private double lastColonySampleAt = -100;
        private string lastColonyContext;
        // Stable main-thread publication fence; the clock worker drains `latest` to null.
        private string lastPublishedClockSessionId;
        private string lastPublishedClockLoadEpoch;
        private int bridgeProcessId;
        private long bridgeProcessStartUtcTicks;
        private string bridgeExecutablePath;
        private string bridgeProcessIdentityError;
        private static bool devRecoveryFixtureEnabled;
        private static string devRecoveryFixtureToken;
        private static bool devPhysicalFixtureEnabled;
        private static string devPhysicalFixtureToken;
        private static bool devRemotePhysicalFixtureEnabled;
        private static string devRemotePhysicalFixtureToken;

        private static bool IsApprovedDevelopmentRoot()
        {
            try
            {
                string actual = Path.GetFullPath(KSPUtil.ApplicationRootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!string.Equals(actual, @"C:\Users\griff\Documents\KSP-RMM-Dev", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(actual, @"C:\Users\griff\Documents\KSP-RMM-Dev-Full", StringComparison.OrdinalIgnoreCase)) return false;
                string current = actual;
                while (!String.IsNullOrEmpty(current))
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
                    string parent = Path.GetDirectoryName(current);
                    if (parent == current) break;
                    current = parent;
                }
                return true;
            }
            catch { return false; }
        }

        public static bool ActivateRecoveryFixture(string oneShotToken)
        {
            Guid parsed;
            if (!Guid.TryParseExact(oneShotToken, "N", out parsed) || parsed == Guid.Empty) return false;
            if (!IsApprovedDevelopmentRoot()) return false;
            if (devRecoveryFixtureEnabled) return string.Equals(devRecoveryFixtureToken, oneShotToken, StringComparison.OrdinalIgnoreCase);
            devRecoveryFixtureToken = oneShotToken;
            devRecoveryFixtureEnabled = true;
            WorldBridgeAddon active = FindObjectOfType<WorldBridgeAddon>();
            if (active != null) active.QueueWorkerLog("Explicit one-shot recovery fixture enabled token=" + oneShotToken.Substring(0, 8));
            return true;
        }

        public static bool ActivatePhysicalFixture(string oneShotToken)
        {
            Guid parsed;
            if (!Guid.TryParseExact(oneShotToken, "N", out parsed) || parsed == Guid.Empty) return false;
            if (!IsApprovedDevelopmentRoot()) return false;
            if (devPhysicalFixtureEnabled) return string.Equals(devPhysicalFixtureToken, oneShotToken, StringComparison.OrdinalIgnoreCase);
            devPhysicalFixtureToken = oneShotToken;
            devPhysicalFixtureEnabled = true;
            WorldBridgeAddon active = FindObjectOfType<WorldBridgeAddon>();
            if (active != null) active.QueueWorkerLog("Explicit one-shot physical candidate enabled token=" + oneShotToken.Substring(0, 8));
            return true;
        }

        internal static bool PhysicalCandidateEnabled { get { return devPhysicalFixtureEnabled; } }
        public static bool ActivateRemotePhysicalFixture(string oneShotToken)
        {
            Guid parsed;
            if (!Guid.TryParseExact(oneShotToken, "N", out parsed) || parsed == Guid.Empty) return false;
            if (!IsApprovedDevelopmentRoot()) return false;
            if (devRemotePhysicalFixtureEnabled) return string.Equals(devRemotePhysicalFixtureToken, oneShotToken, StringComparison.OrdinalIgnoreCase);
            devRemotePhysicalFixtureToken = oneShotToken;
            devRemotePhysicalFixtureEnabled = true;
            WorldBridgeAddon active = FindObjectOfType<WorldBridgeAddon>();
            if (active != null) active.QueueWorkerLog("Explicit one-shot remote BRP candidate enabled token=" + oneShotToken.Substring(0, 8));
            return true;
        }

        // The production path is available only with the exact BRP provider in
        // a loaded game. Per-depot Describe and effect preflight still require
        // exact selected unloaded-vessel ownership and current stock/capacity.
        internal static bool RemotePhysicalCandidateEnabled { get { return RemoteBrpInventoryGateway.SupportedProviderAvailable; } }
        internal static bool RemotePhysicalDiagnosticsEnabled { get { return devRemotePhysicalFixtureEnabled; } }

        public string CurrentRunId { get { return runId; } }
        public string CurrentLoadEpoch { get { return loadEpoch; } }
        public string CurrentSessionId { get { return sessionId; } }
        public bool IsLoadUnresolved { get { return loadUnresolved; } }
        public bool CanAcceptEffects
        {
            get { return devRecoveryFixtureEnabled && CanAcceptRegistryEffects; }
        }

        public bool CanAcceptPhysicalEffects
        {
            get { return (devPhysicalFixtureEnabled || RemotePhysicalCandidateEnabled) && CanAcceptRegistryEffects; }
        }

        internal bool CanAcceptRegistryEffects
        {
            get
            {
                if (loadUnresolved || !HighLogic.LoadedSceneIsGame || HighLogic.CurrentGame == null || !string.IsNullOrEmpty(bridgeProcessIdentityError)) return false;
                DepotRegistryModule registry = DepotRegistryModule.Instance;
                RecoveryCapsuleModule capsule = RecoveryCapsuleModule.Instance;
                return registry != null && registry.IsReady && !registry.IsCorrupt && capsule != null && capsule.IsLoaded && !capsule.IsCorrupt &&
                    !string.IsNullOrEmpty(registry.WorldId) && string.Equals(registry.WorldId, capsule.WorldId, StringComparison.OrdinalIgnoreCase) && capsule.GetAcceptedStateBytes() != null;
            }
        }

        // Read by the disposable dev harness; no per-sample logging or file I/O is performed.
        public long CompletedSampleCount { get { return Interlocked.Read(ref completedSampleCount); } }
        public long TotalSampleElapsedStopwatchTicks { get { return Interlocked.Read(ref totalSampleElapsedStopwatchTicks); } }
        public long MaxSampleElapsedStopwatchTicks { get { return Interlocked.Read(ref maxSampleElapsedStopwatchTicks); } }
        public long SampleStopwatchFrequency { get { return Stopwatch.Frequency; } }
        public string WorkerDiagnosticLogPath { get { return Interlocked.CompareExchange(ref workerDiagnosticLogPath, null, null); } }
        public string LastWorkerDiagnostic { get { return Interlocked.CompareExchange(ref lastWorkerDiagnostic, null, null); } }

        private void Awake()
        {
            Current = this;
            DontDestroyOnLoad(gameObject);
            clock = Stopwatch.StartNew();
            try { installNamespace = Path.GetFullPath(KSPUtil.ApplicationRootPath); }
            catch { installNamespace = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory); }
            wakeWorker = new AutoResetEvent(false);
            installNamespace = SafeInstallNamespace(installNamespace);
            CaptureBridgeProcessIdentity();
            InstallPowerObserver();
            InstallProductionModeObserver();
            GameEvents.onGameStateLoad.Add(OnGameStateLoad);
            GameEvents.onGameStatePostLoad.Add(OnGameStatePostLoad);
            GameEvents.onLevelWasLoadedGUIReady.Add(OnLevelWasLoadedGuiReady);
            GameEvents.onFlightReady.Add(OnFlightReady);
            GameEvents.OnRevertToLaunchFlightState.Add(OnRevert);
            GameEvents.OnRevertToPrelaunchFlightState.Add(OnRevert);
            GameEvents.onGamePause.Add(OnPause);
            GameEvents.onGameUnpause.Add(OnUnpause);
            GameEvents.onGameStateLoad.Add(OnDepotLoadBegin);
            worker = new Thread(PipeWorker) { IsBackground = true, Name = "Expanse clock publisher" };
            worker.Start();
            StartEffectsWorker();
            StartWolfWorker();
        }

        private void CaptureBridgeProcessIdentity()
        {
            try
            {
                using (Process process = Process.GetCurrentProcess())
                {
                    int pid = process.Id;
                    long startTicks = process.StartTime.ToUniversalTime().Ticks;
                    string executable = process.MainModule == null ? null : process.MainModule.FileName;
                    if (string.IsNullOrWhiteSpace(executable)) throw new InvalidOperationException("KSP executable path is unavailable");
                    executable = Path.GetFullPath(executable);
                    if (!string.Equals(Path.GetFileName(executable), "KSP_x64.exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Current process is not KSP_x64.exe");
                    string root = Path.GetFullPath(installNamespace).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    string prefix = root + Path.DirectorySeparatorChar;
                    if (!executable.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("KSP executable is outside its reported installation namespace");
                    if (pid <= 0 || startTicks <= 0) throw new InvalidOperationException("KSP process identity values are invalid");
                    bridgeProcessId = pid;
                    bridgeProcessStartUtcTicks = startTicks;
                    bridgeExecutablePath = executable;
                    bridgeProcessIdentityError = null;
                }
            }
            catch (Exception ex)
            {
                bridgeProcessId = 0;
                bridgeProcessStartUtcTicks = 0;
                bridgeExecutablePath = string.Empty;
                bridgeProcessIdentityError = "KSP process identity is unavailable: " + ex.GetType().Name + ": " + ex.Message;
                QueueWorkerLog(bridgeProcessIdentityError);
            }
        }

        private void OnGameStateLoad(ConfigNode ignored)
        {
            BeginUnresolvedLoad(false, "game-state load", false);
        }

        private void OnGameStatePostLoad(ConfigNode ignored)
        {
            if (!loadUnresolved) return;
            loadPostLoadObserved = true;
            ResetReadinessStability();
            QueueWorkerLog("Post-load readiness event observed for epoch=" + loadEpoch);
        }

        private void OnRevert(FlightState ignored)
        {
            BeginUnresolvedLoad(true, "revert event", true);
        }

        private void OnLevelWasLoadedGuiReady(GameScenes scene)
        {
            string loadedScene = scene.ToString();
            if (!loadUnresolved || IsNoWorldScene(loadedScene)) return;
            loadSceneReadyObserved = true;
            ResetReadinessStability();
            QueueWorkerLog("Level-loaded GUI-ready event observed scene=" + loadedScene + " epoch=" + loadEpoch);
        }

        private void OnFlightReady()
        {
            if (!loadUnresolved || !HighLogic.LoadedSceneIsFlight) return;
            loadSceneReadyObserved = true;
            ResetReadinessStability();
            QueueWorkerLog("Flight-ready event observed epoch=" + loadEpoch);
        }

        private void BeginUnresolvedLoad(bool sceneReadyObserved, string source, bool forceNewEpoch)
        {
            InvalidatePhysicalGatewayLineage();
            if (forceNewEpoch && loadUnresolved)
            {
                runId = Guid.NewGuid().ToString("D");
                StartNewEpoch();
                loadEpochChangePending = true;
                QueueWorkerLog("Forced new run fence source=" + source + " epoch=" + loadEpoch);
            }
            if (!loadUnresolved)
            {
                loadUnresolved = true;
                runId = Guid.NewGuid().ToString("D");
                loadPreviousGame = observedGame;
                loadEpochChangePending = forceNewEpoch || observedGame == null;
                if (loadEpochChangePending) StartNewEpoch();
                paused = null;
                loadSceneReadyObserved = false;
                loadPostLoadObserved = false;
                ResetReadinessStability();
                QueueWorkerLog("Load transition started source=" + source + " epoch=" + loadEpoch);
            }
            if (sceneReadyObserved) loadSceneReadyObserved = true;
            previousUt = null;
            ResetReadinessStability();
            PublishInactive(CurrentScene());
        }

        private void OnPause() { paused = true; MarkPowerWindowInterrupted(); }
        private void OnUnpause() { paused = false; MarkPowerWindowInterrupted(); }

        private void Update()
        {
            // Unity logging stays on its main thread; the worker hands off at
            // most one timing summary and one rate-limited file failure.
            string timing = Interlocked.Exchange(ref pendingTimingLog, null);
            if (timing != null) UnityEngine.Debug.Log("[Expanse.WorldBridge] " + timing);
            string logFailure = Interlocked.Exchange(ref pendingLogFailure, null);
            if (logFailure != null) UnityEngine.Debug.LogWarning("[Expanse.WorldBridge] " + logFailure);
            CheckPowerWindowConditions();
            MaintainPhysicalGatewayLifecycle();
            ProcessPendingEffectProposal();
            TickLegacyLogisticsRuntime();
            ProcessPendingWolfChange();
            double now = clock.Elapsed.TotalSeconds;
            if (now - lastSampleAt < SampleIntervalSeconds) return;
            lastSampleAt = now;
            long started = Stopwatch.GetTimestamp();
            try { Sample(CurrentScene()); }
            catch
            {
                // A transient KSP load boundary is represented as inactive until APIs are ready again.
                PublishInactive(CurrentScene());
            }
            finally { RecordSampleTiming(Stopwatch.GetTimestamp() - started); }
        }

        private void RecordSampleTiming(long elapsedTicks)
        {
            sampleTiming.Record(elapsedTicks);
            if (elapsedTicks < 0) elapsedTicks = 0;
            Interlocked.Increment(ref completedSampleCount);
            Interlocked.Add(ref totalSampleElapsedStopwatchTicks, elapsedTicks);
            long current;
            do
            {
                current = Interlocked.Read(ref maxSampleElapsedStopwatchTicks);
                if (elapsedTicks <= current) return;
            }
            while (Interlocked.CompareExchange(ref maxSampleElapsedStopwatchTicks, elapsedTicks, current) != current);
        }

        private void Sample(string scene)
        {
            double? ut = null;
            string folder = null;
            string title = null;
            string formattedDate = null;
            if (loadUnresolved)
            {
                bool ready = HighLogic.LoadedSceneIsGame && HighLogic.CurrentGame != null && !IsNoWorldScene(scene);
                if (ready)
                {
                    folder = HighLogic.SaveFolder;
                    title = HighLogic.CurrentGame.Title;
                    ready = !string.IsNullOrWhiteSpace(folder);
                }
                if (ready && (loadSceneReadyObserved || loadPostLoadObserved))
                {
                    string candidate = scene + "\n" + folder + "\n" + (title ?? string.Empty);
                    object currentGame = HighLogic.CurrentGame;
                    if (string.Equals(candidate, readinessCandidate, StringComparison.Ordinal) && object.ReferenceEquals(currentGame, readinessGame)) readinessStableSamples++;
                    else { readinessCandidate = candidate; readinessGame = currentGame; readinessStableSamples = 1; }
                    if (readinessStableSamples >= 2)
                    {
                        if (!loadEpochChangePending && loadPreviousGame != null && !object.ReferenceEquals(currentGame, loadPreviousGame))
                        {
                            StartNewEpoch();
                            loadEpochChangePending = true;
                            QueueWorkerLog("CurrentGame identity changed across load; rotated epoch=" + loadEpoch);
                        }
                        loadUnresolved = false;
                        previousUt = null;
                        paused = null;
                        QueueWorkerLog("Load transition resolved scene=" + scene + " saveFolder=" + folder + " epoch=" + loadEpoch);
                    }
                }
                else ResetReadinessStability();
            }

            bool world = !loadUnresolved && HighLogic.LoadedSceneIsGame && HighLogic.CurrentGame != null && !IsNoWorldScene(scene);
            if (world)
            {
                double current = Planetarium.GetUniversalTime();
                if (double.IsNaN(current) || double.IsInfinity(current)) world = false;
                else
                {
                    if (previousUt.HasValue && current < previousUt.Value)
                    {
                        StartNewEpoch();
                        previousUt = null;
                    }
                    previousUt = current;
                    ut = current;
                    try { formattedDate = KSPUtil.PrintDate(current, true, true); } catch { formattedDate = null; }
                    folder = folder ?? HighLogic.SaveFolder;
                    title = title ?? HighLogic.CurrentGame.Title;
                }
            }
            if (!world)
            {
                ResetPowerTelemetry();
                lastColonyCapture = null;
                lastColonyContext = null;
                previousUt = null;
                folder = null;
                title = null;
                ut = null;
                formattedDate = null;
            }
            ClockSample sample = new ClockSample
            {
                Sequence = Interlocked.Increment(ref sequence), SessionId = sessionId,
                LoadEpoch = loadEpoch, InstallNamespace = installNamespace,
                SaveFolder = folder, SaveTitle = title, UtSeconds = ut,
                ActiveWorld = world, Scene = scene, Paused = world ? ReadPaused() : paused, FormattedDate = formattedDate
            };
            DepotRegistryModule registry = DepotRegistryModule.Instance;
            if (world && registry != null && !string.IsNullOrWhiteSpace(registry.WorldId)) { sample.WorldId = registry.WorldId; sample.RunId = runId; }
            if (registry != null) registry.Tick(clock.Elapsed.TotalSeconds, sessionId, loadEpoch, world);
            RecoveryCapsuleModule recovery = RecoveryCapsuleModule.Instance;
            if (world && registry != null && registry.IsReady && !registry.IsCorrupt && recovery != null && recovery.IsLoaded && !recovery.IsCorrupt && !recovery.HasAcceptedState)
            {
                string initializationReason;
                if (!recovery.TryInitializeNewWorld(registry.WorldId, out initializationReason))
                {
                    if (!string.Equals(lastRecoveryInitializationReason, initializationReason, StringComparison.Ordinal))
                    {
                        lastRecoveryInitializationReason = initializationReason;
                        QueueWorkerLog("Initial recovery state remains unavailable: " + initializationReason);
                    }
                }
                else
                {
                    lastRecoveryInitializationReason = null;
                    QueueWorkerLog("Initialized empty accepted recovery state for registry world=" + registry.WorldId);
                }
            }
            sample.Depot = registry == null ? new DepotView { RegistryState = (loadUnresolved || world) ? "loading" : "noWorld", ObservationState = "none" } : registry.CloneView(world, loadUnresolved, clock.Elapsed.TotalSeconds);
            if (world && registry != null) sample.Depots = registry.CloneSummaries(world, loadUnresolved, clock.Elapsed.TotalSeconds);
            if (world && !loadUnresolved)
            {
                string censusEpochContext = sessionId + "/" + loadEpoch + "/" + (sample.WorldId ?? folder);
                string context = censusEpochContext + "/" + scene;
                if (lastColonyContext != context || clock.Elapsed.TotalSeconds - lastColonySampleAt >= 3)
                {
                    if (lastColonyContext != context) { ResetPowerTelemetry(); ResetProductionTelemetry(); }
                    lastColonySampleAt = clock.Elapsed.TotalSeconds;
                    lastColonyContext = context;
                    long captureStarted = Stopwatch.GetTimestamp();
                    try { lastColonyCapture = ObserveColony(ut, censusEpochContext, scene, sample.WorldId != null); }
                    catch
                    {
                        lastColonyCapture = new ColonyCapture(new ColonySnapshot { Status = "unavailable", Reason = "Colony observation failed; clock and deliveries remain available.", ObservedUt = ut }, censusEpochContext, scene, colonyAttempt, null, null);
                    }
                    finally { captureTiming.Record(Stopwatch.GetTimestamp() - captureStarted); }
                }
                sample.Capture = lastColonyCapture;
            }
            else { lastColonyCapture = null; lastColonyContext = null; }
            lastPublishedClockSessionId = sample.SessionId;
            lastPublishedClockLoadEpoch = sample.LoadEpoch;
            PublishLatestSample(sample);
            PublishEffectContext(world, scene, folder, ut);
            observedGame = HighLogic.CurrentGame;
            TryWakeWorker();
        }

        private void PublishInactive(string scene)
        {
            ClockSample sample = new ClockSample
            {
                Sequence = Interlocked.Increment(ref sequence), SessionId = sessionId,
                LoadEpoch = loadEpoch, InstallNamespace = installNamespace,
                ActiveWorld = false, Scene = scene, Paused = paused
            };
            DepotRegistryModule registry = DepotRegistryModule.Instance;
            sample.Depot = registry == null ? new DepotView { RegistryState = "noWorld", ObservationState = "none" } : registry.CloneView(false, loadUnresolved, clock.Elapsed.TotalSeconds);
            lastColonyCapture = null;
            lastColonyContext = null;
            ResetPowerTelemetry();
            lastPublishedClockSessionId = sample.SessionId;
            lastPublishedClockLoadEpoch = sample.LoadEpoch;
            PublishLatestSample(sample);
            PublishEffectContext(false, scene, null, null);
            observedGame = HighLogic.CurrentGame;
            TryWakeWorker();
        }

        private void TryWakeWorker()
        {
            // Signaling is only a hint; transport work stays on the background thread.
            try { if (wakeWorker != null) wakeWorker.Set(); } catch { }
        }

        private void ResetReadinessStability()
        {
            readinessCandidate = null;
            readinessGame = null;
            readinessStableSamples = 0;
        }

        private void QueueWorkerLog(string message)
        {
            Interlocked.Exchange(ref pendingWorkerLog, message);
            TryWakeWorker();
        }

        private bool? ReadPaused()
        {
            try { return FlightDriver.Pause || paused == true; }
            catch { return paused; }
        }

        private void StartNewEpoch()
        {
            ResetPowerTelemetry();
            loadEpoch = Guid.NewGuid().ToString("D");
            runId = Guid.NewGuid().ToString("D");
            previousUt = null;
        }

        private static string CurrentScene()
        {
            try { return HighLogic.LoadedScene.ToString(); }
            catch { return "Unknown"; }
        }

        private static bool IsNoWorldScene(string scene)
        {
            return string.Equals(scene, "MainMenu", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(scene, "Loading", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(scene, "None", StringComparison.OrdinalIgnoreCase);
        }

        private void PipeWorker()
        {
            try { PipeWorkerLoop(); }
            finally
            {
                try { if (activePipe != null) activePipe.Dispose(); } catch { }
                try { if (wakeWorker != null) wakeWorker.Dispose(); } catch { }
            }
        }

        private void PipeWorkerLoop()
        {
            string pipeName;
            try
            {
                string userName = Environment.UserName;
                if (string.IsNullOrWhiteSpace(userName))
                    throw new InvalidOperationException("Environment.UserName did not provide a pipe namespace.");
                pipeName = ResolvePublisherPipe(userName, Environment.GetCommandLineArgs(), out string rejected);
                if (rejected != null) WorkerLog("Rejected publisher endpoint argument: " + rejected);
                WorkerLog("Publisher pipe name=" + pipeName);
            }
            catch (Exception ex)
            {
                WorkerError("user namespace lookup", null, ex);
                return;
            }
            bool hasReportedPipeError = false;
            bool hasReportedPipeConnected = false;
            long lastPipeErrorAt = 0;
            string phase = "connect";
            while (!stopping)
            {
                DrainPendingWorkerLog();
                ReportPerformance();
                NamedPipeClientStream pipe = null;
                try
                {
                    phase = "connect";
                    pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.None);
                    activePipe = pipe;
                    pipe.Connect(800);
                    if (!hasReportedPipeConnected)
                    {
                        WorkerLog("Connected to publisher pipe name=" + pipeName);
                        hasReportedPipeConnected = true;
                    }
                    phase = "write";
                    while (!stopping && pipe.IsConnected)
                    {
                        DrainPendingWorkerLog();
                        ReportPerformance();
                        ClockSample sample = TakeLatestSample();
                        if (sample != null)
                        {
                            CompleteColonySample(sample);
                            if (sample.ContextGeneration == Interlocked.Read(ref clockContextGeneration))
                            {
                                long serializedAt = Stopwatch.GetTimestamp();
                                string json;
                                try { json = Json(sample); }
                                finally { serializationTiming.Record(Stopwatch.GetTimestamp() - serializedAt); }
                                if (sample.ContextGeneration == Interlocked.Read(ref clockContextGeneration))
                                {
                                    long sentAt = Stopwatch.GetTimestamp();
                                    try { WriteFrame(pipe, json); }
                                    finally { transportTiming.Record(Stopwatch.GetTimestamp() - sentAt); }
                                }
                            }
                        }
                        WaitForWorker(500);
                    }
                }
                catch (Exception ex)
                {
                    long now = Stopwatch.GetTimestamp();
                    if (!hasReportedPipeError || now - lastPipeErrorAt >= Stopwatch.Frequency * 30L)
                    {
                        WorkerError(phase, pipeName, ex);
                        hasReportedPipeError = true;
                        lastPipeErrorAt = now;
                    }
                }
                finally
                {
                    activePipe = null;
                    if (pipe != null) { try { pipe.Dispose(); } catch { } }
                }
                if (!stopping) WaitForWorker(500);
            }
        }

        private bool WaitForWorker(int milliseconds)
        {
            try
            {
                AutoResetEvent signal = wakeWorker;
                if (signal == null) { stopping = true; return false; }
                return signal.WaitOne(milliseconds);
            }
            catch (Exception ex)
            {
                WorkerError("worker wait", null, ex);
                stopping = true;
                return false;
            }
        }

        private void WorkerLog(string message)
        {
            WriteWorkerLogLine(DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " INFO " + message);
        }

        private void DrainPendingWorkerLog()
        {
            string message = Interlocked.Exchange(ref pendingWorkerLog, null);
            if (message != null) WorkerLog(message);
        }

        private void WorkerError(string phase, string endpoint, Exception exception)
        {
            string details = exception.GetType().FullName + ": " + (exception.Message ?? "");
            details = details.Replace('\r', ' ').Replace('\n', ' ');
            if (details.Length > 512) details = details.Substring(0, 512);
            string message = "Worker " + phase + " failed" + (endpoint == null ? "" : " for pipe " + endpoint) + ": " + details;
            Interlocked.Exchange(ref lastWorkerDiagnostic, message);
            WriteWorkerLogLine(DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " ERROR " + message);
        }

        private void WriteWorkerLogLine(string line)
        {
            try
            {
                string path = Interlocked.CompareExchange(ref workerDiagnosticLogPath, null, null);
                if (path == null)
                {
                    string root = string.IsNullOrWhiteSpace(installNamespace) ? null : Path.Combine(installNamespace, "Logs");
                    if (string.IsNullOrWhiteSpace(root)) root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    if (string.IsNullOrWhiteSpace(root)) root = Path.GetTempPath();
                    path = Path.Combine(root, "ExpanseFoundations", "ClockBridge", "worker.log");
                    Interlocked.CompareExchange(ref workerDiagnosticLogPath, path, null);
                }
                string directory = Path.GetDirectoryName(path);
                if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
                FileInfo current = new FileInfo(path);
                string record = line + Environment.NewLine;
                UTF8Encoding encoding = new UTF8Encoding(false);
                if (record.Length > 1024) record = record.Substring(0, 1024);
                int recordBytes = encoding.GetByteCount(record);
                if (current.Exists && current.Length + recordBytes > 65536) File.WriteAllText(path, string.Empty);
                File.AppendAllText(path, record, encoding);
            }
            catch (Exception logException)
            {
                long now = Stopwatch.GetTimestamp();
                if (now >= Interlocked.Read(ref nextLogFailureReport))
                {
                    Interlocked.Exchange(ref nextLogFailureReport, now + 60L * Stopwatch.Frequency);
                    Interlocked.Exchange(ref pendingLogFailure, "Diagnostic file unavailable: " + logException.GetType().Name +
                        "; path=" + Interlocked.CompareExchange(ref workerDiagnosticLogPath, null, null));
                }
                string diagnostic = Interlocked.CompareExchange(ref lastWorkerDiagnostic, null, null);
                if (diagnostic != null)
                    Interlocked.Exchange(ref lastWorkerDiagnostic, diagnostic + " (diagnostic file unavailable: " + logException.GetType().Name + ")");
            }
        }

        private const int ClockMaxFrameBytes = 256 * 1024;
        private static void WriteFrame(Stream stream, string json)
        {
            byte[] body = new UTF8Encoding(false, true).GetBytes(json);
            if (body.Length < 1 || body.Length > ClockMaxFrameBytes) return;
            byte[] header = BitConverter.GetBytes((uint)body.Length);
            if (!BitConverter.IsLittleEndian) Array.Reverse(header);
            stream.Write(header, 0, 4);
            stream.Write(body, 0, body.Length);
            stream.Flush();
        }

        private static string Json(ClockSample s)
        {
            string result = "{\"protocolVersion\":1,\"messageType\":\"clockSample\",\"sequence\":" + s.Sequence.ToString(CultureInfo.InvariantCulture) +
                ",\"sessionId\":" + Q(s.SessionId) + ",\"loadEpoch\":" + Q(s.LoadEpoch) + ",\"worldId\":" + Q(s.WorldId) + ",\"runId\":" + Q(s.RunId) +
                ",\"installNamespace\":" + Q(s.InstallNamespace) + ",\"saveFolder\":" + Q(s.SaveFolder) +
                ",\"saveTitle\":" + Q(s.SaveTitle) + ",\"utSeconds\":" + N(s.UtSeconds) +
                ",\"activeWorld\":" + (s.ActiveWorld ? "true" : "false") + ",\"scene\":" + Q(s.Scene) +
                ",\"paused\":" + (s.Paused.HasValue ? (s.Paused.Value ? "true" : "false") : "null") +
                ",\"formattedDate\":" + Q(s.FormattedDate) + ",\"warpRate\":null,\"depot\":" + DepotJson(s.Depot) + (s.Depots == null ? "" : ",\"depots\":" + DepotsJson(s.Depots)) + "}";
            if (s.Colony != null)
            {
                string prefix = result.Substring(0, result.Length - 1) + ",\"colony\":";
                string colony = ColonyJson(s.Colony, s.Colony.Wolf == null ? null : WolfJson(s.Colony.Wolf));
                string withColony = prefix + colony + "}";
                if (Encoding.UTF8.GetByteCount(withColony) <= ClockMaxFrameBytes) return withColony;
                string fitted = FitProductionColony(prefix, s.Colony, s.Colony.Wolf == null ? null : WolfJson(s.Colony.Wolf), true);
                if (fitted != null) return fitted;
                // Crew detail is optional. Preserve the vessel list, counts and
                // WOLF observations before falling back to a truncated colony.
                withColony = prefix + ColonyJson(s.Colony, s.Colony.Wolf == null ? null : WolfJson(s.Colony.Wolf), false) + "}";
                if (Encoding.UTF8.GetByteCount(withColony) <= ClockMaxFrameBytes) return withColony;
                fitted = FitProductionColony(prefix, s.Colony, s.Colony.Wolf == null ? null : WolfJson(s.Colony.Wolf), false);
                if (fitted != null) return fitted;
                if (s.Colony.Wolf != null)
                {
                    // Retain physical observations and as much of the virtual ledger as
                    // the pipe frame can carry. The ledger reports any omitted rows.
                    int rows = s.Colony.Wolf.Depots.Sum(d => d.Resources.Count);
                    int low = 0, high = rows - 1;
                    string best = null;
                    while (low <= high)
                    {
                        int middle = low + (high - low) / 2;
                        string candidate = prefix + ColonyJson(s.Colony, WolfJson(s.Colony.Wolf, middle), false) + "}";
                        if (Encoding.UTF8.GetByteCount(candidate) <= ClockMaxFrameBytes) { best = candidate; low = middle + 1; }
                        else high = middle - 1;
                    }
                    if (best != null) return best;
                    withColony = prefix + ColonyJson(s.Colony, WolfSizeLimitJson(s.Colony.Wolf), false) + "}";
                    if (Encoding.UTF8.GetByteCount(withColony) <= ClockMaxFrameBytes) return withColony;
                    fitted = FitProductionColony(prefix, s.Colony, WolfSizeLimitJson(s.Colony.Wolf), false);
                    if (fitted != null) return fitted;
                }
                withColony = prefix + ColonyJson(s.Colony,s.Colony.Wolf==null?null:WolfSizeLimitJson(s.Colony.Wolf),false,true)+"}";
                if (Encoding.UTF8.GetByteCount(withColony) <= ClockMaxFrameBytes) return withColony;
                string truncatedCensus = s.Colony.VesselCensus == null ? "" :
                    ",\"vesselCensus\":{\"status\":\"truncated\",\"reason\":\"Colony frame limit.\",\"observationSequence\":" +
                    s.Colony.VesselCensus.ObservationSequence.ToString(CultureInfo.InvariantCulture) + ",\"vesselIds\":[]}";
                string truncatedColony = "{\"status\":\"truncated\",\"reason\":\"Colony observation exceeded the 64 KiB message limit.\",\"observedUt\":" +
                    N(s.Colony.ObservedUt) + ",\"vessels\":[]" + truncatedCensus;
                withColony = prefix + truncatedColony +
                    (s.Colony.Wolf == null ? "" : ",\"wolf\":" + WolfSizeLimitJson(s.Colony.Wolf)) + "}}";
                if (Encoding.UTF8.GetByteCount(withColony) <= ClockMaxFrameBytes) return withColony;
                withColony = prefix + truncatedColony + "}}";
                if (Encoding.UTF8.GetByteCount(withColony) <= ClockMaxFrameBytes) return withColony;
            }
            if (Encoding.UTF8.GetByteCount(result) <= ClockMaxFrameBytes) return result;
            string compactDepot = CompactDepotJson(s.Depot);
            string fallback = "{\"protocolVersion\":1,\"messageType\":\"clockSample\",\"sequence\":" + s.Sequence.ToString(CultureInfo.InvariantCulture) +
                ",\"sessionId\":" + Q(s.SessionId) + ",\"loadEpoch\":" + Q(s.LoadEpoch) + ",\"worldId\":" + Q(s.WorldId) + ",\"runId\":" + Q(s.RunId) + ",\"installNamespace\":" + Q(s.InstallNamespace) +
                ",\"saveFolder\":" + Q(s.SaveFolder) + ",\"saveTitle\":" + Q(s.SaveTitle) + ",\"utSeconds\":" + N(s.UtSeconds) +
                ",\"activeWorld\":" + (s.ActiveWorld ? "true" : "false") + ",\"scene\":" + Q(s.Scene) + ",\"paused\":" + (s.Paused.HasValue ? (s.Paused.Value ? "true" : "false") : "null") +
                ",\"formattedDate\":" + QBound(s.FormattedDate, 2048) + ",\"warpRate\":null,\"depot\":" + compactDepot + (s.Depots == null ? "" : ",\"depots\":" + DepotsJson(s.Depots)) + "}";
            if (Encoding.UTF8.GetByteCount(fallback) <= ClockMaxFrameBytes) return fallback;
            return "{\"protocolVersion\":1,\"messageType\":\"clockSample\",\"sequence\":" + s.Sequence.ToString(CultureInfo.InvariantCulture) +
                ",\"sessionId\":" + Q(s.SessionId) + ",\"loadEpoch\":" + Q(s.LoadEpoch) + ",\"worldId\":" + Q(s.WorldId) + ",\"runId\":" + Q(s.RunId) + ",\"installNamespace\":" + Q(BoundedInstallPath(s.InstallNamespace)) + ",\"saveFolder\":null,\"saveTitle\":null,\"utSeconds\":" + N(s.UtSeconds) +
                ",\"activeWorld\":" + (s.ActiveWorld ? "true" : "false") + ",\"scene\":\"Unknown\",\"paused\":" + (s.Paused.HasValue ? (s.Paused.Value ? "true" : "false") : "null") + ",\"formattedDate\":null,\"warpRate\":null,\"depot\":" + compactDepot + "}";
        }

        private static string CompactDepotJson(DepotView d)
        {
            if (d == null) return "null";
            bool registered = string.Equals(d.RegistryState, "registered", StringComparison.Ordinal);
            bool unavailable = string.Equals(d.RegistryState, "unavailable", StringComparison.Ordinal);
            string observation = registered || unavailable ? "unavailable" : "none";
            string reason = registered ? "Depot payload exceeded frame size" : d.Reason;
            return "{\"schemaVersion\":1,\"registryState\":" + Q(d.RegistryState) + ",\"reason\":" + Q(reason) +
                ",\"worldId\":" + Q(d.WorldId) + ",\"depotId\":" + Q(d.DepotId) + ",\"membershipRevision\":" + (d.MembershipRevision.HasValue ? d.MembershipRevision.Value.ToString(CultureInfo.InvariantCulture) : "null") +
                ",\"label\":" + QBound(d.Label, 256) + ",\"anchorPartId\":" + (d.AnchorPartId.HasValue ? d.AnchorPartId.Value.ToString(CultureInfo.InvariantCulture) : "null") + ",\"memberCount\":" + (d.MemberCount.HasValue ? d.MemberCount.Value.ToString(CultureInfo.InvariantCulture) : "null") +
                ",\"currentVesselName\":" + QBound(d.CurrentVesselName, 256) + ",\"observationState\":" + Q(observation) + ",\"observationReason\":" + Q(reason) + ",\"snapshot\":null}";
        }

        private static string QBound(string value, int maxChars) { return value == null ? "null" : Q(value.Length > maxChars ? value.Substring(0, maxChars) : value); }
        private static string BoundedInstallPath(string value)
        {
            try { string full = Path.GetFullPath(value); return Encoding.UTF8.GetByteCount(full) <= 4096 ? full : Path.GetPathRoot(full); }
            catch { return Path.GetPathRoot(Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory)); }
        }

        private static string DepotJson(DepotView d)
        {
            if (d == null) return "null";
            StringBuilder b = new StringBuilder(512);
            b.Append("{\"schemaVersion\":1,\"registryState\":").Append(Q(d.RegistryState)).Append(",\"reason\":").Append(Q(d.Reason))
             .Append(",\"worldId\":").Append(Q(d.WorldId)).Append(",\"depotId\":").Append(Q(d.DepotId)).Append(",\"membershipRevision\":").Append(d.MembershipRevision.HasValue ? d.MembershipRevision.Value.ToString(CultureInfo.InvariantCulture) : "null")
             .Append(",\"label\":").Append(Q(d.Label)).Append(",\"anchorPartId\":").Append(d.AnchorPartId.HasValue ? d.AnchorPartId.Value.ToString(CultureInfo.InvariantCulture) : "null")
             .Append(",\"memberCount\":").Append(d.MemberCount.HasValue ? d.MemberCount.Value.ToString(CultureInfo.InvariantCulture) : "null").Append(",\"currentVesselName\":").Append(Q(d.CurrentVesselName))
             .Append(",\"observationState\":").Append(Q(d.ObservationState)).Append(",\"observationReason\":").Append(Q(d.ObservationReason)).Append(",\"snapshot\":");
            DepotSnapshot s = d.Snapshot;
            if (s == null) b.Append("null");
            else
            {
                b.Append("{\"sessionId\":").Append(Q(s.SessionId)).Append(",\"loadEpoch\":").Append(Q(s.LoadEpoch)).Append(",\"worldId\":").Append(Q(s.WorldId)).Append(",\"depotId\":").Append(Q(s.DepotId))
                 .Append(",\"membershipRevision\":").Append(s.MembershipRevision.ToString(CultureInfo.InvariantCulture)).Append(",\"revision\":").Append(s.Revision.ToString(CultureInfo.InvariantCulture))
                 .Append(",\"startedUt\":").Append(N(s.StartedUt)).Append(",\"completedUt\":").Append(N(s.CompletedUt)).Append(",\"ageSeconds\":").Append(N(s.AgeSeconds)).Append(",\"resources\":[");
                for (int i = 0; i < s.Resources.Count; i++) { DepotResourceRow r = s.Resources[i]; if (i > 0) b.Append(','); b.Append("{\"name\":").Append(Q(r.Name)).Append(",\"displayName\":").Append(Q(r.DisplayName)).Append(",\"amount\":").Append(N(r.Amount)).Append(",\"maxAmount\":").Append(N(r.MaxAmount)).Append('}'); }
                b.Append("]}");
            }
            return b.Append('}').ToString();
        }

        private static string DepotsJson(DepotSummary[] depots)
        {
            StringBuilder b = new StringBuilder("[");
            for (int i = 0; i < depots.Length; i++)
            {
                if (i > 0) b.Append(',');
                DepotSummary d = depots[i];
                b.Append("{\"depotId\":").Append(Q(d.DepotId)).Append(",\"label\":").Append(Q(d.Label))
                 .Append(",\"membershipRevision\":").Append(d.MembershipRevision.ToString(CultureInfo.InvariantCulture))
                 .Append(",\"membershipHash\":").Append(Q(d.MembershipHash)).Append(",\"status\":").Append(Q(d.Status))
                 .Append(",\"reason\":").Append(Q(d.Reason)).Append(",\"stockAgeSeconds\":").Append(N(d.StockAgeSeconds))
                 .Append(",\"memberCount\":").Append(d.MemberCount.ToString(CultureInfo.InvariantCulture)).Append(",\"resources\":[");
                for (int j = 0; j < d.Resources.Length; j++)
                {
                    if (j > 0) b.Append(',');
                    DepotResourceRow r = d.Resources[j];
                    b.Append("{\"name\":").Append(Q(r.Name)).Append(",\"displayName\":").Append(Q(r.DisplayName))
                     .Append(",\"amount\":").Append(N(r.Amount)).Append(",\"maxAmount\":").Append(N(r.MaxAmount)).Append('}');
                }
                b.Append("]}");
            }
            return b.Append(']').ToString();
        }

        private void OnDepotLoadBegin(ConfigNode ignored) { DepotRegistryModule r = DepotRegistryModule.Instance; if (r != null) r.MarkLoading(); }

        private static string SafeInstallNamespace(string candidate)
        {
            try { if (!string.IsNullOrWhiteSpace(candidate) && Path.IsPathRooted(candidate)) return Path.GetFullPath(candidate); } catch { }
            return Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);
        }

        private static string ResolvePublisherPipe(string userName, string[] args, out string rejected)
        {
            rejected = null;
            string normal = PipePrefix + userName;
            const string argPrefix = "-expanseClockPublisherPipe=";
            foreach (string arg in args ?? new string[0])
            {
                if (arg == null || !arg.StartsWith(argPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                string candidate = arg.Substring(argPrefix.Length);
                string required = "ExpanseFoundations.Clock.Publisher.dev." + userName + ".";
                if (candidate.Length <= 200 && candidate.StartsWith(required, StringComparison.Ordinal) && candidate.Length > required.Length && candidate.All(c => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' || c == '-' || c == '_')) return candidate;
                rejected = candidate.Length > 200 ? "name exceeded 200 characters" : "name did not match the per-user dev endpoint contract";
            }
            return normal;
        }

        private static string N(double? value)
        {
            return value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value)
                ? value.Value.ToString("R", CultureInfo.InvariantCulture) : "null";
        }

        private static string Q(string value)
        {
            if (value == null) return "null";
            StringBuilder b = new StringBuilder(value.Length + 2).Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '"': b.Append("\\\""); break;
                    case '\\': b.Append("\\\\"); break;
                    case '\b': b.Append("\\b"); break;
                    case '\f': b.Append("\\f"); break;
                    case '\n': b.Append("\\n"); break;
                    case '\r': b.Append("\\r"); break;
                    case '\t': b.Append("\\t"); break;
                    default:
                        if (char.IsHighSurrogate(c))
                        {
                            if (i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) b.Append(c).Append(value[++i]);
                            else b.Append("\\uFFFD");
                        }
                        else if (char.IsLowSurrogate(c)) b.Append("\\uFFFD");
                        else if (c < 0x20) b.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else b.Append(c);
                        break;
                }
            }
            return b.Append('"').ToString();
        }

        private void OnDestroy()
        {
            if (ReferenceEquals(Current, this)) Current = null;
            stopping = true;
            RemovePowerObserver();
            RemoveProductionModeObserver();
            StopEffectsWorker();
            StopWolfWorker();
            GameEvents.onGameStateLoad.Remove(OnGameStateLoad);
            GameEvents.onGameStatePostLoad.Remove(OnGameStatePostLoad);
            GameEvents.onLevelWasLoadedGUIReady.Remove(OnLevelWasLoadedGuiReady);
            GameEvents.onFlightReady.Remove(OnFlightReady);
            GameEvents.OnRevertToLaunchFlightState.Remove(OnRevert);
            GameEvents.OnRevertToPrelaunchFlightState.Remove(OnRevert);
            GameEvents.onGamePause.Remove(OnPause);
            GameEvents.onGameUnpause.Remove(OnUnpause);
            try { if (activePipe != null) activePipe.Dispose(); } catch { }
            try { if (wakeWorker != null) wakeWorker.Set(); } catch { }
        }
    }
}







