using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Expanse.Domain.Colonies;

namespace Expanse.WorldBridge
{
    // No KSP/Unity access on a pipe worker. Only Pump invokes the save-runtime
    // delegates. Responses are detached copies returned by the runtime.
    public sealed class ColonyManagementEndpoint : IDisposable
    {
        private sealed class Pending
        {
            public ColonyManagementWireRequest Request;
            public int Stage; // 0 queued, 1 started, 2 complete, 3 cancelled before start
            public readonly TaskCompletionSource<byte[]> Completion = new TaskCompletionSource<byte[]>();
        }
        private readonly string pipeName;
        private readonly Func<ColonyManagementSnapshot> capture;
        private readonly Func<ColonyCommand, ColonyResult> submit;
        private readonly Func<SettlementReadRequest, SettlementPage> settlements;
        private readonly ConcurrentQueue<Pending> queue = new ConcurrentQueue<Pending>();
        private readonly List<NamedPipeServerStream> listeners = new List<NamedPipeServerStream>();
        private readonly object gate = new object();
        private readonly CancellationTokenSource stop = new CancellationTokenSource();
        private int queued, mainThreadId, started;
        private readonly ConcurrentQueue<string> diagnostics=new ConcurrentQueue<string>();
        private int diagnosticCount;
        private string lastDiagnostic;
        public Action<string> Diagnostic {get;set;}
        public ColonyManagementEndpoint(string pipeName, Func<ColonyManagementSnapshot> capture, Func<ColonyCommand, ColonyResult> submit, Func<SettlementReadRequest,SettlementPage> settlements = null)
        {
            ColonyManagementWire.ValidatePipeName(pipeName);
            this.settlements = settlements;
            this.pipeName = pipeName; this.capture = capture ?? throw new ArgumentNullException("capture"); this.submit = submit ?? throw new ArgumentNullException("submit");
        }
        public void Start()
        {
            if (Interlocked.Exchange(ref started, 1) != 0) return;
            mainThreadId = Thread.CurrentThread.ManagedThreadId;
            for (int i = 0; i < 4; i++) Task.Factory.StartNew(Worker,CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
        }
        public void Pump(int maxRequests = 4)
        {
            if (mainThreadId != Thread.CurrentThread.ManagedThreadId) throw new InvalidOperationException("Colony endpoint must be pumped on its owning Unity thread.");
            string diagnostic;
            for(int i=0;i<4 && diagnostics.TryDequeue(out diagnostic);i++){Interlocked.Decrement(ref diagnosticCount);if(diagnostic!=lastDiagnostic){lastDiagnostic=diagnostic;var callback=Diagnostic;if(callback!=null)callback(diagnostic);}}
            Pending item;
            for (int i = 0; i < Math.Max(0, Math.Min(32, maxRequests)) && queue.TryDequeue(out item); i++)
            {
                Interlocked.Decrement(ref queued);
                if (stop.IsCancellationRequested || Interlocked.CompareExchange(ref item.Stage, 1, 0) != 0) continue;
                ColonyManagementWireResponse response;
                try
                {
                    if (item.Request.Kind == "snapshot") response = new ColonyManagementWireResponse { RequestId = item.Request.RequestId, Outcome = "snapshot", Snapshot = capture() };
                    else if(item.Request.Kind == "settlements") response = new ColonyManagementWireResponse { RequestId=item.Request.RequestId,Outcome="settlements",Settlements=settlements==null ? new SettlementPage {Reason="Settlement endpoint unavailable."} : settlements(item.Request.Settlements) };
                    else
                    {
                        var result = submit(item.Request.Command);
                        response = new ColonyManagementWireResponse { RequestId = item.Request.RequestId, Outcome = result.Outcome, Reason = result.Reason, Result = result };
                    }
                }
                catch (Exception ex) { response = Reply(item.Request, "unknown", "Runtime result is unavailable; reconcile the same operation ID. " + ex.GetType().Name); }
                byte[] encoded;
                try { encoded=ColonyManagementWire.Encode(response); }
                catch (Exception ex) { encoded=ColonyManagementWire.Encode(Reply(item.Request,"unknown","Readback could not be encoded; reconcile the same operation. "+ex.GetType().Name)); }
                Interlocked.Exchange(ref item.Stage, 2);
                item.Completion.TrySetResult(encoded);
            }
        }
        private void Worker()
        {
            while (!stop.IsCancellationRequested)
            {
                NamedPipeServerStream server = null;
                try
                {
                    lock (gate)
                    {
                        if(stop.IsCancellationRequested)return;
                        server=ColonyNativePipe.Create(pipeName,listeners.Count==0);
                        if(listeners.Count==0)
                        {if(Interlocked.Increment(ref diagnosticCount)<=16)diagnostics.Enqueue("Local management pipe listening with protected current-user access: "+pipeName);else Interlocked.Decrement(ref diagnosticCount);}
                        listeners.Add(server);
                    }
                    ColonyNativePipe.WaitForConnection(server);
                    using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
                    using (deadline.Token.Register(()=>{ColonyNativePipe.Cancel(server);try{server.Dispose();}catch{}}))
                    {
                        deadline.CancelAfter(TimeSpan.FromSeconds(10));
                        var bytes = ReadFrame(server, deadline.Token).GetAwaiter().GetResult();
                        var request = ColonyManagementWire.DecodeRequest(bytes);
                        deadline.CancelAfter(Timeout.Infinite); deadline.Token.ThrowIfCancellationRequested();
                        ColonyManagementWireResponse response = null;
                        byte[] encoded = null;
                        if (Interlocked.Increment(ref queued) > ColonyManagementWire.MaxQueuedRequests)
                        { Interlocked.Decrement(ref queued); response = Reply(request, "rejected", "The colony request queue is full; no action was started."); }
                        else
                        {
                            var pending = new Pending { Request = request }; queue.Enqueue(pending);
                            if (pending.Completion.Task.Wait(TimeSpan.FromSeconds(12))) encoded = pending.Completion.Task.Result;
                            else
                            {
                                bool cancelled = Interlocked.CompareExchange(ref pending.Stage, 3, 0) == 0;
                                response = Reply(request, cancelled ? "notStarted" : "unknown", cancelled ? "The Unity thread did not start this request; no mutation occurred." : "The request may have started. Reconcile the same operation ID before another action.");
                            }
                        }
                        deadline.CancelAfter(TimeSpan.FromSeconds(10));
                        if (encoded == null) encoded = ColonyManagementWire.Encode(response);
                        WriteFrame(server, encoded, deadline.Token).GetAwaiter().GetResult();
                    }
                }
                catch (Exception ex)
                {
                    if(!stop.IsCancellationRequested)
                    {
                        if(Interlocked.Increment(ref diagnosticCount)<=16)diagnostics.Enqueue("Local pipe worker "+ex.GetType().Name+": "+(ex.Message.Length>512 ? ex.Message.Substring(0,512) : ex.Message));else Interlocked.Decrement(ref diagnosticCount);
                        stop.Token.WaitHandle.WaitOne(500);
                    }
                }
                finally { if (server != null) {lock(gate){server.Dispose();listeners.Remove(server);}} }
            }
        }
        private static ColonyManagementWireResponse Reply(ColonyManagementWireRequest request, string outcome, string reason) => new ColonyManagementWireResponse { RequestId = request.RequestId, Outcome = outcome, Reason = reason };
        private static async Task<byte[]> ReadFrame(Stream stream, CancellationToken token)
        {
            var header = new byte[4]; await ReadExactly(stream, header, token).ConfigureAwait(false);
            int length = BitConverter.ToInt32(header, 0);
            if (length <= 0 || length > ColonyManagementWire.MaxFrameBytes) throw new InvalidDataException("Invalid colony frame length.");
            var bytes = new byte[length]; await ReadExactly(stream, bytes, token).ConfigureAwait(false); return bytes;
        }
        private static async Task ReadExactly(Stream stream, byte[] bytes, CancellationToken token)
        {
            int offset = 0; while (offset < bytes.Length) { int read = await stream.ReadAsync(bytes, offset, bytes.Length - offset, token).ConfigureAwait(false); if (read == 0) throw new EndOfStreamException(); offset += read; }
        }
        private static async Task WriteFrame(Stream stream, byte[] bytes, CancellationToken token)
        {
            var header = BitConverter.GetBytes(bytes.Length); await stream.WriteAsync(header,0,4,token).ConfigureAwait(false); await stream.WriteAsync(bytes,0,bytes.Length,token).ConfigureAwait(false); await stream.FlushAsync(token).ConfigureAwait(false);
        }
        public void Dispose()
        {
            stop.Cancel(); lock (gate) foreach (var listener in listeners) try {ColonyNativePipe.Cancel(listener); listener.Dispose(); } catch { }
            Pending item; while (queue.TryDequeue(out item)) { Interlocked.Decrement(ref queued); Interlocked.CompareExchange(ref item.Stage,3,0); item.Completion.TrySetResult(ColonyManagementWire.Encode(Reply(item.Request,"notStarted","The colony endpoint closed before this queued action started."))); }
        }
    }
}
