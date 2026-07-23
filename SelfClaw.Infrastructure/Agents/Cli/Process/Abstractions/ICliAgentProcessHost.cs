using SelfClaw.Infrastructure.Agents.Cli.Process.Models;
namespace SelfClaw.Infrastructure.Agents.Cli.Process.Abstractions;

/// <summary>
/// Launches a CLI agent subprocess for a single turn and returns a process session. The session exposes
/// stdin, stdout and the classified exit result while owning stderr collection, the inactivity watchdog
/// and process-tree termination.
/// <para>
/// Abstracted behind an interface so <c>CliAgentChatRuntime</c> can be wired against a
/// fake host in tests without spawning real processes.
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
