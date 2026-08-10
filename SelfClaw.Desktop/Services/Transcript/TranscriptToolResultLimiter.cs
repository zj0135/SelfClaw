namespace SelfClaw.Desktop.Services.Transcript;

internal static class TranscriptToolResultLimiter
{
    internal const int MaximumStoredCharacters = 64 * 1024;
    internal const int MaximumDisplayedCharacters = 24 * 1024;
    private const string StoredTruncationSuffix = "\n[SelfClaw truncated the stored tool result at 64 KiB.]";
    private const string DisplayTruncationSuffix = "\n[SelfClaw truncated the displayed tool result at 24 KiB.]";

    public static string? LimitStored(string? value)
        => value is null ? null : Truncate(value, MaximumStoredCharacters, StoredTruncationSuffix);

    public static string LimitDisplayed(string value)
        => Truncate(value, MaximumDisplayedCharacters, DisplayTruncationSuffix);

    private static string Truncate(string value, int maximumCharacters, string suffix)
        => value.Length <= maximumCharacters
            ? value
            : string.Concat(value.AsSpan(0, maximumCharacters - suffix.Length), suffix);
}
