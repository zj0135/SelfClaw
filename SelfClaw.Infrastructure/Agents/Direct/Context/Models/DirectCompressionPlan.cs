namespace SelfClaw.Infrastructure.Agents.Direct.Context.Models;

// The compression decision for one turn: which whole units the summary replaces, and how much of the
// history survives. ThroughSequence is the last replaced unit's message sequence; null means no prefix.
// ThresholdTokens is the trigger the decision was made against, reported to the transcript as-is.
internal sealed record DirectCompressionPlan(
    bool ShouldCompress,
    IReadOnlyList<DirectPromptHistoryUnit> PrefixUnits,
    long? ThroughSequence,
    int RetainedUnitCount,
    int? SourceTokens,
    int ThresholdTokens);
