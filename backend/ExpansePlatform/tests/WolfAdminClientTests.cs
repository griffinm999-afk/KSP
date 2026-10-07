using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using Expanse.Clock.Core;

namespace Expanse.Clock.Tests;

public sealed class WolfAdminClientTests
{
    private static WolfSetIncomingRequest Change() => new()
    {
        RequestId = Guid.NewGuid().ToString("N"), WorldId = "world-1", RunId = "run-1",
        SessionId = Guid.NewGuid(), LoadEpoch = Guid.NewGuid(), Body = "Minmus",
        Biome = "Greater Flats", Resource = "Power", ExpectedIncoming = 35,
        ExpectedOutgoing = 35, TargetIncoming = 85
    };

    private static string PreviewPipe(string customCommandPipe)
    {
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(customCommandPipe)))[..24];
        return $"ExpanseFoundations.WOLF.Admin.preview.{Environment.UserName}.{suffix}";
    }

    [Fact]
    public async Task CustomEndpointSendsExactChangeAndAcceptsCorrelatedReceipt()
    {
        string custom = "Expanse.Preview.Command." + Guid.NewGuid().ToString("N");
        var request = Change();
        using var server = new NamedPipeServerStream(PreviewPipe(custom), PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var serve = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            var received = await ClockProtocol.ReadJsonAsync<WolfSetIncomingRequest>(server, CancellationToken.None);
            Assert.Equal(request, received);
            await ClockProtocol.WriteFrameAsync(server, new WolfSetIncomingResult
            {
                RequestId = request.RequestId, Status = "applied", Incoming = 85,
                Outgoing = 35, Available = 50
            }, CancellationToken.None);
        });
        var actual = await new WolfAdminClient(custom).SetIncomingAsync(request, TimeSpan.FromSeconds(3));
        await serve;
        Assert.Equal(50, actual.Available);
    }

    [Fact]
    public async Task WrongRequestIdCannotBeMistakenForAnAppliedChange()
    {
        string custom = "Expanse.Preview.Command." + Guid.NewGuid().ToString("N");
        var request = Change();
        using var server = new NamedPipeServerStream(PreviewPipe(custom), PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var serve = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            _ = await ClockProtocol.ReadJsonAsync<WolfSetIncomingRequest>(server, CancellationToken.None);
            await ClockProtocol.WriteFrameAsync(server, new WolfSetIncomingResult
            {
                RequestId = "another-request", Status = "applied", Incoming = 85,
                Outgoing = 35, Available = 50
            }, CancellationToken.None);
        });
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new WolfAdminClient(custom).SetIncomingAsync(request, TimeSpan.FromSeconds(3)));
        await serve;
    }

    [Fact]
    public async Task OutOfRangeTargetFailsBeforeAnyPipeConnection()
    {
        var request = Change() with { TargetIncoming = 1_000_001 };
        await Assert.ThrowsAsync<ArgumentException>(() =>
            new WolfAdminClient("isolated-test").SetIncomingAsync(request));
    }
}
