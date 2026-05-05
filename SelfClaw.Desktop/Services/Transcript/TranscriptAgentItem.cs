namespace SelfClaw.Desktop.Services;

public sealed record TranscriptAgentItem(
    string Id,
    string Name,
    string Description,
    string Mode,
    string ToolPolicy,
    IReadOnlyList<string> Skills,
    IReadOnlyList<string> McpServers,
    string Instructions,
    string FilePath,
    bool IsBuiltIn,
    IReadOnlyList<string> Warnings);
