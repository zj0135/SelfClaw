namespace SelfClaw.Core.Models;

public sealed record SubagentContentSnapshot(
    string TaskText,
    ConversationTurnRecord? Turn,
    MessageRecord? Message,
    IReadOnlyList<ToolExecutionRecord> ToolRuns,
    IReadOnlyList<ToolExecutionRecord> UnplacedToolRuns,
    string ContentVersion,
    SubagentHistoryCompleteness HistoryCompleteness);
