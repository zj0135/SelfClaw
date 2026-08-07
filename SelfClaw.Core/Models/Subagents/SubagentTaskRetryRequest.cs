namespace SelfClaw.Core.Models;

public sealed record SubagentTaskRetryRequest(
    Guid ParentConversationId,
    Guid ParentTurnId,
    Guid TaskId);
