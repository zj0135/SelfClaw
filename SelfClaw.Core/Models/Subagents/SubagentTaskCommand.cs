namespace SelfClaw.Core.Models;

public sealed record SubagentTaskCommand(
    Guid ParentConversationId,
    Guid TaskId);
