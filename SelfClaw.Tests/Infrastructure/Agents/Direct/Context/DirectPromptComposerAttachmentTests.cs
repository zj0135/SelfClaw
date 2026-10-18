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
/// is never planned into a summary.
/// </summary>
public sealed class DirectPromptComposerAttachmentTests
{
    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x2A];

    [Fact]
    public void BuildUnits_places_the_image_before_the_text_of_one_user_message()
    {
        var (messages, turns) = CreateHistory((MessageRole.User, "what is in this picture?"));

        var units = new DirectPromptComposer().BuildUnits(messages, turns, [], NoAdjustments(),
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
        unit.ContainsAttachments.Should().BeTrue("the planner must never fold this unit into a summary");
    }

    [Fact]
    public void BuildUnits_keeps_a_user_message_that_carries_only_images()
    {
        var (messages, turns) = CreateHistory((MessageRole.User, string.Empty));

        var units = new DirectPromptComposer().BuildUnits(messages, turns, [], NoAdjustments(),
            AttachmentContents(messages[0].Id));

        var message = units.Should().ContainSingle().Which.Messages.Should().ContainSingle().Subject;
        message.Contents.Should().ContainSingle().Which.Should().BeOfType<DataContent>();
    }

    [Fact]
    public void BuildUnits_still_drops_a_text_only_message_without_text()
    {
        var (messages, turns) = CreateHistory((MessageRole.User, string.Empty));

        var units = new DirectPromptComposer().BuildUnits(messages, turns, [], NoAdjustments());

        units.Should().BeEmpty();
    }

    [Fact]
    public void BuildUnits_leaves_a_message_without_attachments_unchanged()
    {
        var (messages, turns) = CreateHistory((MessageRole.User, "plain prompt"));

        var units = new DirectPromptComposer().BuildUnits(messages, turns, [], NoAdjustments());

        var message = units.Should().ContainSingle().Which.Messages.Should().ContainSingle().Subject;
        message.Contents.Should().ContainSingle().Which.Should().BeOfType<TextContent>();
        units[0].ContainsAttachments.Should().BeFalse();
    }

    [Fact]
    public void PlanCompression_stops_before_the_first_image_bearing_unit()
    {
        var (messages, turns) = CreateHistory(
            (MessageRole.User, "first"),
            (MessageRole.User, "second"),
            (MessageRole.User, "third"),
            (MessageRole.User, "fourth"));
        var units = UnitsWithImageOn(messages, turns, imageIndex: 2);

        var plan = new DirectPromptComposer().PlanCompression(units, null,
            new DirectCompressionPolicy(), Window(400), countedTokens: 300);

        // Without the image this history compresses three of four units; the image caps the candidates at
        // the two units before it, and retention keeps the newer of those two.
        plan.ShouldCompress.Should().BeTrue();
        plan.PrefixUnits.Select(unit => unit.SourceSequence).Should().Equal(messages[0].Sequence);
        plan.ThroughSequence.Should().Be(messages[0].Sequence, "the boundary is the last compressed unit");
        plan.RetainedUnitCount.Should().Be(1);
        plan.SourceTokens.Should().Be(100);
    }

    [Fact]
    public void PlanCompression_declines_when_the_first_candidate_carries_the_image()
    {
        var (messages, turns) = CreateHistory(
            (MessageRole.User, "first"),
            (MessageRole.User, "second"),
            (MessageRole.User, "third"),
            (MessageRole.User, "fourth"));
        var units = UnitsWithImageOn(messages, turns, imageIndex: 0);

        var plan = new DirectPromptComposer().PlanCompression(units, null,
            new DirectCompressionPolicy(), Window(400), countedTokens: 300);

        // Nothing precedes the image, so there is no contiguous prefix to summarize.
        plan.ShouldCompress.Should().BeFalse();
        plan.PrefixUnits.Should().BeEmpty();
        plan.ThroughSequence.Should().BeNull();
        plan.RetainedUnitCount.Should().Be(4, "a decline still reports every candidate, truncated or not");
    }

    [Fact]
    public void PlanCompression_still_compresses_a_history_without_images()
    {
        var (messages, turns) = CreateHistory(
            (MessageRole.User, "first"),
            (MessageRole.User, "second"),
            (MessageRole.User, "third"),
            (MessageRole.User, "fourth"));
        var units = new DirectPromptComposer().BuildUnits(messages, turns, [], NoAdjustments())
            .Select(unit => unit with { Tokens = 100 })
            .ToArray();

        var plan = new DirectPromptComposer().PlanCompression(units, null,
            new DirectCompressionPolicy(), Window(400), countedTokens: 300);

        plan.ShouldCompress.Should().BeTrue();
    }

    private static IReadOnlyDictionary<Guid, IReadOnlyList<AIContent>> AttachmentContents(Guid messageId)
        => new Dictionary<Guid, IReadOnlyList<AIContent>>
        {
            [messageId] = [new DataContent(PngBytes, "image/png") { Name = "photo.png" }]
        };

    private static DirectPromptHistoryUnit[] UnitsWithImageOn(IReadOnlyList<MessageRecord> messages,
        IReadOnlyList<ConversationTurnRecord> turns, int imageIndex)
        => new DirectPromptComposer().BuildUnits(messages, turns, [], NoAdjustments(),
                AttachmentContents(messages[imageIndex].Id))
            .Select(unit => unit with { Tokens = 100 })
            .ToArray();

    private static IReadOnlyDictionary<Guid, string> NoAdjustments() => new Dictionary<Guid, string>();

    private static DirectPromptBudget Window(int? contextWindowTokens) => new(contextWindowTokens, 0);

    private static (List<MessageRecord> Messages, List<ConversationTurnRecord> Turns) CreateHistory(
        params (MessageRole Role, string Text)[] entries)
    {
        var conversationId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var messages = new List<MessageRecord>();
        var turns = new List<ConversationTurnRecord>();
        foreach (var (role, text) in entries)
        {
            var turnId = Guid.NewGuid();
            turns.Add(new ConversationTurnRecord(turnId, conversationId, AgentExecutionMode.Direct,
                DirectTurnOrigin.Interactive, ConversationTurnStatus.Succeeded, now, now));
            messages.Add(new MessageRecord(Guid.NewGuid(), conversationId, turnId, messages.Count + 1, role, text,
                MessageStatus.Sealed, now, now));
        }

        return (messages, turns);
    }
}
