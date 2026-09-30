using SelfClaw.Core.Runtime.Agent;

namespace SelfClaw.Infrastructure.Agents.Cli.Session.Abstractions;

internal interface ICliAgentSessionStore
{
    Task<string?> GetSessionIdAsync(
        Guid conversationId,
        CliAgentKind agentKind,
        CancellationToken cancellationToken = default);

    Task SetSessionIdAsync(
        Guid conversationId,
        CliAgentKind agentKind,
        string sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the CLI's cumulative session cost and returns the part attributable to the current turn.
    /// Used by CLIs that report a running session total rather than a per-turn cost.
    /// </summary>
    Task<long> TrackCumulativeCostAsync(
        Guid conversationId,
        CliAgentKind agentKind,
        string sessionId,
        long cumulativeCostUsdMicros,
        CancellationToken cancellationToken = default);
}
