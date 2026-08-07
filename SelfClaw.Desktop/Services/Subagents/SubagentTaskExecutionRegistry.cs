using System.Collections.Concurrent;

namespace SelfClaw.Desktop.Services.Subagents;

internal sealed class SubagentTaskExecutionRegistry
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();
    private readonly ConcurrentDictionary<Guid, byte> _requested = new();

    internal void Register(Guid taskId, CancellationTokenSource cancellation)
    {
        ArgumentNullException.ThrowIfNull(cancellation);
        if (!_running.TryAdd(taskId, cancellation))
        {
            throw new InvalidOperationException($"Subagent task '{taskId}' is already registered as running.");
        }

        if (_requested.ContainsKey(taskId))
        {
            cancellation.Cancel();
        }
    }

    internal void RequestCancellation(Guid taskId)
    {
        _requested[taskId] = 0;
        if (_running.TryGetValue(taskId, out var cancellation))
        {
            cancellation.Cancel();
        }
    }

    internal bool IsCancellationRequested(Guid taskId) => _requested.ContainsKey(taskId);

    internal void ClearCancellationRequest(Guid taskId) => _requested.TryRemove(taskId, out _);

    internal void Unregister(Guid taskId)
    {
        _running.TryRemove(taskId, out _);
        _requested.TryRemove(taskId, out _);
    }
}
