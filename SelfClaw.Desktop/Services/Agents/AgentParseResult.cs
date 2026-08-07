using SelfClaw.Core.Runtime;

namespace SelfClaw.Desktop.Services;

internal sealed record AgentParseResult(
    string Name,
    string Description,
    AgentExecutionMode Mode,
    string ToolPolicy,
    IReadOnlyList<string> PluginIds,
    IReadOnlyList<string> SkillIds,
    IReadOnlyList<string> DisabledSkillIds,
    IReadOnlyList<string> McpServerIds,
    IReadOnlyList<string> DisabledMcpServerIds,
    IReadOnlyList<string> SubagentIds,
    string Instructions,
    IReadOnlyList<string> Warnings);
