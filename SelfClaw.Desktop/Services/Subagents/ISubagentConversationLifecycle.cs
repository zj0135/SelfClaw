namespace SelfClaw.Desktop.Services.Subagents;

internal interface ISubagentConversationLifecycle
{
    Task CancelAndWaitAsync(
        Guid parentConversationId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}
