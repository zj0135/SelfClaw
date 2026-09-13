using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Desktop.Services.Subagents.Models;

namespace SelfClaw.Desktop.Services.Subagents;

internal sealed class SubagentActivityService : IDisposable
{
    private readonly ISubagentActivityReader _reader;
    private readonly SubagentActivityRegistry _registry;
    private readonly ISubagentStateChangeNotifier _changes;
    private readonly DesktopToolApprovalHandler _approvals;
    private readonly SubagentActivityChangeFeed _notifications;
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, byte> _closedParents = new();
    private readonly Dictionary<SubagentActivityQuery, SubagentActivityPage> _pages = [];
    private readonly Dictionary<Guid, SubagentActivityDetail> _details = [];
    private long _cacheRevision = -1;
    private long _cacheScopeRevision = -1;
    private long _scopeRevision;
    private bool _disposed;

    public SubagentActivityService(ISubagentActivityReader reader, SubagentActivityRegistry registry,
        ISubagentStateChangeNotifier changes, DesktopToolApprovalHandler approvals, ILogger<SubagentActivityService> logger)
        : this(reader, registry, changes, approvals, TimeProvider.System, logger)
    {
    }

    internal SubagentActivityService(ISubagentActivityReader reader, SubagentActivityRegistry registry,
        ISubagentStateChangeNotifier changes, DesktopToolApprovalHandler approvals, TimeProvider timeProvider,
        ILogger<SubagentActivityService> logger)
    {
        _reader = reader;
        _registry = registry;
        _changes = changes;
        _approvals = approvals;
        _notifications = new SubagentActivityChangeFeed(changes, timeProvider, logger);
        _approvals.ApprovalRequested += OnApprovalRequested;
        _approvals.ApprovalCompleted += OnApprovalCompleted;
    }

    internal event Action<Guid>? Changed
    {
        add => _notifications.Changed += value;
        remove => _notifications.Changed -= value;
    }

    internal long PersistenceRevision => _changes.PersistenceRevision;

    internal bool IsScopeClosed(Guid parentConversationId) => _closedParents.ContainsKey(parentConversationId);

    internal async Task<SubagentActivityPageSnapshot> ListAsync(SubagentActivityQuery query,
        bool refresh = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                EnsureOpen(query.ParentConversationId);
                RefreshCache(refresh);
                refresh = false;
                if (!_pages.TryGetValue(query, out var page))
                {
                    page = await _reader.ListAsync(query, cancellationToken).ConfigureAwait(false);
                    _pages[query] = page;
                }

                if (CacheChanged())
                {
                    continue;
                }

                EnsureOpen(query.ParentConversationId);
                var activities = page.Tasks.Select(task => CreateActivity(task, _registry.GetActivity(task.TaskId))).ToArray();
                return new SubagentActivityPageSnapshot(page.Counts, page.ListVersion, activities, page.NextCursor);
            }
        }
        finally
        {
            RefreshCache(Volatile.Read(ref _disposed));
            _readGate.Release();
        }
    }

    internal async Task<SubagentActivitySnapshot?> GetDetailAsync(Guid parentConversationId, Guid taskId,
        bool refresh = false, CancellationToken cancellationToken = default)
    {
        await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadDetailCoreAsync(parentConversationId, taskId, refresh, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            RefreshCache(Volatile.Read(ref _disposed));
            _readGate.Release();
        }
    }

    internal async Task<SubagentContentPage?> ReadContentAsync(SubagentContentQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var snapshot = await GetDetailAsync(query.ParentConversationId, query.TaskId, cancellationToken: cancellationToken).ConfigureAwait(false);
        return snapshot is null ? null : SubagentActivityContent.Read(snapshot.Activity.Task, snapshot.Content, query);
    }

    internal void SetScopeClosed(Guid parentConversationId, bool closed)
    {
        if (closed)
        {
            _closedParents[parentConversationId] = 0;
        }
        else
        {
            _closedParents.TryRemove(parentConversationId, out _);
        }

        _registry.ClearParent(parentConversationId);
        Interlocked.Increment(ref _scopeRevision);
        InvalidateIdleCache();
        _changes.Publish(parentConversationId, null, closed ? SubagentStateChangeKind.ScopeClosed : SubagentStateChangeKind.ScopeOpened);
    }

    public void Dispose()
    {
        Volatile.Write(ref _disposed, true);
        _notifications.Dispose();
        _approvals.ApprovalRequested -= OnApprovalRequested;
        _approvals.ApprovalCompleted -= OnApprovalCompleted;
        InvalidateIdleCache();
    }

    private async Task<SubagentActivitySnapshot?> ReadDetailCoreAsync(Guid parentConversationId, Guid taskId,
        bool refresh, CancellationToken cancellationToken)
    {
        while (true)
        {
            EnsureOpen(parentConversationId);
            RefreshCache(refresh);
            refresh = false;
            if (!_details.TryGetValue(taskId, out var detail))
            {
                detail = await _reader.GetDetailAsync(parentConversationId, taskId, cancellationToken).ConfigureAwait(false);
                if (detail is null)
                {
                    return null;
                }

                _details[taskId] = detail;
            }

            if (detail.Task.ParentConversationId != parentConversationId)
            {
                return null;
            }

            var live = IsActive(detail.Task)
                ? await _registry.CaptureSnapshotAsync(taskId, cancellationToken).ConfigureAwait(false)
                : null;
            if (CacheChanged())
            {
                continue;
            }

            if (live?.Activity.TerminalObserved == true)
            {
                detail = await _reader.GetDetailAsync(parentConversationId, taskId, cancellationToken).ConfigureAwait(false);
                if (detail is null)
                {
                    return null;
                }

                _details[taskId] = detail;
                if (CacheChanged())
                {
                    continue;
                }
            }

            EnsureOpen(parentConversationId);
            return BuildSnapshot(detail, live);
        }
    }

    private SubagentActivitySnapshot BuildSnapshot(SubagentActivityDetail detail, SubagentExecutionSnapshot? live)
    {
        if (!IsActive(detail.Task))
        {
            _registry.ClearRecordingFailure(detail.Task.TaskId);
            return new SubagentActivitySnapshot(CreateActivity(detail.Task, null), detail.Content, "persisted");
        }

        var activity = CreateActivity(detail.Task, live?.Activity);
        if (live is null)
        {
            return new SubagentActivitySnapshot(activity, detail.Content, "persisted");
        }

        var message = live.Message is null ? null : live.Message with { Status = MessageStatus.Streaming, ErrorMessage = null };
        var content = SubagentActivityContent.Create(activity.Task.Status, detail.Content.TaskText, message, live.ToolRuns);
        return new SubagentActivitySnapshot(activity, content, "live");
    }

    private SubagentTaskActivity CreateActivity(SubagentActivityTask task, SubagentExecutionActivity? live)
    {
        if (!IsActive(task))
        {
            return new SubagentTaskActivity(task, task.Status.ToString().ToLowerInvariant(), 0, null);
        }

        var pendingCount = _approvals.GetPendingRequests().Count(request => request.ConversationId == task.ChildConversationId);
        var error = live?.RecordingError ?? _registry.GetRecordingError(task.TaskId);
        if (live?.TerminalObserved == true)
        {
            error ??= "The task terminal state has not been confirmed in storage.";
        }

        var phase = error is not null ? "recording-error"
            : task.CancelRequestedAtUtc is not null ? "cancelling"
            : pendingCount > 0 ? "waiting-approval"
            : task.Status == SubagentTaskStatus.Queued ? "queued"
            : live?.Phase ?? "synchronizing";
        task = task with
        {
            ModelDisplayName = live?.Model ?? task.ModelDisplayName,
            InputTokens = live?.InputTokens ?? task.InputTokens,
            OutputTokens = live?.OutputTokens ?? task.OutputTokens
        };
        return new SubagentTaskActivity(task, phase, pendingCount, error);
    }

    private void InvalidateIdleCache()
    {
        // An active read observes the revision and clears its cache before releasing the gate.
        if (!_readGate.Wait(0))
        {
            return;
        }

        try
        {
            RefreshCache(true);
        }
        finally
        {
            _readGate.Release();
        }
    }

    private void RefreshCache(bool force)
    {
        if (!force && !CacheChanged() && _pages.Count < 64 && _details.Count < 64)
        {
            return;
        }

        _pages.Clear();
        _details.Clear();
        _cacheRevision = _changes.PersistenceRevision;
        _cacheScopeRevision = Interlocked.Read(ref _scopeRevision);
    }

    private bool CacheChanged()
        => _cacheRevision != _changes.PersistenceRevision || _cacheScopeRevision != Interlocked.Read(ref _scopeRevision);

    private void EnsureOpen(Guid parentConversationId)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        if (_closedParents.ContainsKey(parentConversationId))
        {
            throw new SubagentActivityReadException("activity-scope-closed");
        }
    }

    private static bool IsActive(SubagentActivityTask task)
        => task.Status is SubagentTaskStatus.Queued or SubagentTaskStatus.Running;

    private void OnApprovalRequested(ToolApprovalRequest request) => PublishApprovalChanges();
    private void OnApprovalCompleted(Guid requestId) => PublishApprovalChanges();

    private void PublishApprovalChanges()
    {
        foreach (var parentId in _registry.GetActiveParentIds())
        {
            _changes.Publish(parentId, null, SubagentStateChangeKind.Approval);
        }
    }

}
