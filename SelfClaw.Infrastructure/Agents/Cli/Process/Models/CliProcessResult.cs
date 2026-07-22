using SelfClaw.Core.Runtime.Agent;

namespace SelfClaw.Infrastructure.Agents.Cli.Process.Models;

/// <summary>
/// The terminal outcome of a CLI agent turn, derived from process exit state: exit 0 maps to
/// <see cref="RunCompletionStatus.Succeeded"/> and any other exit (including inactivity kill) maps
/// to <see cref="RunCompletionStatus.Failed"/>. Caller cancellation propagates separately as
/// <see cref="OperationCanceledException"/>.
/// </summary>
public sealed record CliProcessResult(
    RunCompletionStatus Status,
    int? ExitCode,
    string? StandardError)
{
    /// <summary>True when the process was killed because it exceeded the inactivity timeout.</summary>
    public bool TimedOut { get; init; }
}
