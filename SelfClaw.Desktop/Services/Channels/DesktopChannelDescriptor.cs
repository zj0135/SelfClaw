namespace SelfClaw.Desktop.Services;

public sealed record DesktopChannelDescriptor(
    string Id,
    string Name,
    string Description,
    string DefaultDisplayName,
    IReadOnlyList<DesktopChannelFieldDefinition> Fields);
