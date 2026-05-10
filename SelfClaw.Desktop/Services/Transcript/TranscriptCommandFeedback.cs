namespace SelfClaw.Desktop.Services;

public sealed record TranscriptCommandFeedback(
    string Level,
    string Message,
    string? CommandId = null,
    string? Arguments = null,
    bool RequiresConfirmation = false);
