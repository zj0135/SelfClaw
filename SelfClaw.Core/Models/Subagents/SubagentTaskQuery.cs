namespace SelfClaw.Core.Models;

public sealed record SubagentTaskQuery(
    Guid ParentConversationId,
    Guid TaskId);
