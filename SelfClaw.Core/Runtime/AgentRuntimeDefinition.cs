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
    IReadOnlyList<string> SubagentIds,
    string Instructions)
{
    public const string SystemToolPolicy = "system";

    /// <summary>No tool may run.</summary>
    public const string NoneToolPolicy = "none";

    /// <summary>Only <see cref="Agent.ToolCallKind.List"/>, <c>Search</c> and <c>Read</c> tools may run.</summary>
    public const string ReadOnlyToolPolicy = "read-only";
}
