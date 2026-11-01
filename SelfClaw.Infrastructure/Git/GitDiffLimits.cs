namespace SelfClaw.Infrastructure.Git;

internal static class GitDiffLimits
{
    /// <summary>Per-scope changed-file cap; the response reports the truncation instead of hiding it.</summary>
    internal const int MaximumFiles = 500;

    /// <summary>Per-file patch cap. The patch travels to the WebView as one message.</summary>
    internal const int MaximumPatchCharacters = 512 * 1024;
}
