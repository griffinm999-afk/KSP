using System.Text.Json;
using Expanse.Clock.Core;

var duration = 120;
string? output = null;
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--duration-seconds" && i + 1 < args.Length && int.TryParse(args[++i], out var seconds)) duration = seconds;
    else if (args[i] == "--output" && i + 1 < args.Length) output = args[++i];
    else { Console.Error.WriteLine("Usage: Expanse.Clock.Observer [--duration-seconds 1..600] [--output absolute-jsonl-path]"); return 2; }
}
if (duration is < 1 or > 600) { Console.Error.WriteLine("Duration must be between 1 and 600 seconds."); return 2; }
output ??= Path.Combine(AppContext.BaseDirectory, "clock-observer-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".jsonl");
output = Path.GetFullPath(output);
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
Console.WriteLine($"Observing Host for {duration}s; JSONL: {output}");
var client = new ClockViewClient();
var end = DateTimeOffset.UtcNow.AddSeconds(duration);
using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(duration));
var options = new JsonSerializerOptions(ClockProtocol.JsonOptions) { WriteIndented = false };
await using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
await using var writer = new StreamWriter(file);
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
while (!stop.IsCancellationRequested && DateTimeOffset.UtcNow < end)
{
    var record = new Dictionary<string, object?> { ["observedAtUtc"] = DateTimeOffset.UtcNow };
    try
    {
        var view = await client.GetSnapshotAsync(TimeSpan.FromSeconds(2), stop.Token);
        record["hostReachable"] = true; record["status"] = view.Status; record["ageSeconds"] = view.AgeSeconds; record["publisherConnected"] = view.PublisherConnected;
        if (view.Sample is { } sample)
        {
            record["protocolVersion"] = view.ProtocolVersion; record["messageType"] = view.MessageType;
            record["sequence"] = sample.Sequence; record["sessionId"] = sample.SessionId; record["loadEpoch"] = sample.LoadEpoch;
            record["installNamespace"] = sample.InstallNamespace; record["saveFolder"] = sample.SaveFolder; record["saveTitle"] = sample.SaveTitle;
            record["utSeconds"] = sample.UtSeconds; record["activeWorld"] = sample.ActiveWorld; record["scene"] = sample.Scene;
            record["paused"] = sample.Paused; record["formattedDate"] = sample.FormattedDate; record["warpRate"] = sample.WarpRate;
        }
    }
    catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
    catch (OperationCanceledException ex)
    { record["hostReachable"] = false; record["status"] = "hostUnavailable"; record["error"] = ex.Message; }
    catch (Exception ex) when (ex is IOException or TimeoutException or InvalidDataException or System.Text.Json.JsonException)
    { record["hostReachable"] = false; record["status"] = "hostUnavailable"; record["error"] = ex.Message; }
    await writer.WriteLineAsync(JsonSerializer.Serialize(record, options)); await writer.FlushAsync();
    try { await Task.Delay(TimeSpan.FromMilliseconds(500), stop.Token); } catch (OperationCanceledException) { }
}
Console.WriteLine("Observation complete.");
return 0;
