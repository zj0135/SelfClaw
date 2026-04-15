namespace SelfClaw.Core.Models;

public sealed record TeamAgentRecord(
    Guid Id,
    Guid ConversationId,
    string Name,
    string Role,
    string GoalPrompt,
    TeamAgentStatus Status,
    int SortOrder,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
