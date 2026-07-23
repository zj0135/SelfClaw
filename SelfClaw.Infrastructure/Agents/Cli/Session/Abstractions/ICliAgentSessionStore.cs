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
}
