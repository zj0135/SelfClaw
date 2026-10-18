using FluentAssertions;
using Microsoft.Extensions.AI;
using SelfClaw.Infrastructure.Agents.Direct.Context;

namespace SelfClaw.Tests.Infrastructure.Agents.Direct.Context;

/// <summary>
/// A5: binary content has no text to tokenize, so every image is charged a constant instead of zero —
/// otherwise an image-heavy prompt would look small enough to skip compaction and the window check.
/// </summary>
public sealed class DirectTokenCounterAttachmentTests
{
    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x2A];

    [Fact]
    public void CountMessages_charges_a_constant_per_image()
    {
        var counter = new DirectTokenCounter();
        var textOnly = new ChatMessage(ChatRole.User, "look at this");
        var withImage = new ChatMessage(ChatRole.User, [new DataContent(PngBytes, "image/png"), new TextContent("look at this")]);

        var plain = counter.CountMessages([textOnly], null);
        var charged = counter.CountMessages([withImage], null);

        charged.Should().Be(plain + DirectTokenCounter.PerImageTokens);
    }

    [Fact]
    public void CountMessages_does_not_reuse_the_count_of_the_same_message_id_without_the_image()
    {
        var counter = new DirectTokenCounter();
        const string messageId = "message-id";
        var textOnly = new ChatMessage(ChatRole.User, "same text") { MessageId = messageId };
        var withImages = new ChatMessage(ChatRole.User, [new DataContent(PngBytes, "image/png"), new TextContent("same text")])
        {
            MessageId = messageId
        };

        var plain = counter.CountMessages([textOnly], null);
        counter.CountMessages([withImages], null).Should().Be(plain + DirectTokenCounter.PerImageTokens);
        counter.CountMessages([textOnly], null).Should().Be(plain, "the image count is part of the cache fingerprint");
    }
}
