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

    private static TranscriptRenderState CreateTranscript(string agentName)
        => new(
            [],
            false,
            [],
            null,
            false,
            SelectedAgentName: agentName);
}
