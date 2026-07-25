using SelfClaw.Core.Models;

namespace SelfClaw.Core.Runtime;

/// <summary>
/// A single conversation turn's shared intent: which conversation and workspace it belongs to, the agent that
/// runs it, and the message history. Mode-specific inputs (Direct model / approval, CLI agent / session) live on
/// the <see cref="DirectChatTurnRequest"/> and <see cref="CliChatTurnRequest"/> subtypes so a caller never fills
/// in fields the chosen runtime ignores. <see cref="Mode"/> selects the runtime adapter.
/// </summary>
public abstract record ChatTurnRequest(
    Guid ConversationId,
    WorkspaceRoot? WorkspaceRoot,
    AgentRuntimeDefinition Agent,
    IReadOnlyList<MessageRecord> Messages)
{
    /// <summary>The runtime branch this request targets; used by the dispatcher to pick an adapter.</summary>
    public abstract AgentExecutionMode Mode { get; }
}
