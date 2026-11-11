namespace SelfClaw.Core.Models;

/// <summary>
/// Output file format for the GPT Image family. <c>output_compression</c> only applies to
/// <see cref="Jpeg"/> and <see cref="Webp"/>.
/// </summary>
public enum ImageGenerationOutputFormat
{
    Png = 0,
    Jpeg = 1,
    Webp = 2
}
