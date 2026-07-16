namespace SelfClaw.Infrastructure.AiProviders.Models;

/// <summary>
/// Provider-neutral model metadata returned by a remote model catalog or a
/// built-in provider catalog entry. Metadata is best-effort and may be absent.
/// </summary>
public sealed record AiModelDescriptor(
    string ModelId,
    string? DisplayName,
    long? ContextLength,
    long? MaxOutputTokens,
    decimal? PriceInPerMTok,
    decimal? PriceOutPerMTok,
    decimal? PriceCacheWritePerMTok,
    decimal? PriceCacheReadPerMTok);
