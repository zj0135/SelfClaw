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
        if (!MatchesDeclaredMediaType(bytes, normalizedMediaType))
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

    private static bool MatchesDeclaredMediaType(byte[] bytes, string mediaType) => mediaType switch
    {
        AttachmentPathResolver.PngMediaType => StartsWith(bytes, [0x89, 0x50, 0x4E, 0x47]),
        AttachmentPathResolver.JpegMediaType => StartsWith(bytes, [0xFF, 0xD8, 0xFF]),
        AttachmentPathResolver.GifMediaType => StartsWith(bytes, [0x47, 0x49, 0x46, 0x38]),
        // WebP hides its marker behind the four RIFF size bytes.
        AttachmentPathResolver.WebpMediaType => StartsWith(bytes, [0x52, 0x49, 0x46, 0x46]) && MatchesAt(bytes, 8, [0x57, 0x45, 0x42, 0x50]),
        _ => false
    };

    private static bool StartsWith(byte[] bytes, ReadOnlySpan<byte> signature)
        => bytes.AsSpan().StartsWith(signature);

    private static bool MatchesAt(byte[] bytes, int offset, ReadOnlySpan<byte> signature)
        => bytes.Length >= offset + signature.Length
            && bytes.AsSpan(offset, signature.Length).SequenceEqual(signature);
}
