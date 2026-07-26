namespace SelfClaw.Core.Models;

public sealed record McpKeyValueCommand(
    string Key,
    string? Value,
    bool IsSecret,
    bool ClearSecret = false);
