using SelfClaw.Core.Models;

namespace SelfClaw.Infrastructure.AiProviders;

/// <summary>
/// Estimates a turn's cost from its normalized usage and the model's USD per-million-token prices.
/// Missing cache prices fall back to the plain input price; without input and output prices no
/// estimate is possible, so null is returned instead of a misleading zero.
/// </summary>
internal static class TurnUsageCostCalculator
{
    private const decimal TokensPerMillion = 1_000_000m;
    private const decimal MicrosPerUsd = 1_000_000m;

    internal static long? ComputeCostUsdMicros(TurnUsage usage, AiModelConfiguration? configuration)
    {
        ArgumentNullException.ThrowIfNull(usage);
        if (configuration is null ||
            (configuration.PriceInPerMTok is null && configuration.PriceOutPerMTok is null))
        {
            return null;
        }

        var inputPrice = configuration.PriceInPerMTok ?? 0m;
        var outputPrice = configuration.PriceOutPerMTok ?? 0m;
        var cacheReadPrice = configuration.PriceCacheReadPerMTok ?? configuration.PriceInPerMTok ?? 0m;
        var cacheWritePrice = configuration.PriceCacheWritePerMTok ?? configuration.PriceInPerMTok ?? 0m;

        var usd = ((usage.UncachedInputTokens ?? 0) / TokensPerMillion * inputPrice)
            + ((usage.CachedInputTokens ?? 0) / TokensPerMillion * cacheReadPrice)
            + ((usage.CacheWriteInputTokens ?? 0) / TokensPerMillion * cacheWritePrice)
            + ((usage.OutputTokens ?? 0) / TokensPerMillion * outputPrice);
        return (long)Math.Round(usd * MicrosPerUsd, MidpointRounding.AwayFromZero);
    }
}
