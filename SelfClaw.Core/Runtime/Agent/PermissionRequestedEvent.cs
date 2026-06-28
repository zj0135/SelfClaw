namespace SelfClaw.Core.Runtime.Agent;

/// <summary>
/// Reserved for future approval gating (e.g. via a permission-prompt MCP / ACP).
/// Not emitted by the first CLI version, which runs the agent autonomously.
/// </summary>
public sealed record PermissionRequestedEvent(
    string RequestId,
    string ToolName,
    string ArgumentsJson,
    string? Description) : AgentStreamEvent;
