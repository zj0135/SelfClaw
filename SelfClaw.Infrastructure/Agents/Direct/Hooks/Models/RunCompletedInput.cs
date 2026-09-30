using SelfClaw.Core.Models;

namespace SelfClaw.Infrastructure.Agents.Direct.Hooks.Models;

internal sealed record RunCompletedInput(
    string Status,
    string? FinalText,
    string? ErrorMessage,
    TurnUsage? Usage,
    int ToolCallCount,
    TimeSpan Duration);
