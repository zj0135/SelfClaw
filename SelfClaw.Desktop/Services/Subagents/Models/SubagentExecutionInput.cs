using SelfClaw.Core.Models;

namespace SelfClaw.Desktop.Services.Subagents.Models;

internal sealed record SubagentExecutionInput(
    ConversationRecord Conversation,
    IReadOnlyList<ConversationTurnRecord> Turns,
    IReadOnlyList<MessageRecord> Messages,
    IReadOnlyList<ToolExecutionRecord> ToolRuns);
