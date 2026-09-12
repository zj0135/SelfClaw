using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;

namespace SelfClaw.Infrastructure.Agents.Subagents.Runtime;

internal sealed class SubagentStateChangeNotifier : ISubagentStateChangeNotifier
{
    private readonly ILogger<SubagentStateChangeNotifier> _logger;
    private long _persistenceRevision;

    public SubagentStateChangeNotifier(ILogger<SubagentStateChangeNotifier>? logger = null)
    {
        _logger = logger ?? NullLogger<SubagentStateChangeNotifier>.Instance;
    }

    public long PersistenceRevision => Interlocked.Read(ref _persistenceRevision);

    public event Action<SubagentStateChange>? Changed;

    public void Publish(Guid parentConversationId, Guid? taskId, SubagentStateChangeKind kind)
    {
        var revision = kind is SubagentStateChangeKind.Task or SubagentStateChangeKind.Delivery
            ? Interlocked.Increment(ref _persistenceRevision)
            : PersistenceRevision;
        var change = new SubagentStateChange(parentConversationId, taskId, kind, revision);
        var handlers = Changed;
        if (handlers is null)
        {
            return;
        }

        // Observers run outside the writer's transaction and cancellation flow.
        foreach (Action<SubagentStateChange> handler in handlers.GetInvocationList())
        {
            _ = Task.Run(() => handler(change)).ContinueWith(
                failed => _logger.LogWarning(failed.Exception,
                    "Subagent state observer failed. ParentConversationId={ParentConversationId} TaskId={TaskId} Kind={Kind}",
                    parentConversationId, taskId, kind),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}
