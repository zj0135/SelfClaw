namespace SelfClaw.Core.Runtime.Agent;

/// <summary>Terminal event for a run. <paramref name="FinalText"/> is the resolved assistant text, if any.</summary>
public sealed record RunCompletedEvent(
    RunCompletionStatus Status,
    string? FinalText,
    string? ErrorMessage = null) : AgentStreamEvent;
