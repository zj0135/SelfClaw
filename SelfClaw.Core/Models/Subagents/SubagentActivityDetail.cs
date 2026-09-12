namespace SelfClaw.Core.Models;

public sealed record SubagentActivityDetail(
    SubagentActivityTask Task,
    string TaskText,
    MessageRecord? Message,
    IReadOnlyList<ToolExecutionRecord> ToolRuns,
    IReadOnlyList<ToolExecutionRecord> UnplacedToolRuns,
    string ContentVersion,
    SubagentHistoryCompleteness HistoryCompleteness);
