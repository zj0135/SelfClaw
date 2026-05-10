namespace SelfClaw.Desktop.Services;

public sealed record TranscriptSlashCommandItem(
    string Id,
    string Command,
    string Name,
    string Description,
    string? ArgumentHint,
    bool RequiresConfirmation);
