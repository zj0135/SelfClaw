namespace SelfClaw.Core.Models;

public sealed record ExtensionPackageView(
    ExtensionKind Kind,
    string Id,
    string Name,
    string Version,
    string Description,
    bool Enabled,
    string? SourcePluginId,
    IReadOnlyList<string> AssignedAgentIds,
    ExtensionStatus Status,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<string> UnacknowledgedPermissions);
