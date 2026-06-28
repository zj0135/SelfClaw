namespace SelfClaw.Core.Runtime.Agent;

/// <summary>
/// High-level lifecycle phase reported by an agent run. Maps loosely across CLI
/// agents (Claude Code, Codex, OpenCode) and any future agent framework.
/// </summary>
public enum AgentRunStatus
{
    Initializing = 0,
    Requesting = 1,
    Thinking = 2,
    Running = 3
}
