namespace SelfClaw.Infrastructure.Agents.Subagents.Persistence.Models;

internal sealed record SubagentActivityCursor(Guid ParentConversationId, Guid? ParentTurnId, string ListVersion, int Offset);
