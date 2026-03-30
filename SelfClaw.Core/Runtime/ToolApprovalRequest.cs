namespace SelfClaw.Core.Runtime;

public sealed record ToolApprovalRequest(
    Guid ToolExecutionId,
    string ToolName,
    string DisplayName,
    string Description,
    string ArgumentsJson);
