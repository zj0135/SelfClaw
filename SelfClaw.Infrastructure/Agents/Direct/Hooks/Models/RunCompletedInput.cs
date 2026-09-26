namespace SelfClaw.Infrastructure.Agents.Direct.Hooks.Models;

internal sealed record RunCompletedInput(
    string Status,
    string? FinalText,
    string? ErrorMessage,
    int? InputTokens,
    int? OutputTokens,
    int ToolCallCount,
    TimeSpan Duration);
