namespace SelfClaw.Core.Models;

public sealed record ConversationTurnCommit(
    ConversationTurnRecord Turn,
    IReadOnlyList<MessageRecord> Messages,
    IReadOnlyList<ToolExecutionRecord> ToolExecutions);
