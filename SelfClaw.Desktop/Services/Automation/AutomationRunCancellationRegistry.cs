using System.Collections.Concurrent;

namespace SelfClaw.Desktop.Services.Automation;

internal sealed class AutomationRunCancellationRegistry
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _sources = new();
    private readonly ConcurrentDictionary<Guid, byte> _userRequests = new();

    internal CancellationToken BeginRun(Guid runId)
    {
        var source = new CancellationTokenSource();
        _sources[runId] = source;
        if (_userRequests.ContainsKey(runId))
        {
            Cancel(source);
        }

        return source.Token;
    }

    internal void CancelRun(Guid runId)
    {
        _userRequests[runId] = 0;
        if (_sources.TryGetValue(runId, out var source))
        {
            Cancel(source);
        }
    }

    /// <summary>
    /// Whether the cancellation was requested by the user rather than by application shutdown. Only the
    /// former becomes <c>Cancelled(CancelledByUser)</c>; shutdown is reported by the host as
    /// <c>Cancelled(ApplicationStopping)</c>.
    /// </summary>
    internal bool IsUserCancellationRequested(Guid runId) => _userRequests.ContainsKey(runId);

    internal void CompleteRun(Guid runId)
    {
        if (_sources.TryRemove(runId, out var source))
        {
            source.Dispose();
        }

        _userRequests.TryRemove(runId, out _);
    }

    internal void CancelAll()
    {
        foreach (var source in _sources.Values)
        {
            Cancel(source);
        }
    }

    private static void Cancel(CancellationTokenSource source)
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
