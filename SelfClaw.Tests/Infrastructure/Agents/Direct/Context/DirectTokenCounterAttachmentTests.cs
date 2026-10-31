using FluentAssertions;
using Microsoft.Extensions.AI;
using SelfClaw.Infrastructure.Agents.Direct.Context;

namespace SelfClaw.Tests.Infrastructure.Agents.Direct.Context;

/// <summary>
/// A5: binary content has no text to tokenize, so every image is charged a constant instead of zero —
/// otherwise an image-heavy prompt would look small enough to skip compaction and the window check. A tool
/// result on the native delivery path nests its image inside the result, and the same rule has to hold there.
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

    [Fact]
    public void A_tool_result_nested_image_is_charged_the_constant_and_never_its_base64()
    {
        const string header = "Read image 'shot.png' (image/png, 70 bytes). The image follows.";
        var textOnly = ToolMessage([new TextContent(header)]);
        var withImage = ToolMessage(
            [new TextContent(header), new DataContent(PngBytes, "image/png") { Name = "shot.png" }]);

        var plain = new DirectTokenCounter().CountMessages([textOnly], null);
        var charged = new DirectTokenCounter().CountMessages([withImage], null);

        charged.Should().Be(plain + DirectTokenCounter.PerImageTokens,
            "a nested image is charged the constant, exactly like a top-level one");
        var huge = ToolMessage([new TextContent(header), new DataContent(new byte[64 * 1024], "image/png")]);
        new DirectTokenCounter().CountMessages([huge], null).Should().Be(charged,
            "the charge never scales with the image size, so base64 can never inflate the count");
    }

    [Fact]
    public void The_nested_image_count_is_part_of_the_cache_fingerprint()
    {
        const string messageId = "nested-image-message";
        var single = new ChatMessage(ChatRole.Tool,
            [
                new FunctionResultContent("call-1",
                    new List<AIContent> { new TextContent("header"), new DataContent(PngBytes, "image/png") })
            ])
            {
                MessageId = messageId
            };
        var pair = new ChatMessage(ChatRole.Tool,
            [
                new FunctionResultContent("call-1",
                    new List<AIContent>
                    {
                        new TextContent("header"),
                        new DataContent(PngBytes, "image/png"),
                        new DataContent(PngBytes, "image/png")
                    })
            ])
            {
                MessageId = messageId
            };

        var counter = new DirectTokenCounter();
        var oneImage = counter.CountMessages([single], null);
        counter.CountMessages([pair], null).Should().Be(oneImage + DirectTokenCounter.PerImageTokens);
        counter.CountMessages([single], null).Should().Be(oneImage,
            "the image count is part of the cache fingerprint, not only the text");
    }

    private static ChatMessage ToolMessage(IReadOnlyList<AIContent> contents)
        => new(ChatRole.Tool, [new FunctionResultContent("call-1", contents)]);
}
