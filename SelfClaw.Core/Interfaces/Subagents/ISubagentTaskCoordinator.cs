using SelfClaw.Core.Models;

namespace SelfClaw.Core.Interfaces;

public interface ISubagentTaskCoordinator
{
    Task<SubagentTaskView> StartAsync(
        SubagentTaskStartRequest request,
        CancellationToken cancellationToken = default);

    Task<SubagentTaskView?> GetAsync(
        SubagentTaskQuery query,
        CancellationToken cancellationToken = default);

    Task<SubagentTaskView> CancelAsync(
        SubagentTaskCommand command,
        CancellationToken cancellationToken = default);

    Task<SubagentTaskView> RetryAsync(
        SubagentTaskRetryRequest request,
        CancellationToken cancellationToken = default);
}
