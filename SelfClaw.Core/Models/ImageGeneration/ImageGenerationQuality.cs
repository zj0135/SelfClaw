namespace SelfClaw.Core.Models;

/// <summary>
/// Quality tier named after the wire values the OpenAI Images protocol accepts. The vocabulary is not
/// shared: <c>dall-e-3</c> speaks <c>standard</c>/<c>hd</c>, <c>gpt-image-1</c> speaks
/// <c>low</c>/<c>medium</c>/<c>high</c>/<c>auto</c>. Which subset a model accepts is answered by
/// <see cref="ImageGenerationModelCapabilities"/>, never by this enum alone.
/// </summary>
public enum ImageGenerationQuality
{
    Auto = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    Standard = 4,
    Hd = 5
}
