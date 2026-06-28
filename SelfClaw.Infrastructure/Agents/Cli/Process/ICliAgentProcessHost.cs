namespace SelfClaw.Infrastructure.Agents.Cli.Process;

/// <summary>
/// Launches a CLI agent subprocess for a single turn and exposes its stdout as a line stream.
/// Implementations own the lifecycle described in plan.md §5: write the JSONL prompt to stdin,
/// close stdin to signal EOF, surface stdout line-by-line for the parser, collect stderr for
/// diagnostics, enforce the no-activity watchdog, and classify the exit code.
/// <para>
/// Abstracted behind an interface so <c>CliAgentChatRuntime</c> (阶段 5) can be wired against a
/// fake host in tests (T8.4) without spawning real processes.
/// </para>
/// </summary>
public interface ICliAgentProcessHost
{
    /// <summary>
    /// Starts the process described by <paramref name="startInfo"/> and returns a live session whose
    /// <see cref="ICliAgentProcessSession.ReadOutputLinesAsync"/> yields stdout lines until the
    /// process exits. The caller writes the prompt via the returned session, then disposes it.
    /// </summary>
    ICliAgentProcessSession Start(CliProcessStartInfo startInfo, CancellationToken cancellationToken = default);
}
