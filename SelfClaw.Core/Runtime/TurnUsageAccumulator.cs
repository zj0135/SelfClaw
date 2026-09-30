using SelfClaw.Core.Models;

namespace SelfClaw.Core.Runtime;

/// <summary>
/// Accumulates the usage observations reported for a single turn. Token counts are summed across
/// provider calls, context and model come from the latest observation, and provider-reported cost
/// wins over an estimate.
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
    private int _providerCalls;
    private int? _contextTokens;
    private int? _contextWindowTokens;
    private long? _costUsdMicros;
    private TurnUsageCostSource _costSource;
    private string? _additionalCountsJson;

    public bool HasObservations { get; private set; }

    public void Observe(TurnUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);

        HasObservations = true;
        _model ??= usage.Model;
        _inputTokens = Add(_inputTokens, usage.InputTokens);
        _cachedInputTokens = Add(_cachedInputTokens, usage.CachedInputTokens);
        _cacheWriteInputTokens = Add(_cacheWriteInputTokens, usage.CacheWriteInputTokens);
        _outputTokens = Add(_outputTokens, usage.OutputTokens);
        _reasoningTokens = Add(_reasoningTokens, usage.ReasoningTokens);
        _totalTokens = Add(_totalTokens, usage.TotalTokens);
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
                _costUsdMicros = _costSource == TurnUsageCostSource.ProviderReported
                    ? (_costUsdMicros ?? 0) + cost
                    : cost;
                _costSource = TurnUsageCostSource.ProviderReported;
            }
            else if (_costSource != TurnUsageCostSource.ProviderReported)
            {
                _costUsdMicros = cost;
                _costSource = TurnUsageCostSource.Estimated;
            }
        }

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
        int? totalTokens = _totalTokens ?? (_inputTokens is int input && _outputTokens is int output
            ? input + output
            : null);

        return new TurnUsage(
            _model,
            _inputTokens,
            uncachedInputTokens,
            _cachedInputTokens,
            _cacheWriteInputTokens,
            _outputTokens,
            _reasoningTokens,
            totalTokens,
            _providerCalls,
            _contextTokens,
            _contextWindowTokens,
            _costUsdMicros,
            _costSource,
            _additionalCountsJson);
    }

    private static int? Add(int? left, int? right)
        => left is null ? right : right is null ? left : left + right;
}
