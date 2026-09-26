namespace SelfClaw.Desktop.Services.Transcript.Views;

public sealed record TranscriptToolHookView(
    string? EffectiveArgumentsText,
    IReadOnlyList<string> ArgumentsModifiedBy,
    IReadOnlyList<string> ApprovalRequiredBy,
    string? BlockedBy,
    string? BlockReason,
    IReadOnlyList<TranscriptHookNoteView> Feedback,
    IReadOnlyList<TranscriptHookNoteView> IgnoredFailures,
    string? OriginalArgumentsText = null);
