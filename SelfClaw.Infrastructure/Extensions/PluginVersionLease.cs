namespace SelfClaw.Infrastructure.Extensions;

internal sealed class PluginVersionLease : IDisposable
{
    private readonly Action _release;
    private int _disposed;

    public PluginVersionLease(Action release)
    {
        ArgumentNullException.ThrowIfNull(release);
        _release = release;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _release();
        }
    }
}
