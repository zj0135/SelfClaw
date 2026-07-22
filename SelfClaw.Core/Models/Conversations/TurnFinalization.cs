namespace SelfClaw.Core.Models;

public sealed record TurnFinalization(
    MessageRecord AssistantMessage,
    IReadOnlyList<ToolExecutionRecord> ToolExecutions);
