using System.Threading.Channels;

namespace Sokna.PrintAgent.Service;

public sealed class PrintWakeSignal
{
    private readonly Channel<byte> _channel=Channel.CreateBounded<byte>(new BoundedChannelOptions(1){FullMode=BoundedChannelFullMode.DropWrite,SingleReader=true,SingleWriter=false});
    public void Pulse()=>_channel.Writer.TryWrite(1);
    public async Task WaitOrDelayAsync(TimeSpan delay,CancellationToken ct)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(delay);
        try{await _channel.Reader.ReadAsync(timeout.Token);while(_channel.Reader.TryRead(out _)){} }
        catch(OperationCanceledException) when(!ct.IsCancellationRequested){}
    }
}
