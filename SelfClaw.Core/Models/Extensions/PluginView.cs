namespace SelfClaw.Core.Models;

public sealed record PluginView(
    string Key,
    string PluginId,
    string ViewId,
    string Title,
    string Icon,
    PluginViewSlot Slot,
    string Origin,
    string Url,
    int? DefaultWidth,
    bool Enabled,
    ExtensionStatus Status,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<string> NetworkOrigins);
