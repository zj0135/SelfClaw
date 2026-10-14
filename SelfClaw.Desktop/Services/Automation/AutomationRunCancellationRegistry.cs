using System.Collections.Concurrent;

namespace SelfClaw.Desktop.Services.Automation;

internal sealed class AutomationRunCancellationRegistry
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _sources = new();

    internal CancellationToken BeginRun(Guid runId)
    {
        var source = new CancellationTokenSource();
        _sources[runId] = source;
        return source.Token;
    }

    internal void CompleteRun(Guid runId)
    {
        if (_sources.TryRemove(runId, out var source))
        {
            source.Dispose();
        }
    }

    internal void CancelAll()
    {
        foreach (var source in _sources.Values)
        {
            try
            {
                source.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
