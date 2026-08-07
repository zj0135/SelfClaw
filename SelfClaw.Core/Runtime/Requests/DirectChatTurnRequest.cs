using SelfClaw.Core.Models;

namespace SelfClaw.Core.Runtime;

/// <summary>
/// A turn routed to the in-process Direct runtime. Carries the provider model selection and the workspace-tool
/// approval policy; the CLI agent / session inputs do not apply here and are absent by construction.
/// </summary>
public sealed record DirectChatTurnRequest(
    Guid TurnId,
    Guid ConversationId,
    WorkspaceRoot? WorkspaceRoot,
    AgentRuntimeDefinition Agent,
    IReadOnlyList<MessageRecord> Messages,
    Guid? ModelProfileId,
    ToolPermissionMode ToolPermissionMode,
    IToolApprovalHandler? ToolApprovalHandler,
    DirectTurnExecutionContext ExecutionContext)
    : ChatTurnRequest(TurnId, ConversationId, WorkspaceRoot, Agent, Messages)
{
    public override AgentExecutionMode Mode => AgentExecutionMode.Direct;
}
