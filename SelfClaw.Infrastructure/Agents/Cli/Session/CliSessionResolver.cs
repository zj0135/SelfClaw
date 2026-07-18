using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Agents.Cli.Definitions;
using SelfClaw.Infrastructure.Agents.Cli.Definitions.Models;
using SelfClaw.Infrastructure.Agents.Cli.Session.Models;

namespace SelfClaw.Infrastructure.Agents.Cli.Session;

/// <summary>
/// Bridges <see cref="ICliAgentSessionStore"/> and <see cref="CliAgentDefinition"/> by turning a
/// conversation's persisted session id into the per-turn resume / new-session ids a definition's
/// <c>BuildArgs</c> consumes, and by persisting the id once a turn establishes it (plan.md §6, T4.5).
/// <para>
/// The two <see cref="ResumeStrategy"/> values differ in <em>who</em> mints the id:
/// </para>
/// <list type="bullet">
///   <item><see cref="ResumeStrategy.Specified"/> (Claude) — we mint a UID on a fresh conversation and
///         pass it via <see cref="CliRunContext.NewSessionId"/>. It is persisted only once the agent's
///         stream confirms the session exists (<see cref="RunStartedEvent.SessionId"/> from the init
///         event); persisting before launch would leave a dead id behind when the CLI never starts,
///         making every later turn resume a session that does not exist.</item>
///   <item><see cref="ResumeStrategy.CapturedFromStream"/> (Codex / OpenCode) — the agent mints its own
///         id and reports it mid-stream. We resume with whatever we already stored and persist the
///         captured id after the fact.</item>
/// </list>
/// </summary>
public sealed class CliSessionResolver
{
    private readonly ICliAgentSessionStore _sessionStore;

    public CliSessionResolver(ICliAgentSessionStore sessionStore)
    {
        _sessionStore = sessionStore;
    }

    /// <summary>
    /// Resolves the resume / new-session ids for the next turn of <paramref name="conversationId"/>.
    /// For <see cref="ResumeStrategy.Specified"/> a UID is minted when none is stored yet; it is not
    /// persisted here — that happens in <see cref="CaptureAsync"/> once the stream confirms it.
    /// </summary>
    public async Task<CliSessionPlan> PrepareAsync(
        Guid conversationId,
        CliAgentDefinition definition,
        CancellationToken cancellationToken = default)
    {
        var stored = await _sessionStore
            .GetSessionIdAsync(conversationId, definition.Kind, cancellationToken)
            .ConfigureAwait(false);

        switch (definition.ResumeStrategy)
        {
            case ResumeStrategy.Specified:
                if (!string.IsNullOrEmpty(stored))
                    return new CliSessionPlan(ResumeSessionId: stored, NewSessionId: null);

                return new CliSessionPlan(ResumeSessionId: null, NewSessionId: Guid.NewGuid().ToString("D"));

            case ResumeStrategy.CapturedFromStream:
                // Resume with whatever we captured previously; a fresh conversation starts cold and the
                // id is persisted later via CaptureAsync once the stream reports it.
                return new CliSessionPlan(ResumeSessionId: stored, NewSessionId: null);

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(definition),
                    definition.ResumeStrategy,
                    "Unsupported resume strategy.");
        }
    }

    /// <summary>
    /// Persists the session id reported by the agent stream (<see cref="RunStartedEvent.SessionId"/>).
    /// Applies to both strategies: for <see cref="ResumeStrategy.CapturedFromStream"/> it is the only
    /// source of the id, and for <see cref="ResumeStrategy.Specified"/> it confirms the minted id (or a
    /// forked id on resume) belongs to a session that actually exists. No-op for blank ids.
    /// </summary>
    public async Task CaptureAsync(
        Guid conversationId,
        CliAgentDefinition definition,
        string? streamSessionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(streamSessionId))
            return;

        await _sessionStore
            .SetSessionIdAsync(conversationId, definition.Kind, streamSessionId, cancellationToken)
            .ConfigureAwait(false);
    }
}
