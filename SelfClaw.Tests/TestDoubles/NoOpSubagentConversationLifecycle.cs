using SelfClaw.Desktop.Services.Subagents;

namespace SelfClaw.Tests.TestDoubles;

internal sealed class NoOpSubagentConversationLifecycle : ISubagentConversationLifecycle
{
    public Task CancelAndWaitAsync(
        Guid parentConversationId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
