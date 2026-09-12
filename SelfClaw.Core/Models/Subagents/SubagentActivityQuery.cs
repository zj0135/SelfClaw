namespace SelfClaw.Core.Models;

public sealed record SubagentActivityQuery(
    Guid ParentConversationId,
    Guid? ParentTurnId = null,
    string? Cursor = null,
    int PageSize = 50);
