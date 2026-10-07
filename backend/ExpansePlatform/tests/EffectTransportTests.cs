using System.IO.Pipes;
using System.Text.Json;
using Expanse.Clock.Core;
using Expanse.Clock.Host;
using Expanse.Domain;

namespace Expanse.Clock.Tests;

public sealed class EffectTransportTests
{
    sealed class TestProcessVerifier : IBridgeProcessVerifier
    {
        public ProcessIdentityState Verify(int processId, long startUtcTicks, string executablePath, string installNamespace, out string reason)
        { reason = "synthetic process verified"; return ProcessIdentityState.SameLiveProcess; }
    }

    [Fact]
    public async Task FramedEffectsPipeAttachesPreparesProposesAndVerifiesFullReceipt()
    {
        var token = Guid.NewGuid().ToString("N"); var pipeName = "Expanse.Effect.Test." + token;
        var database = Path.Combine(Path.GetTempPath(), "ExpanseEffectPipeTest-" + token); Directory.CreateDirectory(database);
        var session = Guid.NewGuid(); var epoch = Guid.NewGuid(); var install = @"C:\KSP"; var save = "Synthetic";
        var state = new AcceptedState { WorldId = Guid.NewGuid().ToString("D"), CheckpointId = Guid.NewGuid().ToString("N") };
        var clock = new ClockState(); clock.SetPublisherConnected(true);
        clock.Accept(new ClockSample(1, "clockSample", 1, session, epoch, install, save, "Synthetic", 100, true, "Flight", false, "Y1", 1, WorldId: state.WorldId, RunId: "test-run"));
        try
        {
            using var coordinator = new RecoveryCoordinator(database, new TestProcessVerifier(), enableDevCounter: true);
            using var stop = new CancellationTokenSource(); var server = new EffectPipeServer(pipeName, coordinator, clock); var serverTask = server.RunAsync(stop.Token);
            await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await client.ConnectAsync(timeout.Token);
            var attach = new EffectAttach { ProtocolVersion = 1, MessageType = "effectAttach", SessionId = session, LoadEpoch = epoch, BridgeProcessId = 42, BridgeProcessStartUtcTicks = 638946720000000000, BridgeExecutablePath = @"C:\KSP\KSP_x64.exe", InstallNamespace = install, SaveFolder = save, WorldId = state.WorldId, RunId = "test-run", CheckpointId = state.CheckpointId, StateHash = AcceptedStateCodec.ComputeHash(state), CanWrite = true, Capabilities = Array.Empty<string>(), Capsule = AcceptedStateCodec.CreateCapsule(state) };
            await ClockProtocol.WriteFrameAsync(client, attach, timeout.Token);
            Assert.IsType<EffectIdle>(await ReadResponse(client, timeout.Token));

            var request = new SubmitCommand { ProtocolVersion = 1, MessageType = "submitCommand", ClientRequestId = "pipe-test-command", WorldId = state.WorldId, RunId = "test-run", CommandKind = "counterIncrement", CounterDelta = 1 };
            Assert.Equal("pending", coordinator.Submit(request).Status);
            var poll = new EffectPoll { ProtocolVersion = 1, MessageType = "effectPoll", SessionId = session, LoadEpoch = epoch, InstallNamespace = install, SaveFolder = save, WorldId = state.WorldId, RunId = "test-run", CheckpointId = state.CheckpointId, Revision = state.Revision, AcceptedSequence = state.AcceptedSequence, CompactionWatermark = state.CompactionWatermark, StateHash = AcceptedStateCodec.ComputeHash(state) };
            await ClockProtocol.WriteFrameAsync(client, poll, timeout.Token);
            var proposal = Assert.IsType<EffectProposal>(await ReadResponse(client, timeout.Token));
            Assert.Equal("counterIncrement", proposal.OperationKind); Assert.Equal(OperationIdentity.Create(state.WorldId, 1, request.ClientRequestId), proposal.OperationId);
            var result = AcceptedStateCodec.Increment(state, state.WorldId, proposal.OperationId, proposal.ClientRequestId, proposal.CommandSequence, proposal.PayloadHash, proposal.CounterDelta, 102.5);
            var receipt = new EffectReceipt { ProtocolVersion = 1, MessageType = "effectReceipt", SessionId = session, LoadEpoch = epoch, InstallNamespace = install, SaveFolder = save, WorldId = state.WorldId, RunId = "test-run", OperationId = proposal.OperationId, ClientRequestId = proposal.ClientRequestId, CommandSequence = proposal.CommandSequence, PayloadHash = proposal.PayloadHash, OperationKind = proposal.OperationKind, Outcome = "accepted", AppliedUt = 102.5, ActualCounterDelta = 1, AcceptedRevision = result.State.Revision, AcceptedSequence = result.State.AcceptedSequence, StateHash = AcceptedStateCodec.ComputeHash(result.State), AcceptedCapsule = AcceptedStateCodec.CreateCapsule(result.State) };
            await ClockProtocol.WriteFrameAsync(client, receipt, timeout.Token);
            var ack = Assert.IsType<EffectIdle>(await ReadResponse(client, timeout.Token)); Assert.Contains("verified", ack.Reason, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                stop.Cancel(); await client.DisposeAsync();
                try { await serverTask.WaitAsync(TimeSpan.FromSeconds(3)); } catch (OperationCanceledException) { } catch (TimeoutException) { }
            }
        }
        finally { Directory.Delete(database, true); }
    }

    static async Task<object> ReadResponse(Stream stream, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(await ClockProtocol.ReadFrameAsync(stream, cancellationToken));
        var root = document.RootElement; Assert.Equal(1, root.GetProperty("protocolVersion").GetInt32());
        return root.GetProperty("messageType").GetString() switch
        {
            "effectProposal" => JsonSerializer.Deserialize<EffectProposal>(root.GetRawText(), ClockProtocol.JsonOptions)!,
            "effectNeedAttach" => JsonSerializer.Deserialize<EffectNeedAttach>(root.GetRawText(), ClockProtocol.JsonOptions)!,
            "effectIdle" => JsonSerializer.Deserialize<EffectIdle>(root.GetRawText(), ClockProtocol.JsonOptions)!,
            _ => throw new InvalidDataException("Unexpected effects response type.")
        };
    }
}
