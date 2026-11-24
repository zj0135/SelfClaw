namespace SelfClaw.Infrastructure.Agents.Direct.Context.Models;

// What the in-turn compactions of one turn cover so far, in the terms a stored compaction can express: every
// stored message through ThroughSequence, plus the turn's own assistant message up to and including the tool
// call AnchorCallId names. The anchor is a call id because only the recorder knows the stored tool run behind
// it. MessageCount and SourceTokens are cumulative for the turn; SourceTokens is null once one compaction
// could not be counted.
internal sealed record DirectInTurnCoverage(
    long ThroughSequence,
    string? AnchorCallId,
    int MessageCount,
    int? SourceTokens);
