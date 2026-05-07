using SelfClaw.Core.Runtime;

namespace SelfClaw.Infrastructure.Agents.Runtime.Mcp;

internal interface IAgentMcpToolProvider
{
    Task<ResolvedMcpTools> CreateToolsAsync(
        ChatTurnRequest request,
        CancellationToken cancellationToken);
}
