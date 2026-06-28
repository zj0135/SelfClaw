using System.Runtime.CompilerServices;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;

namespace SelfClaw.Infrastructure.Agents.Runtime;

/// <summary>
/// Temporary no-op <see cref="IAgentChatRuntime"/> used during the Direct→CLI rewrite
/// (plan.md 阶段 0). It keeps the solution compiling and launchable before the real
/// <c>CliAgentChatRuntime</c> lands in 阶段 5. It emits a single failed run so the UI
/// surfaces a clear message instead of hanging.
/// </summary>
public sealed class PlaceholderAgentChatRuntime : IAgentChatRuntime
{
    public async IAsyncEnumerable<AgentStreamEvent> StreamTurnAsync(
        ChatTurnRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;

        yield return new RunStartedEvent(SessionId: null, Model: null, AgentKind: CliAgentKind.Claude);
        yield return new RunCompletedEvent(
            RunCompletionStatus.Failed,
            "Agent runtime is not wired yet. The CLI runtime is under construction (plan.md 阶段 5).");
    }
}
