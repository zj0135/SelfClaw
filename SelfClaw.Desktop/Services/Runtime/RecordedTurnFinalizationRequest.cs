using SelfClaw.Core.Models;

namespace SelfClaw.Desktop.Services.Runtime;

internal sealed record RecordedTurnFinalizationRequest(
    MessageRecord AssistantMessage,
    IReadOnlyList<ToolExecutionRecord> ToolExecutions,
    TurnFinalizationKind Kind,
    string? FinalText,
    string? ErrorMessage,
    TurnUsage? Usage,
    DateTimeOffset StartedAtUtc);
