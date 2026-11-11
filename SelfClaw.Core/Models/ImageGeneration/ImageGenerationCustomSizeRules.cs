namespace SelfClaw.Core.Models;

/// <summary>
/// Constraints for a free-form <c>WIDTHxHEIGHT</c> size. The GPT Image 2/2.5 families accept any
/// dimensions within these bounds; the client validates before sending so an invalid combination
/// never turns into an opaque provider 400.
/// </summary>
public sealed record ImageGenerationCustomSizeRules(
    int MultipleOf,
    int MinAspectRatioDividend,
    int MaxAspectRatioDividend,
    int MaxWidth,
    int MaxHeight)
{
    /// <summary>Documented limit: 16-pixel multiples, ratio between 1:3 and 3:1, up to 3840x2160.</summary>
    public static ImageGenerationCustomSizeRules GptImage =>
        new(MultipleOf: 16, MinAspectRatioDividend: 1, MaxAspectRatioDividend: 3, MaxWidth: 3840, MaxHeight: 2160);
}
