using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;

namespace SelfClaw.Core.Interfaces;

public interface IAgentChatRuntime
{
    IAsyncEnumerable<AgentStreamEvent> StreamTurnAsync(
        ChatTurnRequest request,
        CancellationToken cancellationToken = default);
}
