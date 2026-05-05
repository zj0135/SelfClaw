using SelfClaw.Core.Models;

namespace SelfClaw.Core.Runtime;

public sealed record ChatTurnRequest(
    Guid ConversationId,
    ProviderProfile Profile,
    string ApiKey,
    WorkspaceRoot? WorkspaceRoot,
    ConversationMode Mode,
    AgentRuntimeDefinition Agent,
    ToolPermissionMode ToolPermissionMode,
    IToolApprovalHandler? ToolApprovalHandler,
    IReadOnlyList<MessageRecord> Messages,
    bool EnableReasoning = false);
