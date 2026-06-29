using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Agents.Cli.Definitions.Models;

namespace SelfClaw.Infrastructure.Agents.Cli.Definitions;

/// <summary>
/// The <see cref="CliAgentDefinition"/> for Codex (plan.md 阶段 7, T7.2), mirroring Open Design's
/// <c>runtimes/defs/codex.ts</c>.
/// <para>
/// Launched as <c>codex exec --json --skip-git-repo-check</c>:
/// <list type="bullet">
///   <item><c>exec</c> — non-interactive execution mode.</item>
///   <item><c>--json</c> — Codex emits the newline-delimited JSON event stream consumed by
///         <c>JsonEventStreamParser</c> (<c>thread.*</c> / <c>turn.*</c> / <c>item.*</c> events).</item>
///   <item><c>--skip-git-repo-check</c> — run even when the workspace is not a git repo, matching how
///         SelfClaw drives the CLI against arbitrary workspaces.</item>
/// </list>
/// Codex mints its own thread id and reports it via <c>thread.started.thread_id</c>, so resume uses
/// <see cref="ResumeStrategy.CapturedFromStream"/>: the captured id is replayed on the next turn as
/// <c>exec resume &lt;thread_id&gt;</c> (plan.md §6). The prompt is delivered as a single plain-text line on
/// stdin; the runtime closes stdin afterwards to end the turn.
/// </para>
/// </summary>
public static class CodexAgentDefinition
{
    public static CliAgentDefinition Create() => new()
    {
        Kind = CliAgentKind.Codex,
        Command = "codex",
        InputFormat = CliInputFormat.Text,
        StreamFormat = CliStreamFormat.JsonEventStream,
        ResumeStrategy = ResumeStrategy.CapturedFromStream,
        BuildArgs = BuildArgs,
        BuildStdinLines = BuildStdinLines,
    };

    private static IReadOnlyList<string> BuildArgs(CliRunContext context)
    {
        var args = new List<string> { "exec" };

        // CapturedFromStream: resume the thread Codex minted on an earlier turn via the `exec resume`
        // subcommand, which must immediately follow `exec` (`codex exec resume <id> [options]`). There is
        // no "new session id" to pin — Codex assigns it and reports it via thread.started in the stream.
        if (!string.IsNullOrEmpty(context.ResumeSessionId))
        {
            args.Add("resume");
            args.Add(context.ResumeSessionId);
        }

        args.Add("--json");
        args.Add("--skip-git-repo-check");

        return args;
    }

    /// <summary>
    /// Delivers the prompt as a single plain-text line on stdin (<see cref="CliInputFormat.Text"/>). The
    /// runtime closes stdin afterwards to signal EOF and end the turn.
    /// </summary>
    private static IReadOnlyList<string> BuildStdinLines(string prompt) => new[] { prompt };
}
