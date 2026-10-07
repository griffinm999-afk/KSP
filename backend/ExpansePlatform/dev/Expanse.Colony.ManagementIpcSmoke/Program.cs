using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using System.Security.AccessControl;
using System.Security.Principal;
using Expanse.Domain.Colonies;
using Expanse.WorldBridge;

internal static class Program
{
    static int Main(string[] args)
    {
        if(args.Length==2 && args[0]=="--serve") return Serve(args[1]);
        string pipe="ExpanseFoundations.Colonies.test."+Guid.NewGuid().ToString("N");
        int owner=Thread.CurrentThread.ManagedThreadId,submitCount=0;
        var state=ColonyEngine.Create(Guid.NewGuid().ToString("D"),0);
        var env=new ColonyEnvironment {WorldId=state.WorldId,ContextKey=state.WorldId+"/isolated-epoch",AvailableFunds=1000000,Ut=0};
        env.BodyRadiiMeters.Add("Minmus",60000);
        using(var endpoint=new ColonyManagementEndpoint(pipe,
            ()=> { CheckThread(owner);return new ColonyManagementSnapshot {State=ColonyStateCodec.Copy(state),ContextKey=env.ContextKey,Status="Isolated IPC test",AvailableFunds=env.AvailableFunds}; },
            command=> { CheckThread(owner);submitCount++;var result=ColonyEngine.Execute(state,command,env);state=result.State;return result; }))
        {
            endpoint.Start();
            var snapshot=Run(endpoint,Task.Run(()=>Exchange(pipe,new ColonyManagementWireRequest {RequestId=Guid.NewGuid().ToString("D"),Kind="snapshot"})));
            if(snapshot.Snapshot==null || snapshot.Snapshot.ContextKey!=env.ContextKey)throw new Exception("Snapshot identity mismatch.");
            var command=new ColonyCommand {OperationId=Guid.NewGuid().ToString("D"),ContextKey=env.ContextKey,ExpectedRevision=0,Kind="foundColony"};
            command.Fields.Add("Name","IPC-only test colony");command.Fields.Add("Body","Minmus");command.Fields.Add("Biome","Greater Flats");command.Fields.Add("Latitude","0");command.Fields.Add("Longitude","0");
            var first=Run(endpoint,Task.Run(()=>Exchange(pipe,new ColonyManagementWireRequest {RequestId=Guid.NewGuid().ToString("D"),Kind="submit",Command=command})));
            var second=Run(endpoint,Task.Run(()=>Exchange(pipe,new ColonyManagementWireRequest {RequestId=Guid.NewGuid().ToString("D"),Kind="submit",Command=command})));
            if(first.Outcome!="accepted" || second.Outcome!="duplicate" || state.Colonies.Count!=1 || submitCount!=2)throw new Exception("Duplicate submission repeated or lost the effect. First "+first.Outcome+": "+first.Reason+"; second "+second.Outcome+": "+second.Reason+"; colonies "+state.Colonies.Count+"; callbacks "+submitCount);
            // A request that never reaches the owner thread must explicitly state
            // notStarted and must remain cancelled when Pump eventually runs.
            var neverStarted=Task.Run(()=>Exchange(pipe,new ColonyManagementWireRequest {RequestId=Guid.NewGuid().ToString("D"),Kind="submit",Command=new ColonyCommand {OperationId=Guid.NewGuid().ToString("D"),ContextKey=env.ContextKey,ExpectedRevision=state.Revision,Kind="foundColony",Fields=command.Fields}}));
            if(!neverStarted.Wait(TimeSpan.FromSeconds(16)))throw new Exception("Queue timeout did not bound the reply.");
            if(neverStarted.Result.Outcome!="notStarted")throw new Exception("Unstarted mutation was not cancelled.");
            endpoint.Pump(32);if(submitCount!=2 || state.Colonies.Count!=1)throw new Exception("Cancelled request later mutated state.");
        }
        Console.WriteLine("Isolated named-pipe snapshot, main-thread callback, duplicate replay and not-started cancellation checks passed. No KSP/Host process or save was used.");return 0;
    }
    static int Serve(string pipe)
    {
        ColonyManagementWire.ValidatePipeName(pipe);
        if(!pipe.StartsWith("ExpanseFoundations.Colonies.test.",StringComparison.Ordinal))throw new ArgumentException("Only explicit isolated test pipes are allowed.");
        int owner=Thread.CurrentThread.ManagedThreadId;
        var state=ColonyEngine.Create(Guid.NewGuid().ToString("D"),0);
        var env=new ColonyEnvironment {WorldId=state.WorldId,ContextKey=state.WorldId+"/cross-runtime-test-epoch",AvailableFunds=1000000,Ut=0};env.BodyRadiiMeters.Add("Minmus",60000);
        using(var endpoint=new ColonyManagementEndpoint(pipe,
            ()=> {CheckThread(owner);return new ColonyManagementSnapshot {State=ColonyStateCodec.Copy(state),ContextKey=env.ContextKey,Status="Isolated cross-runtime fixture",AvailableFunds=env.AvailableFunds};},
            command=> {CheckThread(owner);var result=ColonyEngine.Execute(state,command,env);state=result.State;return result;}))
        {
            endpoint.Start();Console.WriteLine("Isolated endpoint ready.");
            var until=DateTime.UtcNow+TimeSpan.FromSeconds(30);while(DateTime.UtcNow<until){endpoint.Pump(4);Thread.Sleep(10);}
        }
        return 0;
    }
    static void CheckThread(int owner) {if(Thread.CurrentThread.ManagedThreadId!=owner)throw new Exception("Runtime callback ran on a pipe worker.");}
    static ColonyManagementWireResponse Run(ColonyManagementEndpoint endpoint,Task<ColonyManagementWireResponse> pending)
    {
        var until=DateTime.UtcNow+TimeSpan.FromSeconds(8);while(!pending.IsCompleted && DateTime.UtcNow<until) {endpoint.Pump(4);Thread.Sleep(10);}
        if(!pending.IsCompleted)throw new Exception("IPC request exceeded its test deadline.");return pending.GetAwaiter().GetResult();
    }
    static ColonyManagementWireResponse Exchange(string name,ColonyManagementWireRequest request)
    {
        using(var pipe=new NamedPipeClientStream(".",name,PipeDirection.InOut))
        {
            pipe.Connect(2000);
            var acl=pipe.GetAccessControl();var rules=acl.GetAccessRules(true,false,typeof(SecurityIdentifier));
            var user=WindowsIdentity.GetCurrent().User;bool allowed=false,networkDenied=false;
            foreach(PipeAccessRule rule in rules)
            {
                if(rule.AccessControlType==AccessControlType.Allow){if(!rule.IdentityReference.Equals(user))throw new Exception("Native colony pipe grants another principal access.");allowed=true;}
                if(rule.AccessControlType==AccessControlType.Deny && rule.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.NetworkSid,null)))networkDenied=true;
            }
            if(!acl.AreAccessRulesProtected || !allowed || !networkDenied)throw new Exception("Native colony pipe lacks protected same-user/network-deny security.");
            var bytes=ColonyManagementWire.Encode(request);var header=BitConverter.GetBytes(bytes.Length);pipe.Write(header,0,4);pipe.Write(bytes,0,bytes.Length);pipe.Flush();
            Read(pipe,header);int length=BitConverter.ToInt32(header,0);if(length<=0 || length>ColonyManagementWire.MaxFrameBytes)throw new Exception("Invalid frame length.");
            bytes=new byte[length];Read(pipe,bytes);var response=ColonyManagementWire.DecodeResponse(bytes);if(response.RequestId!=request.RequestId)throw new Exception("Reply mismatch.");return response;
        }
    }
    static void Read(Stream stream,byte[] bytes) {int offset=0;while(offset<bytes.Length){int count=stream.Read(bytes,offset,bytes.Length-offset);if(count==0)throw new EndOfStreamException();offset+=count;}}
}
