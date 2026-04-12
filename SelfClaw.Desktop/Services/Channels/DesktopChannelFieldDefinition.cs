namespace SelfClaw.Desktop.Services;

public sealed record DesktopChannelFieldDefinition(
    string Key,
    string Label,
    DesktopChannelFieldKind Kind,
    bool Required = false,
    string? Description = null,
    string? Placeholder = null);
