namespace SelfClaw.Core.Runtime;

public sealed record ToolApprovalRequest(
    Guid ToolExecutionId,
    string ToolName,
    string DisplayName,
    string Description,
    string ArgumentsJson,
    Guid? ConversationId = null,
    Agent.ToolSourceKind? SourceKind = null,
    string? SourceId = null,
    string? TransportSummary = null,
    string? AnnotationsJson = null);
