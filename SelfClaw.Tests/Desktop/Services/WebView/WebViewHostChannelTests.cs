using System.Text.Json;
using FluentAssertions;
using SelfClaw.Desktop.Services;
using SelfClaw.Desktop.Services.WebView;

namespace SelfClaw.Tests.Desktop.Services.WebView;

public sealed class WebViewHostChannelTests
{
    [Fact]
    public void MarkReady_replays_only_the_latest_transcript()
    {
        var messages = new List<string>();
        var channel = new WebViewHostChannel();
        channel.Attach(messages.Add);
        channel.PublishTranscript(CreateTranscript("first"));
        channel.PublishTranscript(CreateTranscript("second"));

        channel.MarkReady();

        messages.Should().ContainSingle();
        using var payload = JsonDocument.Parse(messages[0]);
        payload.RootElement.GetProperty("type").GetString().Should().Be("replaceState");
        payload.RootElement.GetProperty("selectedAgentName").GetString().Should().Be("second");
        payload.RootElement.TryGetProperty("SelectedAgentName", out _).Should().BeFalse();
        payload.RootElement.GetProperty("revision").GetInt64().Should().BePositive();
    }

    [Fact]
    public void Transcript_delivery_keeps_only_the_latest_pending_state_and_sends_a_patch_after_acknowledgement()
    {
        var messages = new List<string>();
        var channel = new WebViewHostChannel();
        channel.Attach(messages.Add);
        channel.MarkReady();
        channel.PublishTranscript(CreateTranscript("first"));
        channel.PublishTranscript(CreateTranscript("second"));
        channel.PublishTranscript(CreateTranscript("latest"));

        messages.Should().ContainSingle();
        var firstRevision = ReadRevision(messages[0]);

        channel.AcknowledgeTranscript(firstRevision).Should().BeTrue();

        messages.Should().HaveCount(2);
        using var patch = JsonDocument.Parse(messages[1]);
        patch.RootElement.GetProperty("type").GetString().Should().Be("patchState");
        patch.RootElement.GetProperty("selectedAgentName").GetString().Should().Be("latest");
        channel.AcknowledgeTranscript(firstRevision).Should().BeFalse();
    }

    [Fact]
    public void Transcript_patch_contains_only_changed_messages_and_omits_stable_collections()
    {
        var messages = new List<string>();
        var channel = new WebViewHostChannel();
        var stableItem = CreateItem("stable", "stable");
        var changingItem = CreateItem("changing", "before");
        channel.Attach(messages.Add);
        channel.MarkReady();
        channel.PublishTranscript(CreateTranscript("agent", [stableItem, changingItem]));
        channel.AcknowledgeTranscript(ReadRevision(messages[0])).Should().BeTrue();

        channel.PublishTranscript(CreateTranscript(
            "agent",
            [stableItem, CreateItem("changing", "after")]));

        using var patch = JsonDocument.Parse(messages[1]);
        patch.RootElement.GetProperty("upsertItems").EnumerateArray()
            .Select(item => item.GetProperty("id").GetString())
            .Should().Equal("changing");
        patch.RootElement.TryGetProperty("itemOrder", out _).Should().BeFalse();
        patch.RootElement.TryGetProperty("conversations", out _).Should().BeFalse();
    }

    [Fact]
    public void Push_waits_for_ready_while_response_can_reply_to_an_active_page()
    {
        var messages = new List<string>();
        var channel = new WebViewHostChannel();
        channel.Attach(messages.Add);

        channel.PostPush(new { type = "push" }).Should().BeFalse();
        channel.PostResponse(new { type = "response", requestId = "1" }).Should().BeTrue();

        messages.Should().ContainSingle(message => message.Contains("response", StringComparison.Ordinal));
    }

    [Fact]
    public void Navigation_start_blocks_pushes_until_the_next_ready_signal()
    {
        var messages = new List<string>();
        var channel = new WebViewHostChannel();
        channel.Attach(messages.Add);
        channel.MarkReady();
        channel.MarkNotReady();

        channel.PostPush(new { type = "push" }).Should().BeFalse();
        channel.MarkReady();
        channel.PostPush(new { type = "push" }).Should().BeTrue();

        messages.Should().ContainSingle();
    }

    private static TranscriptRenderState CreateTranscript(
        string agentName,
        IReadOnlyList<TranscriptRenderItem>? items = null)
        => new(
            items ?? [],
            false,
            [],
            null,
            false,
            SelectedAgentName: agentName);

    private static TranscriptRenderItem CreateItem(string id, string html)
        => new(
            id,
            "message",
            "assistant",
            "streaming",
            [new TranscriptRenderSegment("content", html, false)],
            true,
            "now");

    private static long ReadRevision(string message)
    {
        using var payload = JsonDocument.Parse(message);
        return payload.RootElement.GetProperty("revision").GetInt64();
    }
}
