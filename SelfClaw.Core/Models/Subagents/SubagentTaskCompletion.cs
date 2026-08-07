namespace SelfClaw.Core.Models;

public sealed record SubagentTaskCompletion(
    SubagentTaskStatus Status,
    TurnFinalization TurnFinalization,
    string? FinalText,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset CompletedAtUtc);
