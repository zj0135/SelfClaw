using SelfClaw.Core.Runtime.Agent;

namespace SelfClaw.Core.Interfaces;

/// <summary>
/// Persists the resume identifier a CLI agent associates with a conversation, so
/// subsequent turns can resume the same underlying session instead of starting cold.
/// Keyed by conversation and <see cref="CliAgentKind"/> because a single conversation
/// may target different CLIs over its lifetime.
/// </summary>
public interface ICliAgentSessionStore
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
}
