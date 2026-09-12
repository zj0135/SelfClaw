using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;

namespace SelfClaw.Tests.TestDoubles;

internal sealed class ActivityTaskCoordinator : ISubagentTaskCoordinator
{
    internal List<SubagentTaskCommand> Commands { get; } = [];
    internal TaskCompletionSource<SubagentTaskView> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<SubagentTaskView> CancelAsync(SubagentTaskCommand command, CancellationToken cancellationToken = default)
    {
        Commands.Add(command);
        return Completion.Task;
    }
    public Task<SubagentTaskView> StartAsync(SubagentTaskStartRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task<SubagentTaskView?> GetAsync(SubagentTaskQuery query, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task<SubagentTaskView> RetryAsync(SubagentTaskRetryRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}
