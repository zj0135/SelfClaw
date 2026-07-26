namespace SelfClaw.Core.Models;

public sealed record ExtensionPackageRecord(
    ExtensionKind Kind,
    string Id,
    string DisplayName,
    string Version,
    string Description,
    string InstallPath,
    string ContentHash,
    string ManifestJson,
    string? SourcePluginId,
    bool IsEnabled,
    string? AcknowledgedPermissionsJson,
    DateTimeOffset? AcknowledgedAtUtc,
    DateTimeOffset InstalledAtUtc,
    DateTimeOffset UpdatedAtUtc);
