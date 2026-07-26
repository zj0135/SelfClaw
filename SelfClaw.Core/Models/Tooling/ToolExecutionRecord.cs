using SelfClaw.Core.Runtime.Agent;

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
    Guid? AgentId = null,
    Guid? MessageId = null,
    int? AfterSegmentIndex = null,
    string? ResultContent = null,
    ToolSourceKind? SourceKind = null,
    string? SourceId = null,
    string? DisplayName = null)
{
    public ToolExecutionRecord(
        Guid id,
        Guid conversationId,
        string toolName,
        string argumentsJson,
        ToolExecutionStatus status,
        string? resultSummary,
        string? correlationId,
        double? durationMs,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc,
        Guid? messageId,
        int? afterSegmentIndex,
        string? resultContent = null,
        ToolSourceKind? sourceKind = null,
        string? sourceId = null,
        string? displayName = null)
        : this(
            id,
            conversationId,
            toolName,
            argumentsJson,
            status,
            resultSummary,
            correlationId,
            durationMs,
            createdAtUtc,
            updatedAtUtc,
            null,
            messageId,
            afterSegmentIndex,
            resultContent,
            sourceKind,
            sourceId,
            displayName)
    {
    }
}
