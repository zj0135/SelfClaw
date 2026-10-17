namespace SelfClaw.Core.Runtime.Agent;

/// <summary>
/// Compaction failed for this turn only: the turn continues with the uncompacted context and the user
/// sees a diagnostic instead of a silently truncated history.
/// </summary>
public sealed record ContextCompactionFailedEvent(string Reason) : AgentStreamEvent;
