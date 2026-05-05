using SelfClaw.Core.Runtime;

namespace SelfClaw.Desktop.Services;

public sealed record DesktopAgentDefinition(
    string Id,
    string Name,
    string Description,
    AgentExecutionMode Mode,
    string ToolPolicy,
    IReadOnlyList<string> Skills,
    IReadOnlyList<string> McpServers,
    string Instructions,
    string FilePath,
    bool IsBuiltIn,
    IReadOnlyList<string> Warnings)
{
    public bool HasWarnings => Warnings.Count > 0;

    public AgentRuntimeDefinition ToRuntimeDefinition()
        => new(
            Id,
            Name,
            Description,
            Mode,
            ToolPolicy,
            Skills,
            McpServers,
            Instructions);
}
