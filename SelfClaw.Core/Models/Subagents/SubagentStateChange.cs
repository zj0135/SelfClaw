namespace SelfClaw.Core.Models;

public sealed record SubagentStateChange(
    Guid ParentConversationId,
    Guid? TaskId,
    SubagentStateChangeKind Kind,
    long PersistenceRevision);
