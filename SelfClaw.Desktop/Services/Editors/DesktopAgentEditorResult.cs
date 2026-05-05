using SelfClaw.Core.Runtime;

namespace SelfClaw.Desktop.Services;

public sealed record DesktopAgentEditorResult(
    string? OriginalAgentId,
    string AgentId,
    string Name,
    string Description,
    AgentExecutionMode Mode,
    string ToolPolicy,
    IReadOnlyList<string> Skills,
    IReadOnlyList<string> DisabledSkills,
    IReadOnlyList<string> McpServers,
    IReadOnlyList<string> DisabledMcpServers,
    string Instructions);
