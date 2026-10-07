using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using UnityEngine;

namespace Expanse.WorldBridge
{
    internal sealed class ClockSample
    {
        public long Sequence;
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
    }

    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public sealed class WorldBridgeAddon : MonoBehaviour
    {
        private const string PipePrefix = "ExpanseFoundations.Clock.Publisher.v1.";
        private const double SampleIntervalSeconds = 0.5;
        private readonly string sessionId = Guid.NewGuid().ToString("D");
        private string loadEpoch = Guid.NewGuid().ToString("D");
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

        // Read by the disposable dev harness; no per-sample logging or file I/O is performed.
        public long CompletedSampleCount { get { return Interlocked.Read(ref completedSampleCount); } }
        public long TotalSampleElapsedStopwatchTicks { get { return Interlocked.Read(ref totalSampleElapsedStopwatchTicks); } }
        public long MaxSampleElapsedStopwatchTicks { get { return Interlocked.Read(ref maxSampleElapsedStopwatchTicks); } }
        public long SampleStopwatchFrequency { get { return Stopwatch.Frequency; } }
        public string WorkerDiagnosticLogPath { get { return Interlocked.CompareExchange(ref workerDiagnosticLogPath, null, null); } }
        public string LastWorkerDiagnostic { get { return Interlocked.CompareExchange(ref lastWorkerDiagnostic, null, null); } }

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
            clock = Stopwatch.StartNew();
            try { installNamespace = Path.GetFullPath(KSPUtil.ApplicationRootPath); }
            catch { installNamespace = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory); }
            wakeWorker = new AutoResetEvent(false);
            GameEvents.onGameStateLoad.Add(OnGameStateLoad);
            GameEvents.onGameStatePostLoad.Add(OnGameStatePostLoad);
            GameEvents.onLevelWasLoadedGUIReady.Add(OnLevelWasLoadedGuiReady);
            GameEvents.onFlightReady.Add(OnFlightReady);
            GameEvents.OnRevertToLaunchFlightState.Add(OnRevert);
            GameEvents.OnRevertToPrelaunchFlightState.Add(OnRevert);
            GameEvents.onGamePause.Add(OnPause);
            GameEvents.onGameUnpause.Add(OnUnpause);
            worker = new Thread(PipeWorker) { IsBackground = true, Name = "Expanse clock publisher" };
            worker.Start();
        }

        private void OnGameStateLoad(ConfigNode ignored)
        {
            BeginUnresolvedLoad(false, "game-state load");
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
            BeginUnresolvedLoad(true, "revert event");
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

        private void BeginUnresolvedLoad(bool sceneReadyObserved, string source)
        {
            if (!loadUnresolved)
            {
                loadUnresolved = true;
                StartNewEpoch();
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

        private void OnPause() { paused = true; }
        private void OnUnpause() { paused = false; }

        private void Update()
        {
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
            Interlocked.Exchange(ref latest, sample);
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
            Interlocked.Exchange(ref latest, sample);
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
            loadEpoch = Guid.NewGuid().ToString("D");
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
                pipeName = PipePrefix + userName;
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
                        ClockSample sample = Interlocked.Exchange(ref latest, null);
                        if (sample != null) WriteFrame(pipe, Json(sample));
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
                    string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
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
                string diagnostic = Interlocked.CompareExchange(ref lastWorkerDiagnostic, null, null);
                if (diagnostic != null)
                    Interlocked.Exchange(ref lastWorkerDiagnostic, diagnostic + " (diagnostic file unavailable: " + logException.GetType().Name + ")");
            }
        }

        private static void WriteFrame(Stream stream, string json)
        {
            byte[] body = new UTF8Encoding(false, true).GetBytes(json);
            if (body.Length < 1 || body.Length > 65536) return;
            byte[] header = BitConverter.GetBytes((uint)body.Length);
            if (!BitConverter.IsLittleEndian) Array.Reverse(header);
            stream.Write(header, 0, 4);
            stream.Write(body, 0, body.Length);
            stream.Flush();
        }

        private static string Json(ClockSample s)
        {
            return "{\"protocolVersion\":1,\"messageType\":\"clockSample\",\"sequence\":" + s.Sequence.ToString(CultureInfo.InvariantCulture) +
                ",\"sessionId\":" + Q(s.SessionId) + ",\"loadEpoch\":" + Q(s.LoadEpoch) +
                ",\"installNamespace\":" + Q(s.InstallNamespace) + ",\"saveFolder\":" + Q(s.SaveFolder) +
                ",\"saveTitle\":" + Q(s.SaveTitle) + ",\"utSeconds\":" + N(s.UtSeconds) +
                ",\"activeWorld\":" + (s.ActiveWorld ? "true" : "false") + ",\"scene\":" + Q(s.Scene) +
                ",\"paused\":" + (s.Paused.HasValue ? (s.Paused.Value ? "true" : "false") : "null") +
                ",\"formattedDate\":" + Q(s.FormattedDate) + ",\"warpRate\":null}";
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
            stopping = true;
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







