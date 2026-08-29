namespace SelfClaw.Core.Models;

public sealed record PluginPanelView(
    string Key,
    string PluginId,
    string PanelId,
    string Title,
    string Icon,
    string Origin,
    string Url,
    int DefaultWidth,
    bool Enabled,
    ExtensionStatus Status,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<string> NetworkOrigins);
