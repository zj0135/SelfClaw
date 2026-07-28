namespace SelfClaw.Core.Models;

public sealed record McpServerView(
    string Id,
    string Name,
    string Transport,
    bool Enabled,
    string? SourcePluginId,
    IReadOnlyList<string> AssignedAgentIds,
    ExtensionStatus Status,
    string? LastError,
    IReadOnlyList<string> Tools,
    string? Command,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectoryMode,
    bool RequiresWorkspace,
    IReadOnlyList<McpConfigurationEntryView> Environment,
    string? Endpoint,
    string? TransportMode,
    int? ConnectionTimeoutSeconds,
    IReadOnlyList<McpConfigurationEntryView> Headers,
    DateTimeOffset? LastCheckedAtUtc = null);
