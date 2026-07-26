namespace SelfClaw.Core.Models;

public sealed record McpConfigurationEntryView(
    string Key,
    string? Value,
    bool IsSecret,
    bool HasSecret);
