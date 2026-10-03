using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services.ConversationInputs;

namespace SelfClaw.Tests.TestDoubles;

internal sealed class FakeConversationInputCoordinator : IConversationInputCoordinator
{
    public bool QueueEnabled { get; set; } = true;

    public Func<ConversationInputSubmission, ConversationInputSubmitResult>? Handler { get; set; }

    public List<ConversationInputSubmission> Submissions { get; } = [];

    public Guid LastInputId { get; } = Guid.NewGuid();

    public bool Paused { get; private set; }

    public long QueueRevision { get; private set; }

    public Task StopAsync(Guid conversationId) { Paused = true; return Task.CompletedTask; }

    public Task<ConversationInputSubmitResult> SubmitAsync(ConversationInputSubmission submission, CancellationToken cancellationToken = default)
    {
        Submissions.Add(submission);
        if (Handler is { } handler)
        {
            return Task.FromResult(handler(submission));
        }

        QueueRevision++;
        return Task.FromResult(new ConversationInputSubmitResult(true, LastInputId, 1, QueueRevision,
            ConversationInputSubmitOutcome.Started));
    }

    public Task<ConversationInputQueueState> GetStateAsync(Guid conversationId, CancellationToken cancellationToken = default)
        => Task.FromResult(new ConversationInputQueueState(conversationId, QueueRevision, Paused, null, false, []));

    public Task<ConversationInputQueueState> PauseAsync(Guid conversationId, long expectedQueueRevision, string? reason, CancellationToken cancellationToken = default)
    {
        Paused = true;
        QueueRevision++;
        return Task.FromResult(new ConversationInputQueueState(conversationId, QueueRevision, true, reason, false, []));
    }
}
