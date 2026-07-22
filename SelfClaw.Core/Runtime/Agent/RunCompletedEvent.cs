namespace SelfClaw.Core.Runtime.Agent;

/// <summary>
/// Terminal event for a run. Successful and failed runs emit exactly one terminal event as the final
/// stream item. Cancellation is control flow and propagates as <see cref="OperationCanceledException"/>.
/// </summary>
public sealed record RunCompletedEvent(
    RunCompletionStatus Status,
    string? FinalText,
    string? ErrorMessage = null) : AgentStreamEvent;
