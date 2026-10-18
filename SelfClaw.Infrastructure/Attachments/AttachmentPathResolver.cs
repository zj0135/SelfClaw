using SelfClaw.Core.Models;

namespace SelfClaw.Infrastructure.Attachments;

/// <summary>
/// Single source of truth for the attachment layout: the writer, the transcript URL projector, the
/// desktop bridge and the cleanup paths must agree on it, or files land where nothing can read them.
/// </summary>
public static class AttachmentPathResolver
{
    /// <summary>
    /// Virtual host the transcript renderer loads attachments from. It lives here because
    /// <see cref="CreateSourceUrl"/> is what turns a stored file into a renderable URL.
    /// </summary>
    public const string AttachmentHostName = "attachments.selfclaw.local";

    /// <summary>The one authoritative single-image ceiling: the store, the bridge and the queue all read it.</summary>
    public const int MaximumByteLength = 10 * 1024 * 1024;

    public const string PngMediaType = "image/png";
    public const string JpegMediaType = "image/jpeg";
    public const string GifMediaType = "image/gif";
    public const string WebpMediaType = "image/webp";

    /// <summary>The media types a submission may carry; anything else is refused before a byte is stored.</summary>
    public static readonly IReadOnlySet<string> AllowedMediaTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        PngMediaType,
        JpegMediaType,
        GifMediaType,
        WebpMediaType
    };

    private const string AttachmentsDirectoryName = "attachments";

    private static readonly IReadOnlyDictionary<string, string> ExtensionByMediaType =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [PngMediaType] = ".png",
            [JpegMediaType] = ".jpg",
            [GifMediaType] = ".gif",
            [WebpMediaType] = ".webp"
        };

    private static readonly IReadOnlyDictionary<string, string> MediaTypeByExtension =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = PngMediaType,
            [".jpg"] = JpegMediaType,
            [".jpeg"] = JpegMediaType,
            [".gif"] = GifMediaType,
            [".webp"] = WebpMediaType
        };

    public static string ResolveRoot(string appDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataDirectory);
        return Path.Combine(appDataDirectory, AttachmentsDirectoryName);
    }

    public static string ResolveConversationDirectory(string root, Guid conversationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        return Path.Combine(root, conversationId.ToString("D"));
    }

    public static bool TryResolveExistingFile(string root, string relativePath, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(root)
            || string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathRooted(relativePath))
        {
            return false;
        }

        try
        {
            var normalizedRoot = Path.GetFullPath(root);
            var candidate = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath));
            // Same containment rule as TranscriptMessageProjector: a ".." relative result, or an
            // absolute one (path on another volume), means the file escaped the attachment root.
            var contained = Path.GetRelativePath(normalizedRoot, candidate);
            if (contained.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(contained))
            {
                return false;
            }

            if (!File.Exists(candidate))
            {
                return false;
            }

            fullPath = candidate;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Maps an absolute storage path back to a root-relative path. The queue persists only this form, so a
    /// stored reference can never point outside the attachment root.
    /// </summary>
    public static bool TryCreateRelativePath(string root, string fullPath, out string relativePath)
    {
        relativePath = string.Empty;
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(fullPath) || !Path.IsPathRooted(fullPath))
        {
            return false;
        }

        string candidate;
        try
        {
            candidate = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(fullPath));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }

        if (candidate.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(candidate) || candidate == ".")
        {
            return false;
        }

        relativePath = candidate;
        return true;
    }

    /// <summary>
    /// Resolves a persisted absolute storage path to a file inside the attachment root. The queue stores
    /// relative references but messages store absolute paths, so both forms must share one containment rule.
    /// </summary>
    public static bool TryResolveStoredFile(string root, string storagePath, out string fullPath)
    {
        fullPath = string.Empty;
        return TryCreateRelativePath(root, storagePath, out var relativePath)
            && TryResolveExistingFile(root, relativePath, out fullPath);
    }

    /// <summary>
    /// Derives the on-disk location from a reference alone, so the sender never gets to name a path.
    /// </summary>
    public static string CreateRelativePath(Guid conversationId, Guid attachmentId, string mediaType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);
        if (conversationId == Guid.Empty || attachmentId == Guid.Empty)
        {
            throw new ArgumentException("An attachment reference requires non-empty identifiers.", nameof(conversationId));
        }

        return TryResolveExtension(mediaType, out var extension)
            ? Path.Combine(conversationId.ToString("D"), attachmentId.ToString("D") + extension)
            : throw new ArgumentException($"Unsupported attachment media type '{mediaType}'.", nameof(mediaType));
    }

    /// <summary>
    /// Builds the persisted row for a stored reference. Containment, existence and the skip policy
    /// stay with the caller; only the record shape is shared, so the queue, the boundary and the
    /// test doubles cannot drift apart.
    /// </summary>
    public static MessageAttachmentRecord CreateRecord(AttachmentReference reference, string fullPath,
        Guid messageId, DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        return new MessageAttachmentRecord(reference.AttachmentId, messageId, MessageAttachmentKind.Image,
            reference.FileName, reference.MediaType, fullPath, reference.ByteLength, createdAtUtc);
    }

    public static bool TryResolveExtension(string mediaType, out string extension)
        => ExtensionByMediaType.TryGetValue(mediaType?.Trim() ?? string.Empty, out extension!);

    public static bool TryResolveMediaType(string fileName, out string mediaType)
        => MediaTypeByExtension.TryGetValue(Path.GetExtension(fileName ?? string.Empty), out mediaType!);

    public static string CreateSourceUrl(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        var normalizedPath = relativePath
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
        return $"https://{AttachmentHostName}/{Uri.EscapeDataString(normalizedPath).Replace("%2F", "/", StringComparison.OrdinalIgnoreCase)}";
    }

    /// <summary>
    /// Maps a persisted absolute storage path to the transcript URL. Containment and existence go through
    /// <see cref="TryResolveExistingFile"/> so the writer, the projector and the bridge keep one rule.
    /// </summary>
    public static bool TryCreateSourceUrl(string root, string fullPath, out string? sourceUrl)
    {
        sourceUrl = null;
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(fullPath))
        {
            return false;
        }

        string relativePath;
        try
        {
            relativePath = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(fullPath));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }

        if (!TryResolveExistingFile(root, relativePath, out _))
        {
            return false;
        }

        sourceUrl = CreateSourceUrl(relativePath);
        return true;
    }
}
