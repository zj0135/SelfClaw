namespace SelfClaw.Infrastructure.Extensions;

internal sealed class PluginVersionDrainLease : IAsyncDisposable
{
    private readonly Action _release;
    private int _disposed;

    public PluginVersionDrainLease(Action release)
    {
        _release = release;
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _release();
        }

        return ValueTask.CompletedTask;
    }
}
