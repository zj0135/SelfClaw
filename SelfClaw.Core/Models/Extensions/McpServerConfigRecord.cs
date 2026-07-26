namespace SelfClaw.Core.Models;

public sealed record McpServerConfigRecord(
    string Id,
    string DisplayName,
    McpTransportKind Transport,
    string SettingsJson,
    IReadOnlyDictionary<string, string> CredentialRefs,
    string? SourcePluginId,
    bool IsEnabled,
    long ConfigRevision,
    IReadOnlyList<string> DiscoveredTools,
    McpServerHealthStatus LastStatus,
    string? LastError,
    DateTimeOffset? LastCheckedAtUtc,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
