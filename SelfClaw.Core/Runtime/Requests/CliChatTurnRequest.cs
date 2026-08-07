using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime.Agent;

namespace SelfClaw.Core.Runtime;

/// <summary>
/// A turn routed to a local CLI agent. Carries the selected CLI and its optional model / reasoning overrides; the
/// Direct model profile and workspace-tool approval do not apply here (the CLI manages its own model, auth and
/// permissions) and are absent by construction.
/// </summary>
public sealed record CliChatTurnRequest(
    Guid TurnId,
    Guid ConversationId,
    WorkspaceRoot? WorkspaceRoot,
    AgentRuntimeDefinition Agent,
    IReadOnlyList<MessageRecord> Messages,
    CliAgentKind? CliAgent,
    string? CliModel,
    string? CliReasoningEffort)
    : ChatTurnRequest(TurnId, ConversationId, WorkspaceRoot, Agent, Messages)
{
    public override AgentExecutionMode Mode => AgentExecutionMode.Cli;
}
