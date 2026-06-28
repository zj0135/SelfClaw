using System.Runtime.CompilerServices;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;

namespace SelfClaw.Infrastructure.Agents.Runtime;

/// <summary>
/// The <see cref="IAgentChatRuntime"/> seen by the rest of the app. It dispatches a turn to the concrete
/// runtime for the requested <see cref="AgentExecutionMode"/> (plan.md §8, T5.2): <see cref="AgentExecutionMode.Cli"/>
/// goes to <see cref="Cli.CliAgentChatRuntime"/>. <see cref="AgentExecutionMode.Direct"/> is reserved for the
/// future in-process rewrite and currently has no runtime, so a Direct turn fails cleanly rather than hanging.
/// </summary>
public sealed class DispatchingAgentChatRuntime : IAgentChatRuntime
{
    private readonly IAgentChatRuntime _cliRuntime;

    public DispatchingAgentChatRuntime(Cli.CliAgentChatRuntime cliRuntime)
    {
        _cliRuntime = cliRuntime;
    }

    public IAsyncEnumerable<AgentStreamEvent> StreamTurnAsync(
        ChatTurnRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.Agent.Mode switch
        {
            AgentExecutionMode.Cli => _cliRuntime.StreamTurnAsync(request, cancellationToken),
            _ => UnsupportedMode(request.Agent.Mode),
        };
    }

    private static async IAsyncEnumerable<AgentStreamEvent> UnsupportedMode(
        AgentExecutionMode mode,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        _ = cancellationToken;

        yield return new RunCompletedEvent(
            RunCompletionStatus.Failed,
            FinalText: null,
            ErrorMessage: mode == AgentExecutionMode.Direct
                ? "The Direct execution mode is not available yet; select a CLI agent."
                : $"Execution mode '{mode}' is not supported.");
    }
}
