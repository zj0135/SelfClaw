namespace SelfClaw.Infrastructure.Agents.Cli.Definitions.Models;

/// <summary>
/// How a CLI agent resumes an earlier conversation across turns (plan.md §6).
/// </summary>
public enum ResumeStrategy
{
    /// <summary>
    /// We mint the session id ourselves and pass it in. Claude Code: <c>--session-id &lt;uid&gt;</c> on the
    /// first turn, <c>--resume &lt;uid&gt;</c> on subsequent turns. The id is known before the run starts,
    /// so we persist it eagerly.
    /// </summary>
    Specified = 0,

    /// <summary>
    /// The agent mints its own session id and reports it in the stream (e.g. Codex
    /// <c>thread.started.thread_id</c>). We capture it from <c>RunStartedEvent.SessionId</c> and pass it
    /// back via a resume flag on the next turn. Reserved for Codex / OpenCode (阶段 7).
    /// </summary>
    CapturedFromStream = 1
}
