namespace SelfClaw.Core.Models;

/// <summary>
/// Everything one turn writes atomically. Compactions stay a separate collection: they describe history
/// that was replaced by a summary, not message content owned by this turn.
/// </summary>
public sealed record ConversationTurnCommit(
    ConversationTurnRecord Turn,
    IReadOnlyList<MessageRecord> Messages,
    IReadOnlyList<ToolExecutionRecord> ToolExecutions,
    IReadOnlyList<ConversationCompactionRecord>? Compactions = null);
