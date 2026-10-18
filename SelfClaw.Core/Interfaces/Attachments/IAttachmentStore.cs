using SelfClaw.Core.Models;

namespace SelfClaw.Core.Interfaces;

public interface IAttachmentStore
{
    Task<MessageAttachmentRecord> IngestAsync(
        Guid conversationId,
        Stream content,
        string fileName,
        string mediaType,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid conversationId, Guid attachmentId, CancellationToken cancellationToken = default);

    Task DeleteConversationAsync(Guid conversationId, CancellationToken cancellationToken = default);
}
