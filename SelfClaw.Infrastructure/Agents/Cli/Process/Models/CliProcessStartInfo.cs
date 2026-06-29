namespace SelfClaw.Infrastructure.Agents.Cli.Process.Models;

/// <summary>
/// Everything <see cref="CliAgentProcessHost"/> needs to launch a single CLI agent turn:
/// the resolved command, the directory the agent runs in, environment overrides (used to pass
/// API keys / base URLs to the child without leaking them onto the command line) and the
/// no-activity watchdog window.
/// </summary>
public sealed record CliProcessStartInfo
{
    /// <summary>The launch-ready command produced by <see cref="CliCommandResolver"/>.</summary>
    public required CommandInvocation Invocation { get; init; }

    /// <summary>Working directory for the child process (the workspace root). Defaults to the temp dir.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Environment variables layered onto the inherited environment. A <c>null</c> value removes the
    /// variable from the child. Used to inject provider keys / base URLs without exposing them in argv.
    /// </summary>
    public IReadOnlyDictionary<string, string?>? Environment { get; init; }

    /// <summary>
    /// How long the child may go without producing any stdout/stderr before it is treated as hung and
    /// killed. Resets on every line of output. Defaults to 10 minutes (plan.md §5).
    /// </summary>
    public TimeSpan InactivityTimeout { get; init; } = TimeSpan.FromMinutes(10);
}
