using Microsoft.Extensions.Logging;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;

namespace SelfClaw.Desktop.Services.Subagents;

internal sealed class SubagentActivityChangeFeed : IDisposable
{
    private static readonly TimeSpan ContentInterval = TimeSpan.FromMilliseconds(120);
    private readonly ISubagentStateChangeNotifier _changes;
    private readonly ILogger _logger;
    private readonly object _notificationLock = new();
    private readonly HashSet<Guid> _dirtyParents = [];
    private readonly ITimer _timer;
    private bool _scheduled;
    private bool _disposed;

    internal SubagentActivityChangeFeed(ISubagentStateChangeNotifier changes, TimeProvider timeProvider, ILogger logger)
    {
        _changes = changes;
        _logger = logger;
        _timer = timeProvider.CreateTimer(_ => FlushChanges(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _changes.Changed += OnStateChanged;
    }

    internal event Action<Guid>? Changed;

    public void Dispose()
    {
        lock (_notificationLock)
        {
            _disposed = true;
            _dirtyParents.Clear();
            Changed = null;
        }

        _changes.Changed -= OnStateChanged;
        _timer.Dispose();
    }

    private void OnStateChanged(SubagentStateChange change)
    {
        lock (_notificationLock)
        {
            if (_disposed)
            {
                return;
            }

            _dirtyParents.Add(change.ParentConversationId);
            var immediate = change.Kind != SubagentStateChangeKind.ExecutionContent;
            if (!_scheduled || immediate)
            {
                _scheduled = true;
                _timer.Change(immediate ? TimeSpan.Zero : ContentInterval, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void FlushChanges()
    {
        Guid[] parents;
        Action<Guid>? handlers;
        lock (_notificationLock)
        {
            if (_disposed)
            {
                return;
            }

            parents = _dirtyParents.ToArray();
            _dirtyParents.Clear();
            _scheduled = false;
            handlers = Changed;
        }

        if (handlers is null)
        {
            return;
        }

        foreach (var parentId in parents)
        {
            foreach (Action<Guid> handler in handlers.GetInvocationList())
            {
                _ = Task.Run(() =>
                {
                    if (!Volatile.Read(ref _disposed))
                    {
                        handler(parentId);
                    }
                }).ContinueWith(failed => _logger.LogWarning(failed.Exception,
                    "Subagent activity observer failed. ParentConversationId={ParentConversationId}", parentId),
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
    }
}
