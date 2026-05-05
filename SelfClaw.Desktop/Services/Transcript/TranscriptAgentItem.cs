namespace SelfClaw.Desktop.Services;

public sealed record TranscriptAgentItem(
    string Id,
    string Name,
    string Description,
    string Mode,
    string ToolPolicy,
    IReadOnlyList<string> Skills,
    IReadOnlyList<string> DisabledSkills,
    IReadOnlyList<string> McpServers,
    IReadOnlyList<string> DisabledMcpServers,
    string Instructions,
    string FilePath,
    bool IsBuiltIn,
    IReadOnlyList<string> Warnings);
