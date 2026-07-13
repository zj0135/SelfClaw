using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime.Agent;

namespace SelfClaw.Core.Runtime;

public sealed record ChatTurnRequest(
    Guid ConversationId,
    ProviderProfile? Profile,
    string? ApiKey,
    WorkspaceRoot? WorkspaceRoot,
    ConversationMode Mode,
    AgentRuntimeDefinition Agent,
    CliAgentKind? CliAgent,
    string? CliModel,
    string? CliReasoningEffort,
    ToolPermissionMode ToolPermissionMode,
    IToolApprovalHandler? ToolApprovalHandler,
    IReadOnlyList<MessageRecord> Messages);
