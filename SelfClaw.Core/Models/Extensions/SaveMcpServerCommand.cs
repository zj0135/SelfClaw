namespace SelfClaw.Core.Models;

public sealed record SaveMcpServerCommand(
    string? Id,
    string DisplayName,
    McpTransportKind Transport,
    string? Command,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectoryMode,
    bool RequiresWorkspace,
    IReadOnlyList<McpKeyValueCommand> Environment,
    string? Endpoint,
    string? TransportMode,
    int? ConnectionTimeoutSeconds,
    IReadOnlyList<McpKeyValueCommand> Headers,
    bool? Enabled = null);
