using SelfClaw.Core.Runtime;

namespace SelfClaw.Core.Models;

public sealed record ConversationTurnRecord(
    Guid Id,
    Guid ConversationId,
    AgentExecutionMode ExecutionMode,
    DirectTurnOrigin Origin,
    ConversationTurnStatus Status,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc = null,
    string? ErrorMessage = null,
    TurnUsage? Usage = null);
