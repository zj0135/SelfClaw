namespace SelfClaw.Desktop.Services.Transcript.Views;

/// <summary>
/// Session-level usage for the composer meter: turn totals plus the size of the context the next
/// request starts from. <see cref="ContextIsEstimated"/> is true only while the context size is a local count
/// that no provider measurement has replaced yet (right after a compaction).
/// <see cref="CachedInputTokens"/> is the cached share of <see cref="InputTokens"/>; null means no turn
/// reported a cached count, which is not the same as zero. <see cref="CompactionTriggerTokens"/> is the
/// context size at which the conversation compacts next, on the same scale as <see cref="ContextTokens"/>;
/// null when it is unknown or compaction is off.
/// </summary>
public sealed record TranscriptUsageSummary(
    long InputTokens,
    long OutputTokens,
    int? ContextTokens,
    int? ContextWindowTokens,
    bool ContextIsEstimated,
    long? CachedInputTokens = null,
    int? CompactionTriggerTokens = null);
