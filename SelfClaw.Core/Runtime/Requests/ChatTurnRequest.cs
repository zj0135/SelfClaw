using SelfClaw.Core.Models;

namespace SelfClaw.Core.Runtime;

public sealed record ChatTurnRequest(
    Guid ConversationId,
    ProviderProfile Profile,
    string ApiKey,
    WorkspaceRoot? WorkspaceRoot,
    ConversationMode Mode,
    ToolPermissionMode ToolPermissionMode,
    int TeamMaxRounds,
    TeamOutputMode TeamOutputMode,
    IToolApprovalHandler? ToolApprovalHandler,
    IReadOnlyList<MessageRecord> Messages,
    IReadOnlyList<TeamAgentRecord> TeamAgents,
    IReadOnlyList<MessageRecord>? ContextMessages = null,
    TeamAgentRecord? BoundAgent = null);
