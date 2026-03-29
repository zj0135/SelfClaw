namespace SelfClaw.Desktop.Services;

public sealed record ShellSelectOption(
    string Id,
    string Label,
    string? Description = null);
