namespace SelfClaw.Core.Models;

/// <summary>
/// Quality tiers named after the wire values the OpenAI Images protocol accepts. The vocabulary is a
/// neutral superset, not a model-specific one: which subset a model accepts is answered by
/// <see cref="ImageGenerationModelCapabilities"/>, never by this enum alone.
/// </summary>
public enum ImageGenerationQuality
{
    Auto = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    Standard = 4,
    Hd = 5,
    XHigh = 6,
    Max = 7
}
