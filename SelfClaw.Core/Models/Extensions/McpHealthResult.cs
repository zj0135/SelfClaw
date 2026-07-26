namespace SelfClaw.Core.Models;

public sealed record McpHealthResult(
    string Id,
    McpServerHealthStatus Status,
    double? LatencyMilliseconds,
    string? Error,
    IReadOnlyList<string> Tools);
