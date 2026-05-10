namespace SelfClaw.Desktop.Services;

public sealed record SlashCommandDefinition(
    string Id,
    string Command,
    string Name,
    string Description,
    string? ArgumentHint = null,
    bool RequiresConfirmation = false);
