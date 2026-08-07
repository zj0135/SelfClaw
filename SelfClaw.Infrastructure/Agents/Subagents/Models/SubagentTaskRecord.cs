using SelfClaw.Core.Models;

namespace SelfClaw.Infrastructure.Agents.Subagents.Models;

internal sealed record SubagentTaskRecord(
    Guid Id,
    Guid ParentConversationId,
    Guid ParentTurnId,
    Guid ChildConversationId,
    Guid ChildTurnId,
    string SubagentId,
    string SubagentName,
    string TaskText,
    SubagentTaskStatus Status,
    int Attempt,
    Guid? RetryOfTaskId,
    string DefinitionSnapshotJson,
    string ParentExecutionSnapshotJson,
    Guid? ResolvedModelProfileId,
    int MaxRunSeconds,
    string? FinalText,
    int? InputTokens,
    int? OutputTokens,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset? CancelRequestedAtUtc,
    DateTimeOffset QueuedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
