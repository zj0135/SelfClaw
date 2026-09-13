namespace SelfClaw.Core.Models;

public sealed record AiModelConfiguration(
    string Model,
    string Name,
    AiSamplingOptions Sampling,
    bool? IsMultimodal = null,
    string? ReasoningEffort = null,
    int? ContextLength = null,
    int? MaxOutputTokens = null,
    decimal? PriceInPerMTok = null,
    decimal? PriceOutPerMTok = null,
    decimal? PriceCacheReadPerMTok = null,
    decimal? PriceCacheWritePerMTok = null,
    string? Description = null);
