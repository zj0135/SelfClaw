using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;

namespace SelfClaw.Infrastructure.Agents.Runtime.Abstractions;

internal interface IAgentRuntimeAdapter
{
    AgentExecutionMode Mode { get; }

    IAsyncEnumerable<AgentStreamEvent> StreamTurnAsync(
        ChatTurnRequest request,
        CancellationToken cancellationToken = default);
}
