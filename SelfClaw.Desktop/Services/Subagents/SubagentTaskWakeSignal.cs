using System.Threading.Channels;

namespace SelfClaw.Desktop.Services.Subagents;

internal sealed class SubagentTaskWakeSignal
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        SingleWriter = false
    });

    internal void Signal() => _channel.Writer.TryWrite(true);

    internal async Task WaitAsync(
        TimeSpan maximumDelay,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(maximumDelay, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await _channel.Reader.ReadAsync(linked.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
        }

        while (_channel.Reader.TryRead(out _))
        {
        }
    }
}
