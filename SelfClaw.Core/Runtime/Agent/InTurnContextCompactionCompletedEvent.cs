namespace SelfClaw.Core.Runtime.Agent;

/// <summary>
/// A compaction that happened between provider round trips of one turn. It is observation only: the
/// summary lives in this turn's request context, so nothing is persisted, recorded or replayed, and the
/// transcript must not derive any state from it.
/// </summary>
public sealed record InTurnContextCompactionCompletedEvent(
    int CoveredUnitCount,
    int? SourceTokens,
    int? SummaryTokens,
    string? Model) : AgentStreamEvent;
