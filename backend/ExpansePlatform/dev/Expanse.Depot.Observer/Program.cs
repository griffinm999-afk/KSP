using System.Text.Json;
using Expanse.Clock.Core;

var duration = 180;
string? output = null;
string? viewPipe = null;
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--duration-seconds" && i + 1 < args.Length && int.TryParse(args[++i], out var seconds)) duration = seconds;
    else if (args[i] == "--output" && i + 1 < args.Length) output = args[++i];
    else if (args[i] == "--view-pipe" && i + 1 < args.Length) viewPipe = args[++i];
    else { Console.Error.WriteLine("Usage: Expanse.Depot.Observer [--duration-seconds 1..600] [--output absolute-jsonl-path] [--view-pipe name]"); return 2; }
}
if (duration is < 1 or > 600) { Console.Error.WriteLine("Duration must be between 1 and 600 seconds."); return 2; }
output ??= Path.Combine(AppContext.BaseDirectory, "depot-observer-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".jsonl");
output = Path.GetFullPath(output);
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
Console.WriteLine($"Observing Clock Host for {duration}s; JSONL: {output}");
var client = new ClockViewClient(viewPipe);
using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(duration));
var options = new JsonSerializerOptions(ClockProtocol.JsonOptions);
await using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
await using var writer = new StreamWriter(file);
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
while (!stop.IsCancellationRequested)
{
    var record = new Dictionary<string, object?> { ["observedAtUtc"] = DateTimeOffset.UtcNow };
    try
    {
        var view = await client.GetSnapshotAsync(TimeSpan.FromSeconds(2), stop.Token);
        record["hostReachable"] = true;
        record["clockStatus"] = view.Status;
        record["clockAgeSeconds"] = view.AgeSeconds;
        record["publisherConnected"] = view.PublisherConnected;
        if (view.Sample is { } sample)
        {
            record["sequence"] = sample.Sequence;
            record["sessionId"] = sample.SessionId;
            record["loadEpoch"] = sample.LoadEpoch;
            record["installNamespace"] = sample.InstallNamespace;
            record["saveFolder"] = sample.SaveFolder;
            record["saveTitle"] = sample.SaveTitle;
            record["utSeconds"] = sample.UtSeconds;
        }
        if (view.DepotView is { } depot)
        {
            record["depotStatus"] = depot.Status;
            record["depotReason"] = depot.Reason;
            record["worldId"] = depot.WorldId;
            record["depotId"] = depot.DepotId;
            record["label"] = depot.Label;
            record["currentVesselName"] = depot.CurrentVesselName;
            record["memberCount"] = depot.MemberCount;
            record["snapshotRevision"] = depot.SnapshotRevision;
            record["stockAgeSeconds"] = depot.StockAgeSeconds;
            record["resources"] = depot.Resources.Select(r => new { r.Name, r.DisplayName, r.Amount, r.MaxAmount }).ToArray();
        }
    }
    catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
    catch (Exception ex) when (ex is OperationCanceledException or IOException or TimeoutException or InvalidDataException or JsonException)
    {
        record["hostReachable"] = false;
        record["clockStatus"] = "hostUnavailable";
        record["error"] = ex.Message;
    }
    await writer.WriteLineAsync(JsonSerializer.Serialize(record, options));
    await writer.FlushAsync();
    try { await Task.Delay(TimeSpan.FromMilliseconds(500), stop.Token); } catch (OperationCanceledException) { }
}
Console.WriteLine("Depot observation complete.");
return 0;
