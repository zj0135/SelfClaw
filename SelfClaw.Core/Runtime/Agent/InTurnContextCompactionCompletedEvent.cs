using SelfClaw.Core.Models;

namespace SelfClaw.Core.Runtime.Agent;

/// <summary>
/// A compaction that happened between provider round trips of one turn. <paramref name="Compaction"/> is the
/// record to store with the turn, so the next turn replays against this summary instead of the whole assistant
/// message; one turn reuses one record identity, and each later event replaces the earlier record. It is null
/// when the summary covers something a stored boundary cannot express, in which case the summary lives in this
/// turn's request only. <paramref name="AnchorCallId"/> names the last tool call the record covers inside the
/// turn's own assistant message: the runtime knows the call id, only the recorder knows the stored tool run.
/// </summary>
public sealed record InTurnContextCompactionCompletedEvent(
    int CoveredUnitCount,
    int? SourceTokens,
    int? SummaryTokens,
    string? Model,
    ConversationCompactionRecord? Compaction = null,
    string? AnchorCallId = null) : AgentStreamEvent;
