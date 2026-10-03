using SelfClaw.Core.Models;

namespace SelfClaw.Desktop.Services.ConversationInputs;

/// <summary>
/// The view-model facing input surface. The view model captures selection, prepares the workspace
/// and submits; it never starts a turn or owns queue state.
/// </summary>
internal interface IConversationInputCoordinator
{
    bool QueueEnabled { get; }

    Task StopAsync(Guid conversationId);

    Task<ConversationInputSubmitResult> SubmitAsync(ConversationInputSubmission submission, CancellationToken cancellationToken = default);

    Task<ConversationInputQueueState> GetStateAsync(Guid conversationId, CancellationToken cancellationToken = default);

    Task<ConversationInputQueueState> PauseAsync(Guid conversationId, long expectedQueueRevision, string? reason, CancellationToken cancellationToken = default);
}
