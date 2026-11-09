using System;

namespace SelfClaw.Core.Models;

public sealed record ImageEditRequest(
    ReadOnlyMemory<byte> Image,
    string Prompt,
    int Count = 1,
    string? Size = null);
