using FluentAssertions;
using Microsoft.Extensions.AI;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Infrastructure.Agents.Direct.Context;
using SelfClaw.Infrastructure.Agents.Direct.Context.Models;

namespace SelfClaw.Tests.Infrastructure.Agents.Direct.Context;

/// <summary>
/// A5: user attachments must reach the provider as content, not only as transcript rows. The image goes
/// first and the text last, a message carrying only images still produces a unit, and an image-bearing unit
/// is planned into a summary like any other unit.
/// </summary>
public sealed class DirectPromptComposerAttachmentTests
{
    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x2A];

    [Fact]
    public void BuildUnits_places_the_image_before_the_text_of_one_user_message()
    {
        var (messages, turns) = CreateHistory((MessageRole.User, "what is in this picture?"));

        var units = DirectHistoryReplay.BuildUnits(messages, turns, [], NoAdjustments(),
            AttachmentContents(messages[0].Id));

        var unit = units.Should().ContainSingle().Subject;
        var message = unit.Messages.Should().ContainSingle().Subject;
        message.Role.Should().Be(ChatRole.User);
        message.Contents.Should().HaveCount(2);
        var image = message.Contents[0].Should().BeOfType<DataContent>().Subject;
        image.MediaType.Should().Be("image/png");
        image.Name.Should().Be("photo.png");
        image.Data.ToArray().Should().Equal(PngBytes);
        message.Contents[1].Should().BeOfType<TextContent>().Which.Text.Should().Be("what is in this picture?");
    }

    [Fact]
    public void BuildUnits_keeps_a_user_message_that_carries_only_images()
    {
        var (messages, turns) = CreateHistory((MessageRole.User, string.Empty));

        var units = DirectHistoryReplay.BuildUnits(messages, turns, [], NoAdjustments(),
            AttachmentContents(messages[0].Id));

        var message = units.Should().ContainSingle().Which.Messages.Should().ContainSingle().Subject;
        message.Contents.Should().ContainSingle().Which.Should().BeOfType<DataContent>();
    }

    [Fact]
    public void BuildUnits_still_drops_a_text_only_message_without_text()
    {
        var (messages, turns) = CreateHistory((MessageRole.User, string.Empty));

        var units = DirectHistoryReplay.BuildUnits(messages, turns, [], NoAdjustments());

        units.Should().BeEmpty();
    }

    [Fact]
    public void BuildUnits_leaves_a_message_without_attachments_unchanged()
    {
        var (messages, turns) = CreateHistory((MessageRole.User, "plain prompt"));

        var units = DirectHistoryReplay.BuildUnits(messages, turns, [], NoAdjustments());

        var message = units.Should().ContainSingle().Which.Messages.Should().ContainSingle().Subject;
        message.Contents.Should().ContainSingle().Which.Should().BeOfType<TextContent>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    public void PlanCompression_plans_an_image_bearing_unit_like_any_other(int imageIndex)
    {
        var (messages, turns) = CreateHistory(
            (MessageRole.User, "first"),
            (MessageRole.User, "second"),
            (MessageRole.User, "third"),
            (MessageRole.User, "fourth"));
        var units = DirectHistoryReplay.BuildUnits(messages, turns, [], NoAdjustments(),
                AttachmentContents(messages[imageIndex].Id))
            .Select(unit => unit with { Tokens = 100 })
            .ToArray();

        var plan = DirectCompactionPlanner.PlanCompression(units, null,
            new DirectCompressionPolicy(), Window(400), countedTokens: 300);

        // threshold = 300, keepTarget = 150: only the newest unit is retained, and the prefix is the same three
        // units wherever the image sits. An image is not a barrier, so the boundary may cross its unit.
        plan.ShouldCompress.Should().BeTrue();
        plan.PrefixUnits.Select(unit => unit.SourceSequence)
            .Should().Equal(messages.Take(3).Select(message => message.Sequence));
        plan.ThroughSequence.Should().Be(messages[2].Sequence, "the boundary is the last compressed unit");
        plan.SourceTokens.Should().Be(300);
    }

    [Fact]
    public void SelectReplayedMessages_skips_what_the_summary_covers_and_what_never_replays()
    {
        var conversationId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var succeeded = Turn(conversationId, ConversationTurnStatus.Succeeded, now);
        var blocked = Turn(conversationId, ConversationTurnStatus.Blocked, now);
        var covered = Message(conversationId, succeeded.Id, 1, MessageStatus.Sealed, now);
        var replayed = Message(conversationId, succeeded.Id, 2, MessageStatus.Sealed, now);
        var refused = Message(conversationId, blocked.Id, 3, MessageStatus.Sealed, now);
        var unsettled = Message(conversationId, succeeded.Id, 4, MessageStatus.Streaming, now);
        var orphaned = Message(conversationId, Guid.NewGuid(), 5, MessageStatus.Sealed, now);
        var boundary = new ConversationCompactionRecord(Guid.NewGuid(), conversationId, succeeded.Id, 1,
            "earlier decisions", "test-model", 1, 10, 5, now);

        var selected = DirectHistoryReplay.SelectReplayedMessages(
            [covered, replayed, refused, unsettled, orphaned], [succeeded, blocked], boundary);

        // Attachment bytes are read for exactly this selection, so it has to match what a request can carry:
        // nothing at or before the boundary, nothing from a blocked turn, no unsettled fragment.
        selected.Should().Equal(replayed);
        DirectHistoryReplay.SelectReplayedMessages([covered, replayed], [succeeded, blocked], null)
            .Should().Equal(covered, replayed);
    }

    private static IReadOnlyDictionary<Guid, IReadOnlyList<AIContent>> AttachmentContents(Guid messageId)
        => new Dictionary<Guid, IReadOnlyList<AIContent>>
        {
            [messageId] = [new DataContent(PngBytes, "image/png") { Name = "photo.png" }]
        };

    private static IReadOnlyDictionary<Guid, string> NoAdjustments() => new Dictionary<Guid, string>();

    private static DirectPromptBudget Window(int? contextWindowTokens) => new(contextWindowTokens, 0);

    private static ConversationTurnRecord Turn(Guid conversationId, ConversationTurnStatus status, DateTimeOffset now)
        => new(Guid.NewGuid(), conversationId, AgentExecutionMode.Direct, DirectTurnOrigin.Interactive, status, now, now);

    private static MessageRecord Message(Guid conversationId, Guid turnId, long sequence, MessageStatus status,
        DateTimeOffset now)
        => new(Guid.NewGuid(), conversationId, turnId, sequence, MessageRole.User, $"message {sequence}", status, now, now);

    private static (List<MessageRecord> Messages, List<ConversationTurnRecord> Turns) CreateHistory(
        params (MessageRole Role, string Text)[] entries)
    {
        var conversationId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var messages = new List<MessageRecord>();
        var turns = new List<ConversationTurnRecord>();
        foreach (var (role, text) in entries)
        {
            var turn = Turn(conversationId, ConversationTurnStatus.Succeeded, now);
            turns.Add(turn);
            messages.Add(new MessageRecord(Guid.NewGuid(), conversationId, turn.Id, messages.Count + 1, role, text,
                MessageStatus.Sealed, now, now));
        }

        return (messages, turns);
    }
}
