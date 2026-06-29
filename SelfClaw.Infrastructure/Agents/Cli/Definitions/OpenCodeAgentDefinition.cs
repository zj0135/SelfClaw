using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Agents.Cli.Definitions.Models;

namespace SelfClaw.Infrastructure.Agents.Cli.Definitions;

/// <summary>
/// The <see cref="CliAgentDefinition"/> for OpenCode (plan.md 阶段 7, T7.3), mirroring Open Design's
/// <c>runtimes/defs/opencode.ts</c>.
/// <para>
/// Launched as <c>opencode run --format json</c>:
/// <list type="bullet">
///   <item><c>run</c> — non-interactive execution mode.</item>
///   <item><c>--format json</c> — OpenCode emits the newline-delimited JSON event stream consumed by
///         <c>JsonEventStreamParser</c> (<c>step_start</c> / <c>text</c> / <c>tool</c> / <c>step_finish</c>).</item>
/// </list>
/// OpenCode mints its own session id and reports it via <c>step_start.sessionID</c>, so resume uses
/// <see cref="ResumeStrategy.CapturedFromStream"/>: the captured id is replayed on the next turn via
/// <c>-s &lt;session_id&gt;</c> (plan.md §6). The prompt is delivered as a single plain-text line on stdin;
/// the runtime closes stdin afterwards to end the turn.
/// </para>
/// </summary>
public static class OpenCodeAgentDefinition
{
    public static CliAgentDefinition Create() => new()
    {
        Kind = CliAgentKind.OpenCode,
        Command = "opencode",
        InputFormat = CliInputFormat.Text,
        StreamFormat = CliStreamFormat.JsonEventStream,
        ResumeStrategy = ResumeStrategy.CapturedFromStream,
        BuildArgs = BuildArgs,
        BuildStdinLines = BuildStdinLines,
    };

    private static IReadOnlyList<string> BuildArgs(CliRunContext context)
    {
        var args = new List<string> { "run", "--format", "json" };

        // CapturedFromStream: resume the session OpenCode minted on an earlier turn via `-s <id>`. There is
        // no "new session id" to pin — OpenCode assigns it and reports it via step_start in the stream.
        if (!string.IsNullOrEmpty(context.ResumeSessionId))
        {
            args.Add("-s");
            args.Add(context.ResumeSessionId);
        }

        return args;
    }

    /// <summary>
    /// Delivers the prompt as a single plain-text line on stdin (<see cref="CliInputFormat.Text"/>). The
    /// runtime closes stdin afterwards to signal EOF and end the turn.
    /// </summary>
    private static IReadOnlyList<string> BuildStdinLines(string prompt) => new[] { prompt };
}
