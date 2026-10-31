namespace SelfClaw.Infrastructure.Attachments;

/// <summary>
/// The one magic-byte authority for the whitelisted image formats: the attachment ingest path and
/// <c>read_file</c> must agree on what counts as an image, or a file could be accepted as one and
/// rejected as the other. Extensions never decide.
/// </summary>
internal static class ImageSignatures
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF];
    private static readonly byte[] Gif = [0x47, 0x49, 0x46, 0x38];
    private static readonly byte[] Riff = [0x52, 0x49, 0x46, 0x46];
    private static readonly byte[] WebpMarker = [0x57, 0x45, 0x42, 0x50];

    internal static bool TryDetect(ReadOnlySpan<byte> header, out string mediaType)
    {
        if (header.StartsWith(Png))
        {
            mediaType = AttachmentPathResolver.PngMediaType;
            return true;
        }

        if (header.StartsWith(Jpeg))
        {
            mediaType = AttachmentPathResolver.JpegMediaType;
            return true;
        }

        if (header.StartsWith(Gif))
        {
            mediaType = AttachmentPathResolver.GifMediaType;
            return true;
        }

        if (IsWebp(header))
        {
            mediaType = AttachmentPathResolver.WebpMediaType;
            return true;
        }

        mediaType = string.Empty;
        return false;
    }

    internal static bool Matches(ReadOnlySpan<byte> bytes, string mediaType) => mediaType switch
    {
        AttachmentPathResolver.PngMediaType => bytes.StartsWith(Png),
        AttachmentPathResolver.JpegMediaType => bytes.StartsWith(Jpeg),
        AttachmentPathResolver.GifMediaType => bytes.StartsWith(Gif),
        // WebP hides its marker behind the four RIFF size bytes.
        AttachmentPathResolver.WebpMediaType => IsWebp(bytes),
        _ => false
    };

    private static bool IsWebp(ReadOnlySpan<byte> bytes)
        => bytes.StartsWith(Riff)
            && bytes.Length >= 12
            && bytes.Slice(8, 4).SequenceEqual(WebpMarker);
}
