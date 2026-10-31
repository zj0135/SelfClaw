using FluentAssertions;
using SelfClaw.Infrastructure.Attachments;

namespace SelfClaw.Tests.Infrastructure.Attachments;

public sealed class ImageSignaturesTests
{
    private static readonly byte[] PngHeader = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] JpegHeader = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46];
    private static readonly byte[] GifHeader = [0x47, 0x49, 0x46, 0x38, 0x39, 0x61];
    private static readonly byte[] WebpHeader = [0x52, 0x49, 0x46, 0x46, 0x24, 0x00, 0x00, 0x00, 0x57, 0x45, 0x42, 0x50];

    [Theory]
    [InlineData("image/png")]
    [InlineData("image/jpeg")]
    [InlineData("image/gif")]
    [InlineData("image/webp")]
    public void TryDetect_and_Matches_agree_on_every_whitelisted_format(string expectedMediaType)
    {
        var bytes = HeaderFor(expectedMediaType);

        ImageSignatures.TryDetect(bytes, out var detected).Should().BeTrue();

        detected.Should().Be(expectedMediaType);
        ImageSignatures.Matches(bytes, expectedMediaType).Should().BeTrue();
    }

    [Fact]
    public void Matches_rejects_a_signature_that_does_not_belong_to_the_declared_media_type()
    {
        ImageSignatures.Matches(PngHeader, "image/jpeg").Should().BeFalse();
        ImageSignatures.Matches(JpegHeader, "image/gif").Should().BeFalse();
        ImageSignatures.Matches(GifHeader, "image/webp").Should().BeFalse();
        ImageSignatures.Matches(WebpHeader, "image/png").Should().BeFalse();
    }

    [Fact]
    public void TryDetect_rejects_content_that_is_not_a_whitelisted_image()
    {
        var text = "not an image at all"u8.ToArray();

        ImageSignatures.TryDetect(text, out var mediaType).Should().BeFalse();
        mediaType.Should().BeEmpty();
    }

    [Fact]
    public void Matches_rejects_an_unsupported_declared_media_type()
    {
        ImageSignatures.Matches(PngHeader, "image/tiff").Should().BeFalse();
        ImageSignatures.Matches(PngHeader, "text/plain").Should().BeFalse();
    }

    [Theory]
    [InlineData("image/png")]
    [InlineData("image/jpeg")]
    [InlineData("image/gif")]
    [InlineData("image/webp")]
    public void Truncated_headers_never_claim_a_match(string mediaType)
    {
        var full = HeaderFor(mediaType);

        // Every proper prefix is too short to carry the whole signature, so nothing may match.
        for (var length = 0; length < SignatureLength(mediaType); length++)
        {
            var truncated = full.AsSpan(0, length);
            ImageSignatures.TryDetect(truncated, out _).Should().BeFalse();
            ImageSignatures.Matches(truncated, mediaType).Should().BeFalse();
        }
    }

    [Fact]
    public void Webp_requires_the_marker_behind_the_riff_size_bytes()
    {
        var riffOnly = WebpHeader.AsSpan(0, 8);

        ImageSignatures.TryDetect(riffOnly, out _).Should().BeFalse();
        ImageSignatures.Matches(WebpHeader, "image/webp").Should().BeTrue();
    }

    private static byte[] HeaderFor(string mediaType) => mediaType switch
    {
        "image/png" => PngHeader,
        "image/jpeg" => JpegHeader,
        "image/gif" => GifHeader,
        _ => WebpHeader
    };

    private static int SignatureLength(string mediaType) => mediaType switch
    {
        "image/png" => 4,
        "image/jpeg" => 3,
        "image/gif" => 4,
        _ => 12
    };
}
