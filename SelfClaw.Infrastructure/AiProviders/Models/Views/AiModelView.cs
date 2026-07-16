using System.Text.Json;

namespace SelfClaw.Infrastructure.AiProviders.Models.Views;

public sealed record AiModelView(
    Guid ModelProfileId,
    Guid ProviderConnectionId,
    string Name,
    string Model,
    AiProviderApiFormat ApiFormat,
    AiSamplingOptions Sampling,
    IReadOnlyDictionary<string, JsonElement> ModelOptions,
    bool Enabled,
    long? ContextLength,
    long? MaxOutputTokens,
    decimal? PriceInPerMTok,
    decimal? PriceOutPerMTok,
    decimal? PriceCacheWritePerMTok,
    decimal? PriceCacheReadPerMTok);
