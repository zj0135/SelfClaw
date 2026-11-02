namespace SelfClaw.Desktop.Services.Transcript;

/// <summary>
/// The transcript panel is a display surface: it shows a short head of a long tool result and says so. The
/// value that reaches the model is bounded elsewhere (<c>ToolResultTextLimits</c>), so trimming here can never
/// change what a later turn replays.
/// </summary>
internal static class TranscriptToolResultDisplayLimiter
{
    internal const int MaximumDisplayedCharacters = 24 * 1024;
    private const string DisplayTruncationSuffix = "\n[SelfClaw truncated the displayed tool result at 24 KiB.]";

    public static string LimitDisplayed(string value)
        => value.Length <= MaximumDisplayedCharacters
            ? value
            : string.Concat(value.AsSpan(0, MaximumDisplayedCharacters - DisplayTruncationSuffix.Length), DisplayTruncationSuffix);
}
