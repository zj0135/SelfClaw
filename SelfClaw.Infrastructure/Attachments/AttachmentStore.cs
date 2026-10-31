using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Infrastructure.Attachments;

internal sealed class AttachmentStore : IAttachmentStore
{
    private const int ReadChunkSize = 64 * 1024;

    private readonly string _root;

    public AttachmentStore(StoragePaths storagePaths)
    {
        ArgumentNullException.ThrowIfNull(storagePaths);
        _root = AttachmentPathResolver.ResolveRoot(storagePaths.AppDataDirectory);
    }

    public async Task<MessageAttachmentRecord> IngestAsync(
        Guid conversationId,
        Stream content,
        string fileName,
        string mediaType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);

        var normalizedMediaType = mediaType.Trim().ToLowerInvariant();
        if (!AttachmentPathResolver.TryResolveExtension(normalizedMediaType, out var extension))
        {
            throw new ArgumentException($"Unsupported attachment media type '{mediaType}'.", nameof(mediaType));
        }

        var bytes = await ReadBytesAsync(content, cancellationToken).ConfigureAwait(false);
        if (!ImageSignatures.Matches(bytes, normalizedMediaType))
        {
            throw new ArgumentException(
                $"Attachment content is not a valid {normalizedMediaType} file.", nameof(content));
        }

        var attachmentId = Guid.NewGuid();
        var directory = AttachmentPathResolver.ResolveConversationDirectory(_root, conversationId);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, attachmentId.ToString("D") + extension);
        await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);

        return new MessageAttachmentRecord(
            attachmentId,
            Guid.Empty,
            MessageAttachmentKind.Image,
            // Only the trailing name is persisted: the transcript shows a label, not a local path.
            Path.GetFileName(fileName.Trim()),
            normalizedMediaType,
            path,
            bytes.LongLength,
            DateTimeOffset.UtcNow);
    }

    public Task DeleteAsync(Guid conversationId, Guid attachmentId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = AttachmentPathResolver.ResolveConversationDirectory(_root, conversationId);
        if (!Directory.Exists(directory))
        {
            return Task.CompletedTask;
        }

        var prefix = attachmentId.ToString("D") + ".";
        foreach (var path in Directory.EnumerateFiles(directory, prefix + "*"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    public Task DeleteConversationAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = AttachmentPathResolver.ResolveConversationDirectory(_root, conversationId);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        return Task.CompletedTask;
    }

    private static async Task<byte[]> ReadBytesAsync(Stream content, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[ReadChunkSize];
        while (true)
        {
            var read = await content.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            // The stream is bounded while reading so an oversized upload never fills memory first.
            if (buffer.Length + read > AttachmentPathResolver.MaximumByteLength)
            {
                throw new ArgumentException(
                    $"Attachment exceeds the {AttachmentPathResolver.MaximumByteLength / (1024 * 1024)} MiB limit.", nameof(content));
            }

            buffer.Write(chunk, 0, read);
        }
    }
}
