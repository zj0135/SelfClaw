using System.Runtime.CompilerServices;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;

namespace SelfClaw.Infrastructure.Agents.Runtime;

/// <summary>
/// The <see cref="IAgentChatRuntime"/> seen by the rest of the app. It dispatches a turn to the concrete
/// runtime for the requested <see cref="AgentExecutionMode"/> (plan.md §8, T5.2): <see cref="AgentExecutionMode.Cli"/>
/// goes to <see cref="Cli.CliAgentChatRuntime"/> and <see cref="AgentExecutionMode.Direct"/> goes to
/// the in-process <see cref="DirectAgentChatRuntime"/>.
/// </summary>
public sealed class DispatchingAgentChatRuntime : IAgentChatRuntime
{
    private readonly Cli.CliAgentChatRuntime _cliRuntime;
    private readonly DirectAgentChatRuntime _directRuntime;

    public DispatchingAgentChatRuntime(
        Cli.CliAgentChatRuntime cliRuntime,
        DirectAgentChatRuntime directRuntime)
    {
        _cliRuntime = cliRuntime;
        _directRuntime = directRuntime;
    }

    public IAsyncEnumerable<AgentStreamEvent> StreamTurnAsync(
        ChatTurnRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.Agent.Mode switch
        {
            AgentExecutionMode.Cli => _cliRuntime.StreamTurnAsync(request, cancellationToken),
            AgentExecutionMode.Direct => _directRuntime.StreamTurnAsync(request, cancellationToken),
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
            ErrorMessage: $"Execution mode '{mode}' is not supported.");
    }
}
