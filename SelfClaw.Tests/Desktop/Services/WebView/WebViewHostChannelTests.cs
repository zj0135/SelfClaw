using SelfClaw.Desktop.Services.Transcript.Views;
using SelfClaw.Desktop.Services.Transcript;
using System.Windows.Threading;
using System.Text.Json;
using FluentAssertions;
using SelfClaw.Desktop.Services.WebView;

namespace SelfClaw.Tests.Desktop.Services.WebView;

public sealed class WebViewHostChannelTests
{
    [Fact]
    public void Missing_ack_has_bounded_full_retries_and_explicit_recovery_uses_the_latest_snapshot()
    {
        var messages = new List<string>();
        var channel = new WebViewHostChannel();
        using var delivery = new TranscriptDelivery(channel, Dispatcher.CurrentDispatcher);
        channel.Attach(messages.Add);
        channel.MarkReady();
        delivery.Publish(CreateTranscript("first"));
        var oldRevision = ReadRevision(messages[0]);
        delivery.Publish(CreateTranscript("latest"));
        for (var attempt = 0; attempt < 8; attempt++) delivery.Retry();
        messages.Should().HaveCount(5, "one original, three complete retries, and one unavailable notification");
        delivery.Acknowledge(oldRevision).Should().BeFalse();
        delivery.Resynchronize();
        using var recovered = JsonDocument.Parse(messages[^1]);
        recovered.RootElement.GetProperty("type").GetString().Should().Be("replaceState");
        recovered.RootElement.GetProperty("selectedAgentName").GetString().Should().Be("latest");
        delivery.Acknowledge(ReadRevision(messages[^1])).Should().BeTrue();
        delivery.Publish(CreateTranscript("after recovery"));
        using var patch = JsonDocument.Parse(messages[^1]);
        patch.RootElement.GetProperty("baseRevision").GetInt64().Should().Be(ReadRevision(messages[^2]));
    }

    [Fact]
    public void MarkReady_replays_only_the_latest_transcript()
    {
        var messages = new List<string>();
        var channel = new WebViewHostChannel();
        using var delivery = new TranscriptDelivery(channel, Dispatcher.CurrentDispatcher);
        channel.Attach(messages.Add);
        delivery.Publish(CreateTranscript("first"));
        delivery.Publish(CreateTranscript("second"));

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
        using var delivery = new TranscriptDelivery(channel, Dispatcher.CurrentDispatcher);
        channel.Attach(messages.Add);
        channel.MarkReady();
        delivery.Publish(CreateTranscript("first"));
        delivery.Publish(CreateTranscript("second"));
        delivery.Publish(CreateTranscript("latest"));

        messages.Should().ContainSingle();
        var firstRevision = ReadRevision(messages[0]);

        delivery.Acknowledge(firstRevision).Should().BeTrue();

        messages.Should().HaveCount(2);
        using var patch = JsonDocument.Parse(messages[1]);
        patch.RootElement.GetProperty("type").GetString().Should().Be("patchState");
        patch.RootElement.GetProperty("selectedAgentName").GetString().Should().Be("latest");
        delivery.Acknowledge(firstRevision).Should().BeFalse();
    }

    [Fact]
    public void Transcript_patch_contains_only_changed_messages_and_omits_stable_collections()
    {
        var messages = new List<string>();
        var channel = new WebViewHostChannel();
        using var delivery = new TranscriptDelivery(channel, Dispatcher.CurrentDispatcher);
        var stableItem = CreateItem("stable", "stable");
        var changingItem = CreateItem("changing", "before");
        channel.Attach(messages.Add);
        channel.MarkReady();
        delivery.Publish(CreateTranscript("agent", [stableItem, changingItem]));
        delivery.Acknowledge(ReadRevision(messages[0])).Should().BeTrue();

        delivery.Publish(CreateTranscript(
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
        using var delivery = new TranscriptDelivery(channel, Dispatcher.CurrentDispatcher);
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
        using var delivery = new TranscriptDelivery(channel, Dispatcher.CurrentDispatcher);
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
