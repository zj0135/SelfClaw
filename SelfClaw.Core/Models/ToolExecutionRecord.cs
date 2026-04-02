namespace SelfClaw.Core.Models;

public sealed record ToolExecutionRecord(
    Guid Id,
    Guid ConversationId,
    string ToolName,
    string ArgumentsJson,
    ToolExecutionStatus Status,
    string? ResultSummary,
    string? CorrelationId,
    double? DurationMs,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    Guid? MessageId = null,
    int? AfterSegmentIndex = null);
