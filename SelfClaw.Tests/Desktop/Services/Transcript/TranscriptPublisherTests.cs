using System.Text.Json;
using System.Windows.Threading;
using FluentAssertions;
using SelfClaw.Desktop.Services;
using SelfClaw.Desktop.Services.Transcript;
using SelfClaw.Desktop.Services.WebView;
using SelfClaw.Infrastructure.Options;
using SelfClaw.Tests.TestDoubles;

namespace SelfClaw.Tests.Desktop.Services.Transcript;

public sealed class TranscriptPublisherTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Dispose_drops_publication_already_queued_from_a_background_thread(bool streaming)
        => WpfDispatcherTest.RunAsync(async () =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var messages = new List<string>();
            var channel = new WebViewHostChannel();
            channel.Attach(messages.Add);
            channel.MarkReady();
            using var publisher = new TranscriptPublisher(new TranscriptProjection(StoragePathDefaults.CreateDefault()), channel, dispatcher);
            var captures = 0;
            publisher.Attach(autoScroll => { captures++; return CreateRequest("current", autoScroll); });
            var failures = new List<Exception>();
            DispatcherUnhandledExceptionEventHandler onFailure = (_, args) => { failures.Add(args.Exception); args.Handled = true; };
            dispatcher.UnhandledException += onFailure;
            try
            {
                // Keep the dispatcher paused until publication is queued, then dispose before it can run.
                Task.Run(() =>
                {
                    if (streaming) publisher.RequestStreamingPublish(false);
                    else publisher.PublishNow(false);
                }).GetAwaiter().GetResult();
                publisher.Dispose();
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                failures.Should().BeEmpty();
                captures.Should().Be(0);
                messages.Should().BeEmpty();
            }
            finally
            {
                dispatcher.UnhandledException -= onFailure;
            }
        });

    [Fact]
    public void PublishNow_flushes_the_latest_pending_streaming_snapshot()
    {
        var hostMessages = new List<string>();
        var channel = new WebViewHostChannel();
        channel.Attach(hostMessages.Add);
        channel.MarkReady();
        var storageRoot = Path.Combine(Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));
        var projection = new TranscriptProjection(
            StoragePathDefaults.Create(
                storageRoot,
                Path.Combine(storageRoot, "selfclaw.db"),
                Path.Combine(storageRoot, "secrets")));
        using var publisher = new TranscriptPublisher(projection, channel, Dispatcher.CurrentDispatcher);
        var agentName = "first";
        publisher.Attach(autoScroll => CreateRequest(agentName, autoScroll));

        publisher.RequestStreamingPublish(false);
        channel.AcknowledgeTranscript(ReadRevision(hostMessages[^1])).Should().BeTrue();
        agentName = "latest";
        publisher.RequestStreamingPublish(false);
        publisher.PublishNow(true);

        hostMessages.Should().HaveCount(2);
        using var payload = JsonDocument.Parse(hostMessages[^1]);
        payload.RootElement.GetProperty("selectedAgentName").GetString().Should().Be("latest");
        payload.RootElement.GetProperty("autoScroll").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Ready_replays_only_the_latest_state_published_through_the_presentation_module()
    {
        var hostMessages = new List<string>();
        var channel = new WebViewHostChannel();
        channel.Attach(hostMessages.Add);
        var storageRoot = Path.Combine(Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));
        var projection = new TranscriptProjection(
            StoragePathDefaults.Create(
                storageRoot,
                Path.Combine(storageRoot, "selfclaw.db"),
                Path.Combine(storageRoot, "secrets")));
        using var publisher = new TranscriptPublisher(projection, channel, Dispatcher.CurrentDispatcher);
        var agentName = "first";
        publisher.Attach(autoScroll => CreateRequest(agentName, autoScroll));

        publisher.PublishNow(false);
        agentName = "latest";
        publisher.PublishNow(false);
        channel.MarkReady();

        hostMessages.Should().ContainSingle();
        using var payload = JsonDocument.Parse(hostMessages[0]);
        payload.RootElement.GetProperty("selectedAgentName").GetString().Should().Be("latest");
    }

    private static TranscriptProjectionRequest CreateRequest(string agentName, bool autoScroll)
        => new(
            [],
            [],
            [],
            [],
            null,
            autoScroll,
            false,
            null,
            "direct",
            "build",
            agentName,
            0,
            "requireApproval");

    private static long ReadRevision(string message)
    {
        using var payload = JsonDocument.Parse(message);
        return payload.RootElement.GetProperty("revision").GetInt64();
    }
}
