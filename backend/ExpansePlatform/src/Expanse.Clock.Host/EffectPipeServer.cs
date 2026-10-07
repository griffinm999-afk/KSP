using System.IO.Pipes;
using System.Text.Json;
using Expanse.Clock.Core;
using Expanse.Domain;

namespace Expanse.Clock.Host;

internal sealed class EffectPipeServer
{
    const int MaxFrameBytes = 65536;
    readonly string pipeName;
    readonly RecoveryCoordinator coordinator;
    readonly ClockState clock;
    public EffectPipeServer(string pipeName, RecoveryCoordinator coordinator, ClockState clock) { this.pipeName = pipeName; this.coordinator = coordinator; this.clock = clock; }

    public async Task RunAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try { await pipe.WaitForConnectionAsync(stop); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            Console.WriteLine("Effects bridge connected.");
            try
            {
                while (pipe.IsConnected && !stop.IsCancellationRequested)
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop); deadline.CancelAfter(TimeSpan.FromSeconds(35));
                    var bytes = await ClockProtocol.ReadFrameAsync(pipe, deadline.Token);
                    var response = Dispatch(bytes);
                    await ClockProtocol.WriteFrameAsync(pipe, response, deadline.Token);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception ex) when (!stop.IsCancellationRequested) { Console.Error.WriteLine($"Effects bridge disconnected: {ex.Message}"); }
        }
    }

    object Dispatch(byte[] bytes)
    {
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("protocolVersion", out var version) || version.ValueKind != JsonValueKind.Number || version.GetInt32() != 1 || !doc.RootElement.TryGetProperty("messageType", out var type) || type.ValueKind != JsonValueKind.String)
                return new EffectNeedAttach { ProtocolVersion = 1, MessageType = "effectNeedAttach", Reason = "Malformed or unsupported effects envelope." };
            var messageType = type.GetString();
            if (messageType == "effectAttach")
            {
                var request = JsonSerializer.Deserialize<EffectAttach>(bytes, ClockProtocol.JsonOptions);
                return request is null ? new EffectNeedAttach { ProtocolVersion = 1, MessageType = "effectNeedAttach", Reason = "Empty attach." } : coordinator.Attach(request, clock);
            }
            if (messageType == "effectPoll")
            {
                var request = JsonSerializer.Deserialize<EffectPoll>(bytes, ClockProtocol.JsonOptions);
                return request is null ? new EffectNeedAttach { ProtocolVersion = 1, MessageType = "effectNeedAttach", Reason = "Empty poll." } : coordinator.Poll(request, clock);
            }
            if (messageType == "effectReceipt")
            {
                var request = JsonSerializer.Deserialize<EffectReceipt>(bytes, ClockProtocol.JsonOptions);
                return request is null ? new EffectNeedAttach { ProtocolVersion = 1, MessageType = "effectNeedAttach", Reason = "Empty receipt." } : coordinator.Receipt(request, clock);
            }
            return new EffectNeedAttach { ProtocolVersion = 1, MessageType = "effectNeedAttach", Reason = "Unknown effects message type." };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or InvalidDataException or FormatException)
        { return new EffectNeedAttach { ProtocolVersion = 1, MessageType = "effectNeedAttach", Reason = ex.Message.Length > 256 ? ex.Message[..256] : ex.Message }; }
    }
}

internal sealed class CommandPipeServer
{
    readonly string name; readonly RecoveryCoordinator coordinator;
    public CommandPipeServer(string name, RecoveryCoordinator coordinator) { this.name = name; this.coordinator = coordinator; }
    public async Task RunAsync(CancellationToken stop)
    {
        var slots = new SemaphoreSlim(8, 8); var handlers = new List<Task>();
        while (!stop.IsCancellationRequested)
        {
            try { await slots.WaitAsync(stop); } catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 8, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stop); var client = pipe; pipe = null;
                var task = Serve(client, coordinator, slots, stop); handlers.Add(task);
                handlers.RemoveAll(t => t.IsCompleted);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { if (pipe is not null) await pipe.DisposeAsync(); slots.Release(); break; }
            catch (Exception ex) { if (pipe is not null) await pipe.DisposeAsync(); slots.Release(); if (!stop.IsCancellationRequested) { Console.Error.WriteLine($"Command listener recovered: {ex.Message}"); await Task.Delay(200, stop); } }
        }
        try { await Task.WhenAll(handlers); } catch (OperationCanceledException) { }
    }
    static async Task Serve(NamedPipeServerStream pipe, RecoveryCoordinator coordinator, SemaphoreSlim slots, CancellationToken stop)
    {
        try
        {
            await using (pipe)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop); deadline.CancelAfter(TimeSpan.FromSeconds(5));
                using var document = await ClockProtocol.ReadJsonAsync<JsonDocument>(pipe, deadline.Token) ?? throw new InvalidDataException("Empty command frame.");
                if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("protocolVersion", out var version) || version.ValueKind != JsonValueKind.Number || version.GetInt32() != 1 || !document.RootElement.TryGetProperty("messageType", out var type) || type.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("Unsupported command envelope.");
                object result;
                if (type.GetString() == "submitCommand")
                {
                    var request = JsonSerializer.Deserialize<SubmitCommand>(document.RootElement.GetRawText(), ClockProtocol.JsonOptions) ?? throw new InvalidDataException("Empty command request.");
                    result = coordinator.Submit(request);
                }
                else if (type.GetString() == "getCommandStatus")
                {
                    var request = JsonSerializer.Deserialize<GetCommandStatus>(document.RootElement.GetRawText(), ClockProtocol.JsonOptions) ?? throw new InvalidDataException("Empty command status request.");
                    result = coordinator.GetCommandStatus(request);
                }
                else if (type.GetString() == "getAcceptedState")
                {
                    var request = JsonSerializer.Deserialize<GetAcceptedState>(document.RootElement.GetRawText(), ClockProtocol.JsonOptions) ?? throw new InvalidDataException("Empty accepted-state request.");
                    result = coordinator.GetAcceptedState(request);
                }
                else if (type.GetString() == "getDeliveryReadiness")
                {
                    var request = JsonSerializer.Deserialize<GetDeliveryReadiness>(document.RootElement.GetRawText(), ClockProtocol.JsonOptions) ?? throw new InvalidDataException("Empty delivery readiness request.");
                    result = coordinator.GetDeliveryReadiness(request);
                }
                else throw new InvalidDataException("Unsupported command envelope.");
                await ClockProtocol.WriteFrameAsync(pipe, result, deadline.Token);
            }
        }
        catch (Exception ex) when (!stop.IsCancellationRequested) { Console.Error.WriteLine($"Command request rejected: {ex.Message}"); }
        finally { slots.Release(); }
    }
}
