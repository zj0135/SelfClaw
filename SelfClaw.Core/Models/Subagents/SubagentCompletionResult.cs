namespace SelfClaw.Core.Models;

public sealed record SubagentCompletionResult(
    string? FinalText,
    bool Truncated,
    string? ErrorCode,
    string? ErrorMessage);
