using SelfClaw.Core.Models;

namespace SelfClaw.Core.Runtime;

/// <summary>
/// Accumulates the usage observations reported for a single turn. Token counts are summed across
/// provider calls; context and model come from the latest observation that carries them; costs are
/// summed per source, and a provider-reported cost wins over the estimates for the same turn.
/// Totals are already normalized by the producer: an unknown total stays unknown through aggregation.
/// </summary>
public sealed class TurnUsageAccumulator
{
    private string? _model;
    private int? _inputTokens;
    private int? _cachedInputTokens;
    private int? _cacheWriteInputTokens;
    private int? _outputTokens;
    private int? _reasoningTokens;
    private int? _totalTokens;
    private bool _hasUnknownTotal;
    private int _providerCalls;
    private int? _contextTokens;
    private int? _contextWindowTokens;
    private long? _estimatedCostUsdMicros;
    private long? _providerReportedCostUsdMicros;
    private string? _additionalCountsJson;

    public bool HasObservations { get; private set; }

    public void Observe(TurnUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);

        HasObservations = true;
        if (usage.Model is not null)
        {
            _model = usage.Model;
        }

        _inputTokens = Add(_inputTokens, usage.InputTokens);
        _cachedInputTokens = Add(_cachedInputTokens, usage.CachedInputTokens);
        _cacheWriteInputTokens = Add(_cacheWriteInputTokens, usage.CacheWriteInputTokens);
        _outputTokens = Add(_outputTokens, usage.OutputTokens);
        _reasoningTokens = Add(_reasoningTokens, usage.ReasoningTokens);
        _totalTokens = Add(_totalTokens, usage.TotalTokens);
        _hasUnknownTotal |= usage.TotalTokens is null;
        _providerCalls += usage.ProviderCalls;

        // The latest observation is the context the next turn starts from, and the window belongs to
        // whichever model produced the final call.
        if (usage.ContextTokens is int contextTokens)
        {
            _contextTokens = contextTokens;
        }

        if (usage.ContextWindowTokens is int contextWindowTokens)
        {
            _contextWindowTokens = contextWindowTokens;
        }

        if (usage.CostUsdMicros is long cost)
        {
            if (usage.CostSource == TurnUsageCostSource.ProviderReported)
            {
                _providerReportedCostUsdMicros = Add(_providerReportedCostUsdMicros, cost);
            }
            else
            {
                _estimatedCostUsdMicros = Add(_estimatedCostUsdMicros, cost);
            }
        }

        // Provider-specific extra counts come from the latest observation that reported any.
        if (!string.IsNullOrEmpty(usage.AdditionalCountsJson))
        {
            _additionalCountsJson = usage.AdditionalCountsJson;
        }
    }

    public TurnUsage? Build()
    {
        if (!HasObservations)
        {
            return null;
        }

        int? uncachedInputTokens = _inputTokens is int inputTokens
            ? Math.Max(0, inputTokens - (_cachedInputTokens ?? 0) - (_cacheWriteInputTokens ?? 0))
            : null;
        var (costUsdMicros, costSource) = _providerReportedCostUsdMicros is long providerReported
            ? (providerReported, TurnUsageCostSource.ProviderReported)
            : _estimatedCostUsdMicros is long estimated
                ? (estimated, TurnUsageCostSource.Estimated)
                : ((long?)null, TurnUsageCostSource.None);

        return new TurnUsage(
            _model,
            _inputTokens,
            uncachedInputTokens,
            _cachedInputTokens,
            _cacheWriteInputTokens,
            _outputTokens,
            _reasoningTokens,
            _hasUnknownTotal ? null : _totalTokens,
            _providerCalls,
            _contextTokens,
            _contextWindowTokens,
            costUsdMicros,
            costSource,
            _additionalCountsJson);
    }

    private static int? Add(int? left, int? right)
        => left is null ? right : right is null ? left : left + right;

    private static long? Add(long? left, long? right)
        => left is null ? right : right is null ? left : left + right;
}
