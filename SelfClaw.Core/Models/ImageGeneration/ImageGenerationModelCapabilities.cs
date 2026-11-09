using System.Collections.Generic;

namespace SelfClaw.Core.Models;

/// <summary>
/// What one image model actually accepts, so the workbench can build its controls from the model
/// instead of from a fixed form, and the client can refuse a mismatched request with a readable
/// message rather than letting the provider answer 400.
/// </summary>
public sealed record ImageGenerationModelCapabilities(
    IReadOnlyList<string> Sizes,
    IReadOnlyList<ImageGenerationQuality> Qualities,
    IReadOnlyList<ImageGenerationStyle> Styles,
    int MaxCount);
