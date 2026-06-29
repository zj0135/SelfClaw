namespace SelfClaw.Infrastructure.Agents.Cli.Definitions.Models;

/// <summary>
/// How a CLI agent expects the prompt to be delivered on stdin. Determines what the
/// runtime writes before closing stdin (plan.md §5).
/// </summary>
public enum CliInputFormat
{
    /// <summary>Newline-delimited JSON user-message records (Claude <c>--input-format stream-json</c>).</summary>
    StreamJson = 0,

    /// <summary>A single plain-text prompt.</summary>
    Text = 1
}
