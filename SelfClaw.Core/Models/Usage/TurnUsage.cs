namespace SelfClaw.Core.Models;

/// <summary>
/// Normalized usage observed for one turn, summed across every provider call the turn made.
/// </summary>
/// <remarks>
/// <see cref="InputTokens"/> always covers the complete input side, including cached and cache-write
/// tokens; <see cref="UncachedInputTokens"/> is the part actually billed at the plain input price.
/// <see cref="OutputTokens"/> includes reasoning/thinking tokens, which are reported separately only
/// for display. <see cref="ContextTokens"/> snapshots the last provider call of the turn (input + output,
/// semantics unchanged), which is the context the next turn starts from.
/// <see cref="LastRequestInputTokens"/> is the input side of that same call — the real size of the replayed
/// context — and is taken from the latest observation without summation, unlike <see cref="InputTokens"/>.
/// <see cref="AdditionalCountsJson"/> carries the provider-specific
/// extra counts of the latest observation that reported any.
/// <see cref="TotalTokens"/> is an already normalized total. Null means unknown, even when both input
/// and output contain partial observations; consumers must not derive a total from those fields.
/// </remarks>
public sealed record TurnUsage(
    string? Model = null,
    int? InputTokens = null,
    int? UncachedInputTokens = null,
    int? CachedInputTokens = null,
    int? CacheWriteInputTokens = null,
    int? OutputTokens = null,
    int? ReasoningTokens = null,
    int? TotalTokens = null,
    int ProviderCalls = 1,
    int? ContextTokens = null,
    int? ContextWindowTokens = null,
    long? CostUsdMicros = null,
    TurnUsageCostSource CostSource = TurnUsageCostSource.None,
    string? AdditionalCountsJson = null,
    int? LastRequestInputTokens = null);
