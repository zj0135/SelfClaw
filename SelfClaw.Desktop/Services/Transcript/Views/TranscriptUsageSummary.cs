namespace SelfClaw.Desktop.Services.Transcript.Views;

/// <summary>
/// Session-level usage for the composer meter: turn totals plus the size of the context the next
/// request starts from. <see cref="ContextIsEstimated"/> stays false because every value here comes
/// from provider-reported measurements; only a real counting source may ever flip it.
/// <see cref="CachedInputTokens"/> is the cached share of <see cref="InputTokens"/>; null means no turn
/// reported a cached count, which is not the same as zero.
/// </summary>
public sealed record TranscriptUsageSummary(
    long InputTokens,
    long OutputTokens,
    int? ContextTokens,
    int? ContextWindowTokens,
    bool ContextIsEstimated,
    long? CachedInputTokens = null);
