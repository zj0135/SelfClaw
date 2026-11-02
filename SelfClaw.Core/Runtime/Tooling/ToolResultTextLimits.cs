namespace SelfClaw.Core.Runtime.Tooling;

/// <summary>
/// The single authority for how much tool-result text one call may carry. The same limit bounds what the
/// model reads in the running turn and what is persisted for the next one, so a result can never be one size
/// when the model saw it and another size when the following turn replays it — the context meter reports the
/// prompt the provider actually measured, and that prompt must be reproducible from storage.
/// </summary>
public static class ToolResultTextLimits
{
    /// <summary>Characters, not bytes: the stored column is text and the transcript shows characters.</summary>
    public const int MaximumCharacters = 64 * 1024;

    /// <summary>Appended in place of the removed tail so both the model and the reader know text is missing.</summary>
    public const string TruncationMarker = "\n[SelfClaw truncated the tool result at 64 KiB.]";

    /// <summary>
    /// Trims to the limit and marks the cut. Idempotent: a value already within the limit comes back unchanged,
    /// so applying the limit twice (once at the tool boundary, once before persistence) cannot shorten twice.
    /// </summary>
    public static string? Limit(string? value)
        => value is null || value.Length <= MaximumCharacters
            ? value
            : string.Concat(value.AsSpan(0, MaximumCharacters - TruncationMarker.Length), TruncationMarker);
}
