namespace SelfClaw.Core.Models;

/// <summary>
/// An edit uses the same output knobs as generation, but has no style switch. The source image is
/// always required; a mask is deliberately not exposed because the workbench has no drawing UI.
/// </summary>
public sealed record ImageEditRequest(
    ReadOnlyMemory<byte> Image,
    string Prompt,
    int Count = 1,
    string? Size = null,
    ImageGenerationQuality? Quality = null,
    ImageGenerationBackground? Background = null,
    ImageGenerationOutputFormat? OutputFormat = null,
    int? OutputCompression = null);
