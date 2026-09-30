namespace SelfClaw.Core.Models;

/// <summary>
/// Aggregated usage totals over one or more turns. Missing observations are summed as zero, so a zero
/// means "nothing was reported for this field" on the read side; <see cref="PricedTurnCount"/> records
/// how many turns actually carried a cost.
/// </summary>
public sealed record UsageTotals(
    long InputTokens,
    long UncachedInputTokens,
    long CachedInputTokens,
    long CacheWriteInputTokens,
    long OutputTokens,
    long ReasoningTokens,
    long TotalTokens,
    long ProviderCalls,
    long CostUsdMicros,
    int TurnCount,
    int PricedTurnCount);
