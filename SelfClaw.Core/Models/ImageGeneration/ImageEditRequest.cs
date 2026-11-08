using System;

namespace SelfClaw.Core.Models;

public sealed record ImageEditRequest(
    ReadOnlyMemory<byte> Image,
    string Prompt,
    ReadOnlyMemory<byte>? Mask = null,
    string? ImageFileName = null,
    string? MaskFileName = null,
    string? Model = null,
    int Count = 1,
    string Size = "1024x1024",
    ImageGenerationResponseFormat ResponseFormat = ImageGenerationResponseFormat.Bytes,
    string? User = null);
