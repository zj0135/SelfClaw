using SelfClaw.Core.Runtime;

namespace SelfClaw.Core.Interfaces;

public interface IAgentChatRuntime
{
    IAsyncEnumerable<ChatRuntimeEvent> StreamTurnAsync(
        ChatTurnRequest request,
        CancellationToken cancellationToken = default);
}