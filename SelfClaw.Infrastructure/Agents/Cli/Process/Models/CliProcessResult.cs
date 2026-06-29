using SelfClaw.Core.Runtime.Agent;

namespace SelfClaw.Infrastructure.Agents.Cli.Process.Models;

/// <summary>
/// The terminal outcome of a CLI agent turn, derived from the process exit code and cancellation
/// state per plan.md §5: exit 0 → <see cref="RunCompletionStatus.Succeeded"/>, cancellation →
/// <see cref="RunCompletionStatus.Canceled"/>, any other exit (or inactivity kill) →
/// <see cref="RunCompletionStatus.Failed"/>.
/// </summary>
public sealed record CliProcessResult(
    RunCompletionStatus Status,
    int? ExitCode,
    string? StandardError)
{
    /// <summary>True when the process was killed because it exceeded the inactivity timeout.</summary>
    public bool TimedOut { get; init; }
}
