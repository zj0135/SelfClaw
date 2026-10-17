namespace SelfClaw.Desktop.Services.Transcript.Views;

/// <summary>
/// Session-level usage for the composer meter: turn totals plus the size of the context the next
/// request starts from. <see cref="ContextIsEstimated"/> stays false because every value here comes
/// from provider-reported measurements; only a real counting source may ever flip it.
/// </summary>
public sealed record TranscriptUsageSummary(
    long InputTokens,
    long OutputTokens,
    int? ContextTokens,
    int? ContextWindowTokens,
    bool ContextIsEstimated);
