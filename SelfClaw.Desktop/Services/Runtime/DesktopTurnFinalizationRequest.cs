using SelfClaw.Core.Models;

namespace SelfClaw.Desktop.Services.Runtime;

internal sealed record DesktopTurnFinalizationRequest(
    MessageRecord AssistantMessage,
    IReadOnlyList<ToolExecutionRecord> ToolExecutions,
    TurnFinalizationKind Kind,
    string? FinalText,
    string? ErrorMessage,
    int? InputTokens,
    int? OutputTokens,
    DateTimeOffset StartedAtUtc);
