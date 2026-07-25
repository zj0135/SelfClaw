using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;

namespace SelfClaw.Desktop.Services.AgentActivity;

public sealed record AgentActivitySnapshot(
    Guid? TurnId,
    Guid? ConversationId,
    string? ConversationTitle,
    string? AgentId,
    string? AgentName,
    AgentExecutionMode? ExecutionMode,
    AgentActivityPhase Phase,
    string Headline,
    string? Detail,
    ToolCallKind? ToolKind,
    ToolApprovalRequest? Approval,
    int PendingApprovalCount,
    int ActiveTurnCount,
    DateTimeOffset UpdatedAtUtc);
