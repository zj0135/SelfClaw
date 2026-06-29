using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Agents.Cli.Definitions;
using SelfClaw.Infrastructure.Agents.Cli.Definitions.Models;

namespace SelfClaw.Infrastructure.Agents.Cli.Session;

/// <summary>
/// Bridges <see cref="ICliAgentSessionStore"/> and <see cref="CliAgentDefinition"/> by turning a
/// conversation's persisted session id into the per-turn resume / new-session ids a definition's
/// <c>BuildArgs</c> consumes, and by persisting the id once a turn establishes it (plan.md §6, T4.5).
/// <para>
/// The two <see cref="ResumeStrategy"/> values differ in <em>when</em> the id is known:
/// </para>
/// <list type="bullet">
///   <item><see cref="ResumeStrategy.Specified"/> (Claude) — we know the id before the run. On a fresh
///         conversation we mint a UID, expose it as <see cref="CliRunContext.NewSessionId"/>, and persist
///         it eagerly so the next turn resumes via <see cref="CliRunContext.ResumeSessionId"/>.</item>
///   <item><see cref="ResumeStrategy.CapturedFromStream"/> (Codex / OpenCode) — the agent mints its own
///         id and reports it mid-stream. We resume with whatever we already stored and persist the id
///         captured from <see cref="RunStartedEvent.SessionId"/> after the fact.</item>
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
    /// For <see cref="ResumeStrategy.Specified"/> a UID is minted and persisted when none exists yet, so
    /// the returned context always carries a session id the run can pin.
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
                    return new CliSessionPlan(ResumeSessionId: stored, NewSessionId: null, PersistEagerly: false);

                var minted = Guid.NewGuid().ToString("D");
                await _sessionStore
                    .SetSessionIdAsync(conversationId, definition.Kind, minted, cancellationToken)
                    .ConfigureAwait(false);
                return new CliSessionPlan(ResumeSessionId: null, NewSessionId: minted, PersistEagerly: true);

            case ResumeStrategy.CapturedFromStream:
                // Resume with whatever we captured previously; a fresh conversation starts cold and the
                // id is persisted later via CaptureAsync once the stream reports it.
                return new CliSessionPlan(ResumeSessionId: stored, NewSessionId: null, PersistEagerly: false);

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(definition),
                    definition.ResumeStrategy,
                    "Unsupported resume strategy.");
        }
    }

    /// <summary>
    /// Persists a session id reported by the agent stream (<see cref="RunStartedEvent.SessionId"/>) for a
    /// <see cref="ResumeStrategy.CapturedFromStream"/> agent. No-op for <see cref="ResumeStrategy.Specified"/>
    /// (already persisted eagerly) and for blank ids.
    /// </summary>
    public async Task CaptureAsync(
        Guid conversationId,
        CliAgentDefinition definition,
        string? streamSessionId,
        CancellationToken cancellationToken = default)
    {
        if (definition.ResumeStrategy != ResumeStrategy.CapturedFromStream)
            return;
        if (string.IsNullOrEmpty(streamSessionId))
            return;

        await _sessionStore
            .SetSessionIdAsync(conversationId, definition.Kind, streamSessionId, cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>
/// The resume / new-session ids resolved for a single turn, ready to drop into <see cref="CliRunContext"/>.
/// <paramref name="PersistEagerly"/> records whether the id was already written to the store (true for a
/// freshly minted <see cref="ResumeStrategy.Specified"/> id).
/// </summary>
public sealed record CliSessionPlan(
    string? ResumeSessionId,
    string? NewSessionId,
    bool PersistEagerly);
