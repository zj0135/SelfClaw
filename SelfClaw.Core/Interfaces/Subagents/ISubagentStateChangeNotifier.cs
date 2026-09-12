using SelfClaw.Core.Models;

namespace SelfClaw.Core.Interfaces;

public interface ISubagentStateChangeNotifier
{
    long PersistenceRevision { get; }

    event Action<SubagentStateChange>? Changed;

    void Publish(Guid parentConversationId, Guid? taskId, SubagentStateChangeKind kind);
}
