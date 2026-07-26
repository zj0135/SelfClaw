namespace SelfClaw.Core.Runtime;

public sealed record AgentRuntimeDefinition(
    string Id,
    string Name,
    string Description,
    AgentExecutionMode Mode,
    string ToolPolicy,
    IReadOnlyList<string> PluginIds,
    IReadOnlyList<string> SkillIds,
    IReadOnlyList<string> McpServerIds,
    string Instructions)
{
    public const string SystemToolPolicy = "system";
}
