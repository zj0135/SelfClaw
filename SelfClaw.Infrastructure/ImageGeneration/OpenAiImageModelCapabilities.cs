using System;
using System.Collections.Generic;
using SelfClaw.Core.Models;

namespace SelfClaw.Infrastructure.ImageGeneration;

/// <summary>
/// Per-model parameter sets for the OpenAI Images protocol. The whole <c>gpt-image</c> family shares
/// one enhanced set (the workbench deliberately does not split 2.5 vs 2.6 capabilities), while every
/// other model id is treated as a third-party gateway and answered with a conservative legacy set.
/// </summary>
internal static class OpenAiImageModelCapabilities
{
    /// <summary>
    /// GPT Image capabilities from the provider docs: auto plus preset sizes, low/medium/high/auto
    /// with the 2.5-series xhigh/max tiers available to the whole family, background, output
    /// format/compression and moderation. Free-form sizes are constrained by
    /// <see cref="ImageGenerationCustomSizeRules.GptImage"/>.
    /// </summary>
    private static readonly ImageGenerationModelCapabilities GptImage = new(
        // Presets cover the common aspect ratios; any other 16-aligned WIDTHxHEIGHT is reachable
        // through the custom-size control. Order matters: the first entry is the default selection.
        Sizes:
        [
            "auto",
            "1024x1024",  // 1:1
            "1536x1024",  // 3:2
            "1024x1536",  // 2:3
            "1536x864",   // 16:9
            "864x1536",   // 9:16
            "1024x768",   // 4:3
            "768x1024",   // 3:4
            "1536x768",   // 2:1
            "768x1536"    // 1:2
        ],
        Qualities:
        [
            ImageGenerationQuality.Auto,
            ImageGenerationQuality.Low,
            ImageGenerationQuality.Medium,
            ImageGenerationQuality.High,
            ImageGenerationQuality.XHigh,
            ImageGenerationQuality.Max
        ],
        Styles: [],
        MaxCount: 10,
        MaxPromptLength: 32000,
        Backgrounds:
        [
            ImageGenerationBackground.Auto,
            ImageGenerationBackground.Transparent,
            ImageGenerationBackground.Opaque
        ],
        OutputFormats:
        [
            ImageGenerationOutputFormat.Png,
            ImageGenerationOutputFormat.Jpeg,
            ImageGenerationOutputFormat.Webp
        ],
        Moderations: [ImageGenerationModeration.Auto, ImageGenerationModeration.Low],
        SupportsOutputCompression: true,
        CustomSize: ImageGenerationCustomSizeRules.GptImage);

    /// <summary>
    /// Unrecognised model ids are assumed to be third-party gateways speaking the same protocol with a
    /// superset of the legacy parameter vocabulary. Guessing narrow would break working gateways;
    /// guessing wide only turns a silent UI restriction into a provider error the user can read.
    /// GPT Image-only knobs are disabled here because sending them to an arbitrary gateway would be
    /// more likely to break it than to help.
    /// </summary>
    private static readonly ImageGenerationModelCapabilities Compatible = new(
        Sizes: ["1024x1024", "1024x1536", "1536x1024", "1024x1792", "1792x1024", "512x512", "256x256"],
        Qualities:
        [
            ImageGenerationQuality.Auto,
            ImageGenerationQuality.Low,
            ImageGenerationQuality.Medium,
            ImageGenerationQuality.High,
            ImageGenerationQuality.Standard,
            ImageGenerationQuality.Hd
        ],
        Styles: [ImageGenerationStyle.Vivid, ImageGenerationStyle.Natural],
        MaxCount: 10,
        MaxPromptLength: ImageGenerationModelCapabilities.DefaultMaxPromptLength);

    public static ImageGenerationModelCapabilities Resolve(string model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var normalized = model.Trim().ToLowerInvariant();
        // Gateways prefix or suffix the family name (openai/gpt-image-1, gpt-image-2.6-sunburst), so
        // match on containment rather than equality. Any id outside this family falls through to the
        // gateway set instead of being rejected.
        return normalized.Contains("gpt-image", StringComparison.Ordinal) ? GptImage : Compatible;
    }
}
