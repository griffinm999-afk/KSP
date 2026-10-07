using System.IO;
using System.IO.Pipes;
using Expanse.Domain.Colonies;

namespace Expanse.Clock.Manager;

public sealed class ColonyManagementClient
{
    private readonly string pipeName;
    public ColonyManagementClient(string pipeName) { ColonyManagementWire.ValidatePipeName(pipeName); this.pipeName = pipeName; }
    public Task<ColonyManagementWireResponse> SnapshotAsync(CancellationToken token = default) => ExchangeAsync(new()
        { RequestId=Guid.NewGuid().ToString("D"), Kind="snapshot" }, TimeSpan.FromSeconds(3),token);
    public Task<ColonyManagementWireResponse> SubmitAsync(ColonyCommand command, CancellationToken token = default) => ExchangeAsync(new()
        { RequestId=Guid.NewGuid().ToString("D"), Kind="submit", Command=command },TimeSpan.FromSeconds(18),token);
    private async Task<ColonyManagementWireResponse> ExchangeAsync(ColonyManagementWireRequest request, TimeSpan timeout, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(timeout);
        using var pipe = new NamedPipeClientStream(".",pipeName,PipeDirection.InOut,PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
        var encoded = ColonyManagementWire.Encode(request);
        await pipe.WriteAsync(BitConverter.GetBytes(encoded.Length),deadline.Token).ConfigureAwait(false);
        await pipe.WriteAsync(encoded,deadline.Token).ConfigureAwait(false);
        await pipe.FlushAsync(deadline.Token).ConfigureAwait(false);
        var header = new byte[4]; await pipe.ReadExactlyAsync(header,deadline.Token).ConfigureAwait(false);
        int length = BitConverter.ToInt32(header);
        if (length <= 0 || length > ColonyManagementWire.MaxFrameBytes) throw new InvalidDataException("Invalid colony response frame length.");
        var bytes = new byte[length]; await pipe.ReadExactlyAsync(bytes,deadline.Token).ConfigureAwait(false);
        var response = ColonyManagementWire.DecodeResponse(bytes);
        if (response.RequestId != request.RequestId) throw new InvalidDataException("Colony response identity does not match its request.");
        return response;
    }
}
