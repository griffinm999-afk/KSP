using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Expanse.Clock.Core;

// Changes WOLF's biome ledger through the active game, never through a save-file edit.
public sealed record WolfSetIncomingRequest
{
    public string RequestId { get; init; } = "";
    public string WorldId { get; init; } = "";
    public string RunId { get; init; } = "";
    public Guid SessionId { get; init; }
    public Guid LoadEpoch { get; init; }
    public string Body { get; init; } = "";
    public string Biome { get; init; } = "";
    public string Resource { get; init; } = "";
    public int ExpectedIncoming { get; init; }
    public int ExpectedOutgoing { get; init; }
    public int TargetIncoming { get; init; }
}

public sealed record WolfSetIncomingResult
{
    public string RequestId { get; init; } = "";
    public string Status { get; init; } = "rejected";
    public string? Reason { get; init; }
    public int Incoming { get; init; }
    public int Outgoing { get; init; }
    public int Available { get; init; }
}

public sealed class WolfAdminClient
{
    public static string DefaultPipeName => $"ExpanseFoundations.WOLF.Admin.v1.{Environment.UserName}";
    private readonly string _pipeName;

    public WolfAdminClient(string? commandPipeName = null)
    {
        if (commandPipeName is null || commandPipeName == EffectsProtocol.DefaultCommandPipeName)
            _pipeName = DefaultPipeName;
        else
        {
            // Every preview/custom command endpoint gets its own unreachable WOLF
            // endpoint; a preview must never fall through to a live game writer.
            string suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(commandPipeName)))[..24];
            _pipeName = $"ExpanseFoundations.WOLF.Admin.preview.{Environment.UserName}.{suffix}";
        }
    }

    public async Task<WolfSetIncomingResult> SetIncomingAsync(WolfSetIncomingRequest request,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length > 100 ||
            string.IsNullOrWhiteSpace(request.WorldId) || request.WorldId.Length > 128 ||
            string.IsNullOrWhiteSpace(request.RunId) || request.RunId.Length > 128 ||
            request.SessionId == Guid.Empty || request.LoadEpoch == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.Body) || request.Body.Length > 64 ||
            string.IsNullOrWhiteSpace(request.Biome) || request.Biome.Length > 64 ||
            string.IsNullOrWhiteSpace(request.Resource) || request.Resource.Length > 64 || request.ExpectedIncoming < 0 ||
            request.ExpectedOutgoing < 0 || request.TargetIncoming < request.ExpectedOutgoing ||
            request.TargetIncoming > 1_000_000)
            throw new ArgumentException("Invalid WOLF ledger change.", nameof(request));
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout ?? TimeSpan.FromSeconds(8));
        using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await pipe.ConnectAsync(limit.Token);
        await ClockProtocol.WriteFrameAsync(pipe, request, limit.Token);
        var result = await ClockProtocol.ReadJsonAsync<WolfSetIncomingResult>(pipe, limit.Token);
        if (result is null || result.RequestId != request.RequestId ||
            result.Status is not ("applied" or "duplicate" or "rejected") ||
            result.Incoming < 0 || result.Outgoing < 0 || result.Available != result.Incoming - result.Outgoing)
            throw new InvalidDataException("WOLF admin response was invalid.");
        if (result.Status is "applied" or "duplicate" &&
            (result.Incoming != request.TargetIncoming || result.Outgoing != request.ExpectedOutgoing))
            throw new InvalidDataException("WOLF admin receipt disagrees with the requested change.");
        return result;
    }
}
