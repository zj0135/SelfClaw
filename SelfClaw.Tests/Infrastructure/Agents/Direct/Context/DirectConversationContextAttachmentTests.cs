using FluentAssertions;
using Microsoft.Extensions.AI;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Agents.Direct.Context;
using SelfClaw.Infrastructure.Agents.Direct.Context.Models;
using SelfClaw.Infrastructure.Agents.Direct.Tools.Models;

namespace SelfClaw.Tests.Infrastructure.Agents.Direct.Context;

/// <summary>
/// A5: a consumed steer that carries images replays exactly like a replayed history message — attachments
/// first, text last — and stays protected for the rest of the turn.
/// </summary>
public sealed class DirectConversationContextAttachmentTests
{
    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x2A];

    [Fact]
    public void AppendInputs_places_the_steer_image_before_its_text()
    {
        var context = new DirectConversationContext(null, [], []);
        var steer = Steer(sequence: 1, "look at this instead");

        context.AppendInputs([steer], AttachmentContents(steer.Id));

        var message = context.Messages.Should().ContainSingle().Subject;
        message.Role.Should().Be(ChatRole.User);
        message.Contents.Should().HaveCount(2);
        message.Contents[0].Should().BeOfType<DataContent>().Which.MediaType.Should().Be("image/png");
        message.Contents[1].Should().BeOfType<TextContent>().Which.Text.Should().Be("look at this instead");
    }

    [Fact]
    public void A_steer_image_survives_in_turn_compaction()
    {
        var context = new DirectConversationContext(null, [Unit(1, "replayed")], []);
        var steer = Steer(sequence: 1, "with a picture");
        context.AppendInputs([steer], AttachmentContents(steer.Id));
        context.AppendResponse(new ChatResponse(new ChatMessage(ChatRole.Assistant, "answer")),
            new Dictionary<string, DirectToolBinding>());

        context.ApplyInTurnCompaction("summarized", coveredUnitCount: 2);

        var steerMessage = context.Messages.Single(message => message.Contents.OfType<DataContent>().Any());
        steerMessage.Contents.Should().HaveCount(2);
        steerMessage.Contents[1].Should().BeOfType<TextContent>().Which.Text.Should().Be("with a picture");
    }

    [Fact]
    public void AppendInputs_without_attachments_keeps_the_plain_text_message()
    {
        var context = new DirectConversationContext(null, [], []);

        context.AppendInputs([Steer(sequence: 1, "plain steer")]);

        var message = context.Messages.Should().ContainSingle().Which;
        message.Contents.Should().ContainSingle().Which.Should().BeOfType<TextContent>();
    }

    private static IReadOnlyDictionary<Guid, IReadOnlyList<AIContent>> AttachmentContents(Guid messageId)
        => new Dictionary<Guid, IReadOnlyList<AIContent>>
        {
            [messageId] = [new DataContent(PngBytes, "image/png") { Name = "photo.png" }]
        };

    private static MessageRecord Steer(long sequence, string text)
        => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), sequence, MessageRole.User, text,
            MessageStatus.Sealed, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static DirectPromptHistoryUnit Unit(long sequence, string text)
        => new([new ChatMessage(ChatRole.User, text)], sequence);
}
