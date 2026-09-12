using SelfClaw.Core.Models;

namespace SelfClaw.Desktop.Services.Subagents.Models;

internal sealed record SubagentExecutionSnapshot(
    Guid TaskId,
    Guid ParentConversationId,
    MessageRecord? Message,
    IReadOnlyList<ToolExecutionRecord> ToolRuns,
    SubagentExecutionActivity Activity);
