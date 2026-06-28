namespace SelfClaw.Core.Runtime.Agent;

/// <summary>Result of a previously started tool call, matched by <paramref name="ToolCallId"/>.</summary>
public sealed record ToolCallCompletedEvent(
    string ToolCallId,
    ToolCallStatus Status,
    string? ResultSummary,
    string? ResultContent) : AgentStreamEvent;
