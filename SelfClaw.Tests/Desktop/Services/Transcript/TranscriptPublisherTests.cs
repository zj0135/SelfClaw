using SelfClaw.Desktop.Services.Transcript.Views;
using System.Text.Json;
using System.Windows.Threading;
using FluentAssertions;
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
        using var delivery = new TranscriptDelivery(channel, Dispatcher.CurrentDispatcher);
            channel.Attach(messages.Add);
            channel.MarkReady();
            using var publisher = new TranscriptPublisher(new TranscriptProjection(StoragePathDefaults.CreateDefault()), delivery, dispatcher);
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
        using var delivery = new TranscriptDelivery(channel, Dispatcher.CurrentDispatcher);
        channel.Attach(hostMessages.Add);
        channel.MarkReady();
        var storageRoot = Path.Combine(Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));
        var projection = new TranscriptProjection(
            StoragePathDefaults.Create(
                storageRoot,
                Path.Combine(storageRoot, "selfclaw.db"),
                Path.Combine(storageRoot, "secrets")));
        using var publisher = new TranscriptPublisher(projection, delivery, Dispatcher.CurrentDispatcher);
        var agentName = "first";
        publisher.Attach(autoScroll => CreateRequest(agentName, autoScroll));

        publisher.RequestStreamingPublish(false);
        delivery.Acknowledge(ReadRevision(hostMessages[^1])).Should().BeTrue();
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
        using var delivery = new TranscriptDelivery(channel, Dispatcher.CurrentDispatcher);
        channel.Attach(hostMessages.Add);
        var storageRoot = Path.Combine(Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));
        var projection = new TranscriptProjection(
            StoragePathDefaults.Create(
                storageRoot,
                Path.Combine(storageRoot, "selfclaw.db"),
                Path.Combine(storageRoot, "secrets")));
        using var publisher = new TranscriptPublisher(projection, delivery, Dispatcher.CurrentDispatcher);
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

    [Fact]
    public void Publish_delivers_the_provisional_continuation_flag_on_both_replace_and_patch()
    {
        var hostMessages = new List<string>();
        var channel = new WebViewHostChannel();
        using var delivery = new TranscriptDelivery(channel, Dispatcher.CurrentDispatcher);
        channel.Attach(hostMessages.Add);
        channel.MarkReady();
        var storageRoot = Path.Combine(Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));
        var projection = new TranscriptProjection(
            StoragePathDefaults.Create(
                storageRoot,
                Path.Combine(storageRoot, "selfclaw.db"),
                Path.Combine(storageRoot, "secrets")));
        using var publisher = new TranscriptPublisher(projection, delivery, Dispatcher.CurrentDispatcher);
        var request = CreateRequest("build", false) with { IsBusy = true, IsContinuation = true };
        publisher.Attach(_ => request);

        publisher.PublishNow(false);

        using var payload = JsonDocument.Parse(hostMessages.Single());
        payload.RootElement.GetProperty("type").GetString().Should().Be("replaceState");
        payload.RootElement.GetProperty("isBusy").GetBoolean().Should().BeTrue();
        payload.RootElement.GetProperty("isContinuation").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Publish_patches_a_no_output_turn_outcome_without_inserting_an_assistant()
    {
        var sent = new List<string>();
        var channel = new WebViewHostChannel();
        using var delivery = new TranscriptDelivery(channel, Dispatcher.CurrentDispatcher);
        channel.Attach(sent.Add);
        channel.MarkReady();
        using var publisher = new TranscriptPublisher(new TranscriptProjection(StoragePathDefaults.CreateDefault()), delivery, Dispatcher.CurrentDispatcher);
        var now = DateTimeOffset.UtcNow;
        var turn = new SelfClaw.Core.Models.ConversationTurnRecord(Guid.NewGuid(), Guid.NewGuid(),
            SelfClaw.Core.Runtime.AgentExecutionMode.Direct, SelfClaw.Core.Runtime.DirectTurnOrigin.Interactive,
            SelfClaw.Core.Models.ConversationTurnStatus.Running, now);
        var request = CreateRequest("build", false) with { Turns = [turn] };
        publisher.Attach(_ => request);
        publisher.PublishNow(false);
        delivery.Acknowledge(ReadRevision(sent.Single())).Should().BeTrue();
        request = request with { Turns = [turn with { Status = SelfClaw.Core.Models.ConversationTurnStatus.Failed, ErrorMessage = "no output", CompletedAtUtc = now }] };
        publisher.PublishNow(false);
        using var payload = JsonDocument.Parse(sent.Last());
        payload.RootElement.GetProperty("type").GetString().Should().Be("patchState");
        var changed = payload.RootElement.GetProperty("upsertItems").EnumerateArray().Single();
        changed.GetProperty("kind").GetString().Should().Be("turn-outcome");
        changed.GetProperty("role").GetString().Should().Be("system");
        changed.GetProperty("turnOutcome").GetProperty("errorMessage").GetString().Should().Be("no output");
        changed.GetProperty("segments").GetArrayLength().Should().Be(0);
    }

    private static TranscriptProjectionRequest CreateRequest(string agentName, bool autoScroll)
        => new(
            [],
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
