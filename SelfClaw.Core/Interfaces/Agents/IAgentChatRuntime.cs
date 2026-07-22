using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;

namespace SelfClaw.Core.Interfaces;

public interface IAgentChatRuntime
{
    /// <summary>
    /// Streams a turn whose successful or failed completion is represented by exactly one final
    /// <see cref="RunCompletedEvent"/>. Cancellation propagates as <see cref="OperationCanceledException"/>.
    /// </summary>
    IAsyncEnumerable<AgentStreamEvent> StreamTurnAsync(
        ChatTurnRequest request,
        CancellationToken cancellationToken = default);
}
