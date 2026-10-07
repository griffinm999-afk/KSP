using System.IO.Pipes;
using Expanse.Domain.Colonies;

namespace Expanse.Clock.Core;

// Read-only client for bridge proxies. Uses the established current-user pipe.
public sealed class SettlementJournalClient
{
    readonly string pipeName;
    public SettlementJournalClient(string pipeName) {ColonyManagementWire.ValidatePipeName(pipeName);this.pipeName=pipeName;}
    public async Task<SettlementPage> ReadAsync(SettlementReadRequest request, CancellationToken cancellationToken=default)
    {
        var envelope=new ColonyManagementWireRequest {RequestId=Guid.NewGuid().ToString("D"),Kind="settlements",Settlements=request};
        var encoded=ColonyManagementWire.Encode(envelope);
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var pipe=new NamedPipeClientStream(".",pipeName,PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
        await pipe.WriteAsync(BitConverter.GetBytes(encoded.Length),deadline.Token).ConfigureAwait(false);
        await pipe.WriteAsync(encoded,deadline.Token).ConfigureAwait(false);await pipe.FlushAsync(deadline.Token).ConfigureAwait(false);
        var header=new byte[4];await pipe.ReadExactlyAsync(header,deadline.Token).ConfigureAwait(false);int length=BitConverter.ToInt32(header);
        if(length<=0 || length>ColonyManagementWire.MaxFrameBytes)throw new InvalidDataException("Invalid settlement response frame size.");
        var bytes=new byte[length];await pipe.ReadExactlyAsync(bytes,deadline.Token).ConfigureAwait(false);
        var response=ColonyManagementWire.DecodeResponse(bytes);
        if(response.RequestId!=envelope.RequestId || response.Outcome!="settlements" || response.Settlements==null)throw new InvalidDataException("Settlement response identity or outcome differs.");
        return response.Settlements;
    }
}
