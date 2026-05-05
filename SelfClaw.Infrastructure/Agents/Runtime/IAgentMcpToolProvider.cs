using SelfClaw.Core.Runtime;

namespace SelfClaw.Infrastructure.Agents.Runtime;

internal interface IAgentMcpToolProvider
{
    Task<ResolvedMcpTools> CreateToolsAsync(
        ChatTurnRequest request,
        CancellationToken cancellationToken);
}
