namespace SelfClaw.Core.Models;

/// <summary>
/// Nullable knobs mean "use the model default": the client fills them from
/// <see cref="ImageGenerationModelCapabilities"/>, so a caller never has to know which vocabulary the
/// selected model speaks.
/// </summary>
public sealed record ImageGenerationRequest(
    string Prompt,
    int Count = 1,
    string? Size = null,
    ImageGenerationQuality? Quality = null,
    ImageGenerationStyle? Style = null,
    ImageGenerationBackground? Background = null,
    ImageGenerationOutputFormat? OutputFormat = null,
    int? OutputCompression = null,
    ImageGenerationModeration? Moderation = null);
