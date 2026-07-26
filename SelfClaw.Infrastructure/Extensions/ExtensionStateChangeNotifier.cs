using SelfClaw.Core.Interfaces;

namespace SelfClaw.Infrastructure.Extensions;

internal sealed class ExtensionStateChangeNotifier : IExtensionStateChangeNotifier
{
    private long _revision;

    public long CurrentRevision => Interlocked.Read(ref _revision);

    public event Action<long>? StateChanged;

    public long Advance()
    {
        var revision = Interlocked.Increment(ref _revision);
        Publish(revision);
        return revision;
    }

    public long AdvanceTo(long revision)
    {
        while (true)
        {
            var current = CurrentRevision;
            if (revision <= current)
            {
                return current;
            }

            if (Interlocked.CompareExchange(ref _revision, revision, current) == current)
            {
                Publish(revision);
                return revision;
            }
        }
    }

    private void Publish(long revision)
    {
        var handlers = StateChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (Action<long> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(revision);
            }
            catch
            {
                // State mutation and runtime health updates must not depend on UI subscribers.
            }
        }
    }
}
