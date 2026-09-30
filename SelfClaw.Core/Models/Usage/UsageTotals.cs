namespace SelfClaw.Core.Models;

/// <summary>Aggregated usage totals over one or more turns. A zero value means "reported as zero", never "unknown".</summary>
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
