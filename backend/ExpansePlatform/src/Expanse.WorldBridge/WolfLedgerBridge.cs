using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using UnityEngine;
using WOLF;

namespace Expanse.WorldBridge
{
    [Serializable]
    internal sealed class WolfWireJson
    {
        // Populated by Unity's JSON deserializer, not by C# assignments.
#pragma warning disable 0649
        public string requestId, worldId, runId, sessionId, loadEpoch, body, biome, resource;
        public int expectedIncoming, expectedOutgoing, targetIncoming;
#pragma warning restore 0649
    }

    internal sealed class WolfWireRequest
    {
        public string RequestId, WorldId, RunId, SessionId, LoadEpoch, Body, Biome, Resource;
        public int ExpectedIncoming, ExpectedOutgoing, TargetIncoming;
    }

    public sealed partial class WorldBridgeAddon
    {
        private sealed class PendingWolfChange
        {
            public WolfWireRequest Request;
            public readonly ManualResetEventSlim Completed = new ManualResetEventSlim(false);
            public long ExpiresAt;
            public volatile bool Cancelled;
            public int State; // 0 queued, 1 applying on game thread, 2 complete
            public string Result;
        }
        private const string WolfPipePrefix = "ExpanseFoundations.WOLF.Admin.v1.";
        private readonly object wolfGate = new object();
        private readonly Dictionary<string, KeyValuePair<string, string>> wolfReceipts = new Dictionary<string, KeyValuePair<string, string>>(StringComparer.Ordinal);
        private readonly Queue<string> wolfReceiptOrder = new Queue<string>();
        private PendingWolfChange pendingWolfChange;
        private Thread wolfPipeWorker;
        private NamedPipeServerStream wolfPipe;
        private string[] wolfAllowedResources;

        private void StartWolfWorker()
        {
            wolfPipeWorker = new Thread(WolfPipeLoop) { IsBackground = true, Name = "Expanse WOLF admin pipe" };
            wolfPipeWorker.Start();
        }
        private void StopWolfWorker()
        {
            NamedPipeServerStream pipe;
            lock (wolfGate)
            {
                pipe = wolfPipe;
                wolfPipe = null;
                if (pendingWolfChange != null)
                {
                    pendingWolfChange.Cancelled = true;
                    pendingWolfChange.Result = WolfResult(pendingWolfChange.Request.RequestId, "rejected", "Game bridge stopped.", 0, 0);
                    pendingWolfChange.Completed.Set();
                    pendingWolfChange = null;
                }
            }
            // Mono's managed WaitForConnection can remain in native I/O after
            // Dispose. Cancel the native operation before closing its handle;
            // never hold the request gate while cancelling or disposing I/O.
            try { ColonyNativePipe.Cancel(pipe); } catch { }
            try { if (pipe != null) pipe.Dispose(); } catch { }
        }

        private void WolfPipeLoop()
        {
            string pipeName = WolfPipePrefix + Environment.UserName;
            const string marker = "-expanseWolfPipe=";
            string[] overrides = Environment.GetCommandLineArgs().Where(x => x.StartsWith(marker, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (overrides.Length > 1) { QueueWorkerLog("WOLF admin disabled: multiple pipe overrides"); return; }
            if (overrides.Length == 1)
            {
                string candidate = overrides[0].Substring(marker.Length);
                string required = "ExpanseFoundations.WOLF.Admin.dev." + Environment.UserName + ".";
                if (!candidate.StartsWith(required, StringComparison.Ordinal) || candidate.Length <= required.Length || candidate.Length > 200 ||
                    !candidate.All(c => char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_'))
                { QueueWorkerLog("WOLF admin disabled: invalid development pipe override"); return; }
                pipeName = candidate;
            }
            while (!stopping)
            {
                NamedPipeServerStream server = null;
                try
                {
                    using (server = ColonyNativePipe.Create(pipeName, true))
                    {
                        // Serialize publication with shutdown. A stream created
                        // after shutdown's snapshot must never enter native I/O.
                        lock (wolfGate)
                        {
                            if (stopping) break;
                            wolfPipe = server;
                        }
                        ColonyNativePipe.WaitForConnection(server);
                        if (stopping) break;
                        byte[] data = ReadWolfFrame(server);
                        if (stopping) break;
                        WolfWireJson decoded = JsonUtility.FromJson<WolfWireJson>(Encoding.UTF8.GetString(data));
                        WolfWireRequest request = decoded == null ? null : new WolfWireRequest
                        {
                            RequestId = decoded.requestId, WorldId = decoded.worldId,
                            RunId = decoded.runId, SessionId = decoded.sessionId,
                            LoadEpoch = decoded.loadEpoch, Body = decoded.body, Biome = decoded.biome,
                            Resource = decoded.resource, ExpectedIncoming = decoded.expectedIncoming,
                            ExpectedOutgoing = decoded.expectedOutgoing, TargetIncoming = decoded.targetIncoming
                        };
                        string response = HandleWolfRequest(request);
                        WriteWolfFrame(server, response);
                    }
                }
                catch (Exception ex)
                {
                    if (!stopping) QueueWorkerLog("WOLF admin pipe rejected request: " + Bound(ex.Message, 160));
                    if (!stopping) Thread.Sleep(100);
                }
                finally
                {
                    lock (wolfGate) if (ReferenceEquals(wolfPipe, server)) wolfPipe = null;
                }
            }
        }
        private static byte[] ReadWolfFrame(Stream stream)
        {
            byte[] header = new byte[4]; ReadWolfExactly(stream, header);
            int length = BitConverter.ToInt32(header, 0);
            if (length < 1 || length > 4096) throw new InvalidDataException("Invalid WOLF request length.");
            byte[] data = new byte[length]; ReadWolfExactly(stream, data); return data;
        }
        private static void ReadWolfExactly(Stream stream, byte[] bytes)
        {
            int offset = 0;
            while (offset < bytes.Length)
            {
                int read = stream.Read(bytes, offset, bytes.Length - offset);
                if (read <= 0) throw new EndOfStreamException();
                offset += read;
            }
        }
        private static void WriteWolfFrame(Stream stream, string json)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            byte[] header = BitConverter.GetBytes(bytes.Length);
            stream.Write(header, 0, 4); stream.Write(bytes, 0, bytes.Length); stream.Flush();
        }

        private string HandleWolfRequest(WolfWireRequest request)
        {
            if (request == null || !WolfText(request.RequestId, 100) || !WolfText(request.WorldId, 128) ||
                !WolfText(request.RunId, 128) || !WolfText(request.Body, 64) ||
                !WolfText(request.Biome, 64) || !WolfText(request.Resource, 64) ||
                !Guid.TryParse(request.SessionId, out _) || !Guid.TryParse(request.LoadEpoch, out _) ||
                request.ExpectedIncoming < 0 || request.ExpectedOutgoing < 0 ||
                request.TargetIncoming < request.ExpectedOutgoing || request.TargetIncoming > 1000000)
                return WolfResult(request == null ? "" : request.RequestId, "rejected", "Invalid WOLF ledger change.", 0, 0);
            PendingWolfChange pending = new PendingWolfChange
            {
                Request = request,
                ExpiresAt = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 6
            };
            lock (wolfGate)
            {
                if (stopping)
                    return WolfResult(request.RequestId, "rejected", "Game bridge stopped.", 0, 0);
                if (pendingWolfChange != null)
                    return WolfResult(request.RequestId, "rejected", "Another WOLF ledger change is in progress.", 0, 0);
                pendingWolfChange = pending;
            }
            if (!pending.Completed.Wait(TimeSpan.FromSeconds(7)))
            {
                lock (wolfGate)
                {
                    if (pending.State == 0)
                    {
                        pending.Cancelled = true;
                        if (ReferenceEquals(pendingWolfChange, pending)) pendingWolfChange = null;
                    }
                }
                if (!pending.Cancelled) pending.Completed.Wait(TimeSpan.FromSeconds(3));
                if (!pending.Completed.IsSet)
                    return WolfResult(request.RequestId, "rejected", "Change outcome is unknown; inspect the ledger before retrying.", 0, 0);
            }
            return pending.Result ?? WolfResult(request.RequestId, "rejected", "Game did not return a WOLF receipt.", 0, 0);
        }

        private void ProcessPendingWolfChange()
        {
            PendingWolfChange pending;
            lock (wolfGate)
            {
                pending = pendingWolfChange;
                if (pending != null) pending.State = 1;
            }
            if (pending == null) return;
            try
            {
                if (pending.Cancelled || Stopwatch.GetTimestamp() > pending.ExpiresAt)
                    pending.Result = WolfResult(pending.Request.RequestId, "rejected", "Change expired before game processing.", 0, 0);
                else pending.Result = ApplyWolfChange(pending.Request);
            }
            catch (Exception ex)
            {
                pending.Result = WolfResult(pending.Request.RequestId, "rejected", "WOLF change failed: " + Bound(ex.Message, 120), 0, 0);
            }
            finally
            {
                lock (wolfGate)
                {
                    pending.State = 2;
                    if (ReferenceEquals(pendingWolfChange, pending)) pendingWolfChange = null;
                    pending.Completed.Set();
                }
            }
        }

        private string ApplyWolfChange(WolfWireRequest r)
        {
            DepotRegistryModule registry = DepotRegistryModule.Instance;
            RecoveryCapsuleModule capsule = RecoveryCapsuleModule.Instance;
            if (loadUnresolved || !HighLogic.LoadedSceneIsGame || HighLogic.CurrentGame == null ||
                observedGame != null && !ReferenceEquals(HighLogic.CurrentGame, observedGame) ||
                IsNoWorldScene(CurrentScene()) || !CanAcceptRegistryEffects ||
                registry == null || capsule == null || !registry.IsReady || !capsule.IsLoaded ||
                !string.Equals(r.WorldId, registry.WorldId, StringComparison.Ordinal) ||
                !string.Equals(r.WorldId, capsule.WorldId, StringComparison.Ordinal) ||
                !string.Equals(r.RunId, runId, StringComparison.Ordinal) ||
                !string.Equals(r.SessionId, sessionId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(r.LoadEpoch, loadEpoch, StringComparison.OrdinalIgnoreCase))
                return WolfResult(r.RequestId, "rejected", "Loaded game identity or readiness changed. Refresh the colony page.", 0, 0);
            string fingerprint = WolfFingerprint(r);
            lock (wolfGate)
            {
                KeyValuePair<string, string> prior;
                if (wolfReceipts.TryGetValue(r.RequestId, out prior))
                    return prior.Key == fingerprint ? prior.Value.Replace("\"status\":\"applied\"", "\"status\":\"duplicate\"") :
                        WolfResult(r.RequestId, "rejected", "Request ID was already used for another change.", 0, 0);
            }
            WOLF_ScenarioModule scenario = FindObjectOfType<WOLF_ScenarioModule>();
            ScenarioPersister persister = scenario == null || scenario.ServiceManager == null ? null :
                scenario.ServiceManager.GetService<IRegistryCollection>() as ScenarioPersister;
            if (persister == null || !persister.IsLoaded)
                return WolfResult(r.RequestId, "rejected", "WOLF ledger is not ready.", 0, 0);
            if (!AllowedWolfResources(scenario).Contains(r.Resource, StringComparer.Ordinal))
                return WolfResult(r.RequestId, "rejected", "Resource is not in the installed WOLF catalog.", 0, 0);
            IDepot depot = persister.GetDepots().FirstOrDefault(d => d.Body == r.Body && d.Biome == r.Biome && d.IsEstablished);
            if (depot == null) return WolfResult(r.RequestId, "rejected", "WOLF depot is not established at this biome.", 0, 0);
            IResourceStream stream = depot.GetResources().FirstOrDefault(s => s.ResourceName == r.Resource);
            int incoming = stream == null ? 0 : stream.Incoming;
            int outgoing = stream == null ? 0 : stream.Outgoing;
            if (incoming != r.ExpectedIncoming || outgoing != r.ExpectedOutgoing)
                return WolfResult(r.RequestId, "rejected", "WOLF ledger changed. Refresh before editing.", incoming, outgoing);
            if (r.TargetIncoming < outgoing)
                return WolfResult(r.RequestId, "rejected", "Target production is below existing allocation.", incoming, outgoing);
            int delta = checked(r.TargetIncoming - incoming);
            if (delta != 0)
            {
                NegotiationResult result = depot.NegotiateProvider(new Dictionary<string, int> { { r.Resource, delta } });
                if (result is FailedNegotiationResult)
                    return WolfResult(r.RequestId, "rejected", "WOLF rejected the resource change.", incoming, outgoing);
            }
            IResourceStream after = depot.GetResources().FirstOrDefault(s => s.ResourceName == r.Resource);
            if (after == null && r.TargetIncoming == 0 && outgoing == 0)
            {
                // Setting an absent stream to zero is a valid no-op.
                string emptyReceipt = WolfResult(r.RequestId, "applied", "No WOLF capacity change was needed.", 0, 0);
                RememberWolfReceipt(r.RequestId, fingerprint, emptyReceipt);
                return emptyReceipt;
            }
            if (after == null || after.Incoming != r.TargetIncoming || after.Outgoing != outgoing)
                return WolfResult(r.RequestId, "rejected", "WOLF did not report the expected result. Inspect the ledger.",
                    after == null ? 0 : after.Incoming, after == null ? 0 : after.Outgoing);
            string receipt = WolfResult(r.RequestId, "applied", "Applied in the loaded game. Save KSP to retain it on disk.", after.Incoming, after.Outgoing);
            RememberWolfReceipt(r.RequestId, fingerprint, receipt);
            // The next clock sample must reflect the applied ledger immediately.
            lastColonySampleAt = -100;
            return receipt;
        }

        private static string WolfFingerprint(WolfWireRequest r) =>
            r.WorldId + "\n" + r.RunId + "\n" + r.SessionId + "\n" + r.LoadEpoch +
                "\n" + r.Body + "\n" + r.Biome + "\n" + r.Resource + "\n" +
                r.ExpectedIncoming.ToString(CultureInfo.InvariantCulture) + "/" +
                r.ExpectedOutgoing.ToString(CultureInfo.InvariantCulture) + "/" +
                r.TargetIncoming.ToString(CultureInfo.InvariantCulture);
        private void RememberWolfReceipt(string requestId, string fingerprint, string receipt)
        {
            lock (wolfGate)
            {
                wolfReceipts[requestId] = new KeyValuePair<string, string>(fingerprint, receipt);
                wolfReceiptOrder.Enqueue(requestId);
                while (wolfReceiptOrder.Count > 256) wolfReceipts.Remove(wolfReceiptOrder.Dequeue());
            }
        }

        private static bool WolfText(string value, int max) =>
            !string.IsNullOrWhiteSpace(value) && value.Length <= max && value.All(c => c >= ' ' && c != '\u007f');
        private static string WolfResult(string id, string status, string reason, int incoming, int outgoing) =>
            "{\"requestId\":" + Q(id) + ",\"status\":" + Q(status) + ",\"reason\":" + Q(reason) +
            ",\"incoming\":" + incoming.ToString(CultureInfo.InvariantCulture) +
            ",\"outgoing\":" + outgoing.ToString(CultureInfo.InvariantCulture) +
            ",\"available\":" + (incoming - outgoing).ToString(CultureInfo.InvariantCulture) + "}";

        private WolfSnapshot ObserveWolf(double? ut)
        {
            WolfSnapshot snapshot = new WolfSnapshot { Status = "unavailable", ObservedUt = ut };
            try
            {
                WOLF_ScenarioModule scenario = FindObjectOfType<WOLF_ScenarioModule>();
                ScenarioPersister persister = scenario == null || scenario.ServiceManager == null ? null :
                    scenario.ServiceManager.GetService<IRegistryCollection>() as ScenarioPersister;
                if (persister == null || !persister.IsLoaded)
                { snapshot.Reason = "WOLF ledger is not loaded."; return snapshot; }
                snapshot.AllowedResources = AllowedWolfResources(scenario).ToArray();
                foreach (IDepot depot in persister.GetDepots().Where(d => d != null && d.IsEstablished)
                    .OrderBy(d => d.Body == "Minmus" ? 0 : 1).ThenBy(d => d.Body).ThenBy(d => d.Biome).Take(12))
                {
                    WolfDepotRow row = new WolfDepotRow { Body = Bound(depot.Body, 64), Biome = Bound(depot.Biome, 64), Established = true };
                    HashSet<string> resourceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (IResourceStream stream in depot.GetResources().Where(s => s != null).OrderBy(s => s.ResourceName).Take(80))
                    {
                        if (!WolfText(stream.ResourceName, 64) || stream.Incoming < 0 || stream.Outgoing < 0 ||
                            !resourceNames.Add(stream.ResourceName))
                            throw new InvalidDataException("WOLF ledger contains an invalid or duplicate resource stream.");
                        row.Resources.Add(new WolfResourceRow { Name = stream.ResourceName, Incoming = stream.Incoming,
                            Outgoing = stream.Outgoing, Available = stream.Available });
                    }
                    snapshot.Depots.Add(row);
                }
                snapshot.Status = persister.GetDepots().Count > 12 || snapshot.Depots.Any(d => d.Resources.Count >= 80) ? "truncated" : "observed";
                snapshot.Reason = snapshot.Status == "truncated" ? "Only the first WOLF depots and resources are shown." : null;
            }
            catch (Exception ex)
            {
                snapshot.Status = "unavailable"; snapshot.Reason = "WOLF observation failed: " + Bound(ex.Message, 120);
                snapshot.Depots.Clear();
            }
            return snapshot;
        }

        private string[] AllowedWolfResources(WOLF_ScenarioModule scenario)
        {
            if (wolfAllowedResources != null) return wolfAllowedResources;
            HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
            foreach (string name in Configuration.DefaultHarvestableResources.Concat(Configuration.DefaultRefinedResources)
                .Concat(Configuration.DefaultAssembledResources).Concat(Configuration.DefaultLifeSupportResources)
                .Concat(scenario.Configuration.AllowedHarvestableResources))
                if (WolfText(name, 64)) names.Add(name);
            // WOLF recipes in the installed GameData also define crew points and
            // biome veins. Read those exact names instead of accepting arbitrary text.
            foreach (ConfigNode part in GameDatabase.Instance.GetConfigNodes("PART"))
                foreach (ConfigNode module in part.GetNodes("MODULE"))
                    if (module.GetValue("name") == "WOLF_RecipeOption")
                        foreach (string key in new[] { "InputResources", "OutputResources" })
                        {
                            string[] entries = (module.GetValue(key) ?? "").Split(',');
                            for (int i = 0; i + 1 < entries.Length; i += 2)
                            {
                                string name = entries[i].Trim();
                                if (WolfText(name, 64)) names.Add(name);
                            }
                        }
            wolfAllowedResources = names.OrderBy(x => x, StringComparer.Ordinal).Take(80).ToArray();
            return wolfAllowedResources;
        }

        private static string WolfJson(WolfSnapshot s, int maxResourceRows = int.MaxValue)
        {
            int totalRows = s.Depots.Sum(d => d.Resources.Count);
            bool clipped = maxResourceRows < totalRows;
            StringBuilder b = new StringBuilder("{\"status\":").Append(Q(clipped ? "truncated" : s.Status))
                .Append(",\"reason\":").Append(Q(clipped ? "WOLF ledger exceeded the clock message size limit; only the first resource rows are shown." : s.Reason))
                .Append(",\"observedUt\":").Append(N(s.ObservedUt)).Append(",\"allowedResources\":").Append(StringArray(s.AllowedResources))
                .Append(",\"depots\":[");
            int writtenRows = 0;
            for (int i = 0; i < s.Depots.Count; i++)
            {
                WolfDepotRow d = s.Depots[i]; if (i > 0) b.Append(',');
                b.Append("{\"body\":").Append(Q(d.Body)).Append(",\"biome\":").Append(Q(d.Biome))
                    .Append(",\"established\":").Append(B(d.Established)).Append(",\"resources\":[");
                for (int j = 0; j < d.Resources.Count; j++)
                {
                    if (writtenRows >= maxResourceRows) break;
                    WolfResourceRow r = d.Resources[j]; if (j > 0) b.Append(',');
                    b.Append("{\"name\":").Append(Q(r.Name)).Append(",\"incoming\":").Append(r.Incoming)
                        .Append(",\"outgoing\":").Append(r.Outgoing).Append(",\"available\":").Append(r.Available).Append('}');
                    writtenRows++;
                }
                b.Append("]}");
            }
            return b.Append("]}").ToString();
        }

        private static string WolfSizeLimitJson(WolfSnapshot s) =>
            "{\"status\":\"truncated\",\"reason\":\"WOLF ledger exceeded the clock message size limit.\",\"observedUt\":" +
            N(s.ObservedUt) + ",\"allowedResources\":[],\"depots\":[]}";
    }
}
