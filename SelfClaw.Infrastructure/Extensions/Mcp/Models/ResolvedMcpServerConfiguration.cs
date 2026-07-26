using SelfClaw.Core.Models;

namespace SelfClaw.Infrastructure.Extensions.Mcp.Models;

internal sealed record ResolvedMcpServerConfiguration(
    string Id,
    string DisplayName,
    McpTransportKind Transport,
    long ConfigRevision,
    string? SourcePluginId,
    bool IsAvailable,
    string? UnavailableReason,
    string? Command,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    Uri? Endpoint,
    string? TransportMode,
    TimeSpan? ConnectionTimeout,
    IReadOnlyDictionary<string, string> Headers,
    string? WorkspacePath);
