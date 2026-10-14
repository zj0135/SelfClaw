namespace SelfClaw.Core.Models;

public sealed record AutomationTaskSaveResult(
    AutomationTaskRecord? Task,
    DateTimeOffset? NextRunAtUtc,
    string? ErrorCode,
    string? ErrorMessage);
