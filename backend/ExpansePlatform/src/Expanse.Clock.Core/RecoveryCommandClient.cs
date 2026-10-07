using System.IO.Pipes;
using System.Text.Json;
using Expanse.Domain;

namespace Expanse.Clock.Core;

/// <summary>Typed client for submitting dev/Manager commands to the Host; this client owns no durable state.</summary>
public sealed class RecoveryCommandClient
{
    readonly string pipeName;
    public RecoveryCommandClient(string? pipeName = null) => this.pipeName = pipeName ?? EffectsProtocol.DefaultCommandPipeName;

    public async Task<SubmitCommandResult> SubmitCommandAsync(SubmitCommand request, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ProtocolVersion != 1 || request.MessageType != "submitCommand") throw new ArgumentException("Request must be a version 1 submitCommand.", nameof(request));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(3));
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(deadline.Token);
        await ClockProtocol.WriteFrameAsync(pipe, request, deadline.Token);
        var response = await ClockProtocol.ReadJsonAsync<SubmitCommandResult>(pipe, deadline.Token) ?? throw new InvalidDataException("Empty command response.");
        if (response.ProtocolVersion != 1 || response.MessageType != "submitCommandResult" || response.ClientRequestId != request.ClientRequestId || response.Status is not ("pending" or "accepted" or "rejected")) throw new InvalidDataException("Host returned an invalid command response.");
        return response;
    }

    public async Task<SubmitCommandResult> GetCommandStatusAsync(string clientRequestId, string worldId, string runId, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(clientRequestId) || clientRequestId.Length > 128) throw new ArgumentException("A bounded request ID is required.", nameof(clientRequestId));
        if (string.IsNullOrWhiteSpace(worldId) || worldId.Length > 128 || string.IsNullOrWhiteSpace(runId) || runId.Length > 128) throw new ArgumentException("A bounded active world and run are required.");
        var request = new GetCommandStatus { ProtocolVersion = 1, MessageType = "getCommandStatus", ClientRequestId = clientRequestId, WorldId = worldId, RunId = runId };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(3));
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(deadline.Token);
        await ClockProtocol.WriteFrameAsync(pipe, request, deadline.Token);
        var response = await ClockProtocol.ReadJsonAsync<SubmitCommandResult>(pipe, deadline.Token) ?? throw new InvalidDataException("Empty command status response.");
        if (response.ProtocolVersion != 1 || response.MessageType != "commandStatus" || response.ClientRequestId != clientRequestId || response.Status is not ("pending" or "accepted" or "faulted" or "rejected" or "historical" or "unknown")) throw new InvalidDataException("Host returned an invalid command status response.");
        if ((response.Status == "accepted" || response.Status == "faulted") && (response.AcceptedCapsule is null || response.StateHash is null || response.AcceptedRevision is null || response.AcceptedSequence is null)) throw new InvalidDataException("Accepted status must include the verified full capsule prefix.");
        return response;
    }

    public async Task<AcceptedStateQueryResult> GetAcceptedStateAsync(string worldId, string runId, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(worldId) || worldId.Length > 128 || string.IsNullOrWhiteSpace(runId) || runId.Length > 128) throw new ArgumentException("A bounded active world and run are required.");
        var request = new GetAcceptedState { ProtocolVersion = 1, MessageType = "getAcceptedState", WorldId = worldId, RunId = runId };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(3));
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(deadline.Token);
        await ClockProtocol.WriteFrameAsync(pipe, request, deadline.Token);
        var response = await ClockProtocol.ReadJsonAsync<AcceptedStateQueryResult>(pipe, deadline.Token) ?? throw new InvalidDataException("Empty accepted-state response.");
        if (response.ProtocolVersion != 1 || response.MessageType != "acceptedState" || response.WorldId != worldId || response.RunId != runId || response.Status is not ("available" or "unavailable")) throw new InvalidDataException("Host returned an invalid accepted-state response.");
        if (response.Status == "available")
        {
            if (response.AcceptedCapsule is null || response.StateHash is null || response.Revision is null || response.AcceptedSequence is null) throw new InvalidDataException("Available state projection is incomplete.");
            var state = AcceptedStateCodec.ReadCapsule(response.AcceptedCapsule);
            if (state.WorldId != worldId || state.Revision != response.Revision || state.AcceptedSequence != response.AcceptedSequence || AcceptedStateCodec.ComputeHash(state) != response.StateHash) throw new InvalidDataException("Host state projection failed capsule prefix/hash verification.");
            if (response.TransientShipmentHolds is null || response.TransientShipmentHolds.Length > AcceptedStateV2Limits.MaxActiveShipments ||
                response.TransientShipmentHolds.Any(x => x is null || string.IsNullOrWhiteSpace(x.ShipmentId) || x.ShipmentId.Length > 128 || string.IsNullOrWhiteSpace(x.Reason) || x.Reason.Length > 256 || !state.ActiveShipments.Any(s => s.ShipmentId == x.ShipmentId)) ||
                response.TransientShipmentHolds.Select(x => x.ShipmentId).Distinct(StringComparer.Ordinal).Count() != response.TransientShipmentHolds.Length)
                throw new InvalidDataException("Host returned invalid transient shipment holds.");
        }
        return response;
    }

    public async Task<DeliveryReadinessResult> GetDeliveryReadinessAsync(string worldId, string runId, string routeId, long routeVersion,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(worldId) || worldId.Length > 128 || string.IsNullOrWhiteSpace(runId) || runId.Length > 128 ||
            string.IsNullOrWhiteSpace(routeId) || routeId.Length > 128 || routeVersion <= 0)
            throw new ArgumentException("A bounded active world, run, and saved route are required.");
        var request = new GetDeliveryReadiness { ProtocolVersion = 1, MessageType = "getDeliveryReadiness", WorldId = worldId,
            RunId = runId, RouteId = routeId, RouteVersion = routeVersion };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(3));
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(deadline.Token);
        await ClockProtocol.WriteFrameAsync(pipe, request, deadline.Token);
        var response = await ClockProtocol.ReadJsonAsync<DeliveryReadinessResult>(pipe, deadline.Token) ?? throw new InvalidDataException("Empty delivery readiness response.");
        if (response.ProtocolVersion != 1 || response.MessageType != "deliveryReadiness" || response.Status is not ("ready" or "held") ||
            response.WorldId != worldId || response.RunId != runId || response.RouteId != routeId || response.RouteVersion != routeVersion ||
            response.Reason is null || response.Reason.Length > 256 || (response.Status == "ready" &&
                (response.AcceptedSequence is null || string.IsNullOrWhiteSpace(response.SourceProviderId) || string.IsNullOrWhiteSpace(response.DestinationProviderId))))
            throw new InvalidDataException("Host returned invalid delivery capability evidence.");
        return response;
    }
}
