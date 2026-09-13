using System.Runtime.CompilerServices;
using System.Threading.Channels;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;

namespace SelfClaw.Tests.TestDoubles;

internal sealed class ControlledSubagentRuntime : IAgentChatRuntime
{
    private readonly Channel<(AgentStreamEvent Event, TaskCompletionSource Applied)> _events = Channel.CreateUnbounded<(AgentStreamEvent, TaskCompletionSource)>();
    internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal ChatTurnRequest? Request { get; private set; }

    internal async Task EmitAsync(AgentStreamEvent streamEvent)
    {
        var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _events.Writer.WriteAsync((streamEvent, applied));
        await applied.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    public async IAsyncEnumerable<AgentStreamEvent> StreamTurnAsync(ChatTurnRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Request = request;
        Started.TrySetResult();
        await foreach (var item in _events.Reader.ReadAllAsync(cancellationToken))
        {
            try
            {
                yield return item.Event;
            }
            finally
            {
                item.Applied.TrySetResult();
            }

            if (item.Event is RunCompletedEvent)
            {
                yield break;
            }
        }
    }
}
