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
    string? ResultContent = null)
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
        string? resultContent = null)
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
            resultContent)
    {
    }
}
