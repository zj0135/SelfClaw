using System.Collections.Concurrent;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services.Runtime;
using SelfClaw.Desktop.Services.Subagents.Models;

namespace SelfClaw.Desktop.Services.Subagents;

internal sealed class SubagentActivityRegistry
{
    private readonly ConcurrentDictionary<Guid, SubagentExecutionSession> _sessions = new();
    private readonly ConcurrentDictionary<Guid, (Guid ParentConversationId, string Error)> _recordingFailures = new();
    private readonly ISubagentStateChangeNotifier? _changes;

    public SubagentActivityRegistry(ISubagentStateChangeNotifier? changes = null)
    {
        _changes = changes;
    }

    internal async Task<SubagentExecutionSession> RegisterAsync(SubagentTaskRecord task, SubagentExecutionInput input,
        ConversationTurnRecorder recorder, ISubagentTaskExecutionStore store, TimeProvider timeProvider)
    {
        var session = new SubagentExecutionSession(task, input, recorder, store, timeProvider, _changes);
        if (!_sessions.TryAdd(task.Id, session))
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException("The Subagent task already has an execution session.");
        }

        _recordingFailures.TryRemove(task.Id, out _);
        _changes?.Publish(task.ParentConversationId, task.Id, SubagentStateChangeKind.ExecutionBoundary);
        return session;
    }

    internal SubagentExecutionActivity? GetActivity(Guid taskId)
        => _sessions.TryGetValue(taskId, out var session) ? session.Activity : null;

    internal string? GetRecordingError(Guid taskId)
        => _recordingFailures.TryGetValue(taskId, out var failure) ? failure.Error : null;

    internal IReadOnlyList<Guid> GetActiveParentIds()
        => _sessions.Values.Select(session => session.ParentConversationId).Distinct().ToArray();

    internal Task<SubagentExecutionSnapshot?> CaptureSnapshotAsync(Guid taskId, CancellationToken cancellationToken)
        => _sessions.TryGetValue(taskId, out var session)
            ? session.CaptureSnapshotAsync(cancellationToken)
            : Task.FromResult<SubagentExecutionSnapshot?>(null);

    internal async Task UnregisterAsync(SubagentExecutionSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _sessions.TryRemove(new KeyValuePair<Guid, SubagentExecutionSession>(session.TaskId, session));
        var activity = session.Activity;
        if (activity.RecordingError is string error)
        {
            _recordingFailures[session.TaskId] = (session.ParentConversationId, error);
        }

        await session.DisposeAsync().ConfigureAwait(false);
        _changes?.Publish(session.ParentConversationId, session.TaskId, SubagentStateChangeKind.ExecutionBoundary);
    }

    internal void ClearRecordingFailure(Guid taskId) => _recordingFailures.TryRemove(taskId, out _);

    internal void ClearParent(Guid parentConversationId)
    {
        foreach (var failure in _recordingFailures.Where(pair => pair.Value.ParentConversationId == parentConversationId))
        {
            _recordingFailures.TryRemove(failure.Key, out _);
        }
    }
}
