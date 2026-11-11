using System.Collections.Generic;

namespace SelfClaw.Core.Models;

/// <summary>
/// What one image model actually accepts, so the workbench can build its controls from the model
/// instead of from a fixed form, and the client can refuse a mismatched request with a readable
/// message rather than letting the provider answer 400.
/// </summary>
/// <param name="Sizes">Preset size values shown as cards. May contain <c>auto</c>.</param>
/// <param name="Qualities">Quality tiers the model accepts, in display order.</param>
/// <param name="Styles">Style values; empty means the style control is not rendered at all.</param>
/// <param name="MaxCount">Upper bound for <c>n</c>.</param>
/// <param name="MaxPromptLength">Prompt ceiling in characters. The host uses the very same value to
/// reject oversized input, so the textarea and the validation cannot drift apart.</param>
/// <param name="Backgrounds">Background values; empty means no background control.</param>
/// <param name="OutputFormats">Output file formats; empty means no output control.</param>
/// <param name="Moderations">Moderation levels; empty means no moderation control.</param>
/// <param name="SupportsOutputCompression">Whether <c>output_compression</c> is accepted.</param>
/// <param name="CustomSize">Constraints for a free-form width/height size, or null when the model
/// only accepts the preset cards.</param>
public sealed record ImageGenerationModelCapabilities(
    IReadOnlyList<string> Sizes,
    IReadOnlyList<ImageGenerationQuality> Qualities,
    IReadOnlyList<ImageGenerationStyle> Styles,
    int MaxCount,
    int MaxPromptLength = ImageGenerationModelCapabilities.DefaultMaxPromptLength,
    IReadOnlyList<ImageGenerationBackground>? Backgrounds = null,
    IReadOnlyList<ImageGenerationOutputFormat>? OutputFormats = null,
    IReadOnlyList<ImageGenerationModeration>? Moderations = null,
    bool SupportsOutputCompression = false,
    ImageGenerationCustomSizeRules? CustomSize = null)
{
    /// <summary>Conservative ceiling for gateways whose prompt limit is unknown.</summary>
    public const int DefaultMaxPromptLength = 1000;
}
