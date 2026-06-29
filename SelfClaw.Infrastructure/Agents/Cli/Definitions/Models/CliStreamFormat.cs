namespace SelfClaw.Infrastructure.Agents.Cli.Definitions.Models;

/// <summary>
/// Identifies the stdout stream format a CLI agent produces, used to pick the matching
/// <c>IAgentStreamParser</c> in 阶段 5.
/// </summary>
public enum CliStreamFormat
{
    /// <summary>Claude Code <c>--output-format stream-json</c> (parsed by <c>ClaudeStreamJsonParser</c>).</summary>
    ClaudeStreamJson = 0,

    /// <summary>Codex / OpenCode JSON event stream (parsed by <c>JsonEventStreamParser</c>, 阶段 7).</summary>
    JsonEventStream = 1
}
