namespace SelfClaw.Core.Models;

/// <summary>
/// Background handling for the GPT Image family. Named after the wire values accepted by the
/// OpenAI Images protocol; which subset a model accepts is answered by
/// <see cref="ImageGenerationModelCapabilities"/>.
/// </summary>
public enum ImageGenerationBackground
{
    Auto = 0,
    Transparent = 1,
    Opaque = 2
}
