namespace SelfClaw.Core.Models;

public sealed record McpServerSettings(
    string? Command,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectoryMode,
    bool RequiresWorkspace,
    IReadOnlyDictionary<string, string> Environment,
    string? Endpoint,
    string? TransportMode,
    int? ConnectionTimeoutSeconds,
    IReadOnlyDictionary<string, string> Headers,
    IReadOnlyList<string> SecretFieldNames,
    IReadOnlyList<string>? RequiredFieldNames = null);
