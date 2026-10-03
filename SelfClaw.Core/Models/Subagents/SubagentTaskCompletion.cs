namespace SelfClaw.Core.Models;

public sealed record SubagentTaskCompletion(
    SubagentTaskStatus Status,
    ConversationTurnCommit TurnFinalization,
    string? FinalText,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset CompletedAtUtc);
