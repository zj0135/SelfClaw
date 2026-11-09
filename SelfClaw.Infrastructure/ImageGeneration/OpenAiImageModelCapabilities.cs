using System;
using System.Collections.Generic;
using SelfClaw.Core.Models;

namespace SelfClaw.Infrastructure.ImageGeneration;

/// <summary>
/// Per-model parameter sets for the OpenAI Images protocol. The families disagree on nearly every
/// field, so the request shape has to be chosen per model instead of once for all:
/// <c>dall-e-3</c> rejects <c>n &gt; 1</c> and speaks quality standard/hd with a style switch,
/// <c>gpt-image-1</c> rejects <c>response_format</c> and <c>style</c> outright and speaks
/// low/medium/high with its own size set, and <c>dall-e-2</c> has no style and no hd at all.
/// </summary>
internal static class OpenAiImageModelCapabilities
{
    private static readonly ImageGenerationModelCapabilities Dalle2 = new(
        ["256x256", "512x512", "1024x1024"],
        [ImageGenerationQuality.Standard],
        [],
        MaxCount: 10);

    private static readonly ImageGenerationModelCapabilities Dalle3 = new(
        ["1024x1024", "1024x1792", "1792x1024"],
        [ImageGenerationQuality.Standard, ImageGenerationQuality.Hd],
        [ImageGenerationStyle.Vivid, ImageGenerationStyle.Natural],
        MaxCount: 1);

    private static readonly ImageGenerationModelCapabilities GptImage1 = new(
        ["1024x1024", "1024x1536", "1536x1024"],
        [ImageGenerationQuality.Auto, ImageGenerationQuality.Low, ImageGenerationQuality.Medium, ImageGenerationQuality.High],
        [],
        MaxCount: 10);

    /// <summary>
    /// Unrecognised model ids are assumed to be third-party gateways speaking the same protocol with a
    /// superset parameter set. Guessing narrow would break working gateways; guessing wide only turns a
    /// silent UI restriction into a provider error the user can read.
    /// </summary>
    private static readonly ImageGenerationModelCapabilities Compatible = new(
        ["1024x1024", "1024x1536", "1536x1024", "1024x1792", "1792x1024", "512x512", "256x256"],
        [
            ImageGenerationQuality.Auto,
            ImageGenerationQuality.Low,
            ImageGenerationQuality.Medium,
            ImageGenerationQuality.High,
            ImageGenerationQuality.Standard,
            ImageGenerationQuality.Hd
        ],
        [ImageGenerationStyle.Vivid, ImageGenerationStyle.Natural],
        MaxCount: 10);

    public static ImageGenerationModelCapabilities Resolve(string model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var normalized = model.Trim().ToLowerInvariant();
        // Gateways prefix or suffix the family name (openai/dall-e-3, dall-e-3@2024-01), so match on
        // containment rather than equality.
        if (normalized.Contains("gpt-image", StringComparison.Ordinal))
        {
            return GptImage1;
        }

        if (normalized.Contains("dall-e-3", StringComparison.Ordinal))
        {
            return Dalle3;
        }

        return normalized.Contains("dall-e-2", StringComparison.Ordinal) ? Dalle2 : Compatible;
    }
}
