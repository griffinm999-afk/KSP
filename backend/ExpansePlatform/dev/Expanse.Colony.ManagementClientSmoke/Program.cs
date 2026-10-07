using System.Diagnostics;
using Expanse.Clock.Manager;
using Expanse.Domain.Colonies;

if(args.Length!=1 || !Path.IsPathFullyQualified(args[0]) || Path.GetFileName(args[0])!="Expanse.Colony.ManagementIpcSmoke.exe")throw new ArgumentException("Pass the absolute path of the isolated .NET472 IPC smoke executable.");
var pipe="ExpanseFoundations.Colonies.test."+Guid.NewGuid().ToString("N");
var start=new ProcessStartInfo(args[0]) {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};start.ArgumentList.Add("--serve");start.ArgumentList.Add(pipe);
using var child=Process.Start(start) ?? throw new Exception("Could not start isolated endpoint.");
try
{
 if(await child.StandardOutput.ReadLineAsync()!="Isolated endpoint ready.")throw new Exception("Isolated endpoint did not initialize.");
 var client=new ColonyManagementClient(pipe);
 var first=await client.SnapshotAsync();var snapshot=first.Snapshot ?? throw new Exception("No cross-runtime snapshot.");
 var command=new ColonyCommand {OperationId=Guid.NewGuid().ToString("D"),ContextKey=snapshot.ContextKey,ExpectedRevision=snapshot.State!.Revision,Kind="foundColony",Fields=new(){{"Name","Cross-runtime transport fixture"},{"Body","Minmus"},{"Biome","Greater Flats"},{"Latitude","0"},{"Longitude","0"}}};
 var result=await client.SubmitAsync(command);if(result.Outcome!="accepted")throw new Exception(".NET8 to .NET472 command rejected: "+result.Reason);
 var retry=await client.SubmitAsync(command);if(retry.Outcome!="duplicate")throw new Exception("Cross-runtime duplicate effect was not suppressed.");
 var stale=new ColonyCommand {OperationId=Guid.NewGuid().ToString("D"),ContextKey=snapshot.ContextKey+"/different-epoch",ExpectedRevision=result.Result!.State.Revision,Kind="foundColony",Fields=command.Fields};
 var rejection=await client.SubmitAsync(stale);if(rejection.Outcome!="rejected" || !rejection.Reason.Contains("context changed"))throw new Exception("Stale epoch was not rejected.");
 var final=await client.SnapshotAsync();if(final.Snapshot!.State!.Colonies.Count!=1)throw new Exception("Transport/replay tests created duplicate colony records.");
 Console.WriteLine(".NET8 Manager client ↔ .NET472 endpoint: snapshot, accepted command, same-ID retry and stale-context rejection passed. Synthetic domain fixture only; no KSP/Host/save connection.");
}
finally {if(!child.HasExited){child.Kill();await child.WaitForExitAsync();}}
