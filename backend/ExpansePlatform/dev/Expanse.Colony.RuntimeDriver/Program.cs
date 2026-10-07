using System.Text.Json;
using Expanse.Clock.Manager;
using Expanse.Domain.Colonies;

var options=new Dictionary<string,string>(StringComparer.Ordinal);
bool allowMutation=false;
try
{
 for(int i=0;i<args.Length;i++)
 {
  if(args[i]=="--allow-development-mutation"){allowMutation=true;continue;}
  if(args[i] is not ("--pipe" or "--command-file" or "--output") || i+1>=args.Length || options.ContainsKey(args[i]))throw new ArgumentException("Usage: --pipe <ExpanseFoundations.Colonies.dev.name> [--output <path>] [--command-file <path> --allow-development-mutation]");
  options.Add(args[i],args[++i]);
 }
 if(!options.TryGetValue("--pipe",out var pipe) || !pipe.StartsWith("ExpanseFoundations.Colonies.dev.",StringComparison.Ordinal))throw new ArgumentException("An explicit isolated development pipe is required; production defaults are never used by this driver.");
 var client=new ColonyManagementClient(pipe);
 ColonyManagementWireResponse response;
 if(options.TryGetValue("--command-file",out var commandFile))
 {
  if(!allowMutation)throw new ArgumentException("Submission requires --allow-development-mutation after verifying the executable, installation, save and IPC isolation.");
  var file=new FileInfo(Path.GetFullPath(commandFile));if(file.Length>512*1024)throw new ArgumentException("Command file exceeds 512 KiB.");
  var command=JsonSerializer.Deserialize<ColonyCommand>(await File.ReadAllTextAsync(file.FullName)) ?? throw new ArgumentException("Command file is empty.");
  response=await client.SubmitAsync(command);
 }
 else response=await client.SnapshotAsync();
 if(options.TryGetValue("--output",out var output))await File.WriteAllTextAsync(Path.GetFullPath(output),JsonSerializer.Serialize(response,new JsonSerializerOptions {WriteIndented=true}));
 Console.WriteLine(response.Outcome+" · "+response.Reason);
 if(response.Snapshot is { } snapshot)Console.WriteLine(snapshot.Status+" · "+snapshot.Reason+" · context "+snapshot.ContextKey+" · revision "+snapshot.State?.Revision+" · colonies "+snapshot.State?.Colonies.Count);
 if(response.Result is { } result)Console.WriteLine("Result "+result.ResultId+" · revision "+result.State?.Revision);
 return response.Outcome is "unknown" or "unavailable" ? 2 : 0;
}
catch(Exception ex){Console.Error.WriteLine("Driver failed: "+ex.Message+". If a submission was transmitted, retry the exact same command file and operation ID to reconcile.");return 2;}
