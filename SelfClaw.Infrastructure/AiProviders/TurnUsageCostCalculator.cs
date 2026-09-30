using SelfClaw.Core.Models;

namespace SelfClaw.Infrastructure.AiProviders;

/// <summary>
/// Estimates a turn's cost from its normalized usage and the model's USD per-million-token prices.
/// Missing cache prices fall back to the plain input price. A partially priced model returns
/// <c>null</c> rather than an estimate that silently bills the unpriced side at zero.
/// </summary>
internal static class TurnUsageCostCalculator
{
    private const decimal TokensPerMillion = 1_000_000m;
    private const decimal MicrosPerUsd = 1_000_000m;

    internal static long? ComputeCostUsdMicros(TurnUsage usage, AiModelConfiguration? configuration)
    {
        ArgumentNullException.ThrowIfNull(usage);
        if (configuration?.PriceInPerMTok is not decimal inputPrice ||
            configuration.PriceOutPerMTok is not decimal outputPrice)
        {
            return null;
        }

        var cacheReadPrice = configuration.PriceCacheReadPerMTok ?? inputPrice;
        var cacheWritePrice = configuration.PriceCacheWritePerMTok ?? inputPrice;

        var usd = ((usage.UncachedInputTokens ?? 0) / TokensPerMillion * inputPrice)
            + ((usage.CachedInputTokens ?? 0) / TokensPerMillion * cacheReadPrice)
            + ((usage.CacheWriteInputTokens ?? 0) / TokensPerMillion * cacheWritePrice)
            + ((usage.OutputTokens ?? 0) / TokensPerMillion * outputPrice);
        return (long)Math.Round(usd * MicrosPerUsd, MidpointRounding.AwayFromZero);
    }
}
