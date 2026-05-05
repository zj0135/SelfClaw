namespace SelfClaw.Core.Runtime;

public sealed record AgentRuntimeDefinition(
    string Id,
    string Name,
    string Description,
    AgentExecutionMode Mode,
    string ToolPolicy,
    IReadOnlyList<string> Skills,
    IReadOnlyList<string> McpServers,
    string Instructions)
{
    public const string SystemToolPolicy = "system";
}
