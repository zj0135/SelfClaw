using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Agents.Direct.Abstractions;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Infrastructure.Attachments;

internal sealed class AttachmentContentReader : IAttachmentContentReader
{
    private readonly string _root;
    private readonly ILogger<AttachmentContentReader> _logger;

    public AttachmentContentReader(StoragePaths storagePaths, ILogger<AttachmentContentReader>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(storagePaths);
        _root = AttachmentPathResolver.ResolveRoot(storagePaths.AppDataDirectory);
        _logger = logger ?? NullLogger<AttachmentContentReader>.Instance;
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<AIContent>>> ReadAsync(
        IReadOnlyList<MessageRecord> messages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var contents = new Dictionary<Guid, IReadOnlyList<AIContent>>();
        foreach (var message in messages)
        {
            if (message.Role != MessageRole.User || message.Attachments is not { Count: > 0 } attachments)
            {
                continue;
            }

            var images = new List<AIContent>(attachments.Count);
            foreach (var attachment in attachments)
            {
                if (attachment.Kind != MessageAttachmentKind.Image)
                {
                    continue;
                }

                if (await ReadImageAsync(message, attachment, cancellationToken).ConfigureAwait(false) is { } image)
                {
                    images.Add(image);
                }
            }

            if (images.Count > 0)
            {
                contents[message.Id] = images;
            }
        }

        return contents;
    }

    private async Task<DataContent?> ReadImageAsync(
        MessageRecord message,
        MessageAttachmentRecord attachment,
        CancellationToken cancellationToken)
    {
        // Containment and existence share the writer's rule: a row may only point inside the attachment root.
        if (!AttachmentPathResolver.TryResolveStoredFile(_root, attachment.StoragePath, out var path))
        {
            SkipAttachment(message, attachment, "is outside the attachment root or no longer exists");
            return null;
        }

        if (!AttachmentPathResolver.AllowedMediaTypes.Contains(attachment.MediaType))
        {
            SkipAttachment(message, attachment, $"carries the unsupported media type '{attachment.MediaType}'");
            return null;
        }

        try
        {
            if (new FileInfo(path).Length > AttachmentPathResolver.MaximumByteLength)
            {
                SkipAttachment(message, attachment, "exceeds the single-image byte limit");
                return null;
            }

            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            return new DataContent(bytes, attachment.MediaType) { Name = attachment.FileName };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                exception,
                "Attachment {AttachmentId} of message {MessageId} could not be read; the turn continues without it.",
                attachment.Id,
                message.Id);
            return null;
        }
    }

    private void SkipAttachment(MessageRecord message, MessageAttachmentRecord attachment, string reason)
        => _logger.LogWarning(
            "Attachment {AttachmentId} of message {MessageId} {Reason}; the turn continues without it.",
            attachment.Id,
            message.Id,
            reason);
}
