namespace SelfClaw.Core.Models;

public sealed record ImageGenerationRequest(
    string Prompt,
    string? Model = null,
    int Count = 1,
    string Size = "1024x1024",
    ImageGenerationQuality Quality = ImageGenerationQuality.Standard,
    ImageGenerationStyle Style = ImageGenerationStyle.Vivid,
    ImageGenerationResponseFormat ResponseFormat = ImageGenerationResponseFormat.Bytes,
    string? User = null);
