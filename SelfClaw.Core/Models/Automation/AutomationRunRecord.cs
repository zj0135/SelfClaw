namespace SelfClaw.Core.Models;

public sealed record AutomationRunRecord(
    Guid Id,
    Guid TaskId,
    Guid? ConversationId,
    AutomationRunTrigger Trigger,
    DateTimeOffset? ScheduledForUtc,
    AutomationRunStatus Status,
    AutomationRunSkipReason? SkipReason,
    int Attempt,
    Guid? ModelProfileId,
    string? CapabilitySnapshotJson,
    Guid? WorkspaceRootId,
    string? WorkspacePathSnapshot,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset CreatedAtUtc);
