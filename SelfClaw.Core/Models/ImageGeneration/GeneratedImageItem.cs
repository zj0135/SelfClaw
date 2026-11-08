using System;

namespace SelfClaw.Core.Models;

public sealed record GeneratedImageItem(
    ReadOnlyMemory<byte>? ImageBytes,
    Uri? ImageUri,
    string? RevisedPrompt);
