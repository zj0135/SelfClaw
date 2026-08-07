using SelfClaw.Core.Runtime;

namespace SelfClaw.Desktop.Services;

public sealed record DesktopAgentDefinition(
    string Id,
    string Name,
    string Description,
    AgentExecutionMode Mode,
    string ToolPolicy,
    IReadOnlyList<string> PluginIds,
    IReadOnlyList<string> SkillIds,
    IReadOnlyList<string> McpServerIds,
    IReadOnlyList<string> SubagentIds,
    string Instructions,
    string FilePath,
    bool IsBuiltIn,
    IReadOnlyList<string> Warnings);
