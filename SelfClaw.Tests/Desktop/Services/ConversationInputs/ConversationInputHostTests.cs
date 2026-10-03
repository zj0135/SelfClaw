using System.Text.Json;
using System.Windows.Threading;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services.ConversationInputs;
using SelfClaw.Desktop.Services.WebView;
using SelfClaw.Tests.TestDoubles;

namespace SelfClaw.Tests.Desktop.Services.ConversationInputs;

/// <summary>
/// Host-boundary coverage for the queue bridge and publisher: bounded state, revision-bound
/// mutations, scope isolation and explicit Steer rejection.
/// </summary>
public sealed class ConversationInputHostTests
{
    [Fact]
    public Task Subscribe_publishes_the_bounded_queue_and_detail_reads_the_restricted_body()
        => WpfDispatcherTest.RunAsync(async () =>
        {
            using var context = new HostContext();
            var conversation = await context.CreateConversationAsync();
            await context.AcceptAsync(conversation, "first");
            await context.AcceptAsync(conversation, "second");

            await context.Bridge.HandleAsync("conversation-input/subscribe", context.Payload(new
            {
                subscriptionId = context.SubscriptionId, conversationId = conversation
            }), CancellationToken.None);

            var state = context.LastState();
            state.GetProperty("conversationId").GetString().Should().Be(conversation.ToString("D"));
            state.GetProperty("canSteer").GetBoolean().Should().BeFalse();
            state.GetProperty("paused").GetBoolean().Should().BeFalse();
            state.GetProperty("items").GetArrayLength().Should().Be(2);

            var inputId = state.GetProperty("items")[0].GetProperty("inputId").GetString()!;
            await context.Bridge.HandleAsync("conversation-input/detail", context.Payload(new { inputId }), CancellationToken.None);
            var detail = context.LastResult();
            detail.GetProperty("operation").GetString().Should().Be("detail");
            detail.GetProperty("prompt").GetString().Should().Be("first");
        });

    [Fact]
    public Task Edit_cancel_pause_and_resume_are_revision_bound_and_reject_stale_subscriptions()
        => WpfDispatcherTest.RunAsync(async () =>
        {
            using var context = new HostContext();
            var conversation = await context.CreateConversationAsync();
            await context.AcceptAsync(conversation, "draft");
            await context.Bridge.HandleAsync("conversation-input/subscribe", context.Payload(new
            {
                subscriptionId = context.SubscriptionId, conversationId = conversation
            }), CancellationToken.None);
            var item = context.LastState().GetProperty("items")[0];
            var inputId = item.GetProperty("inputId").GetString()!;
            var revision = item.GetProperty("revision").GetInt32();

            await context.Bridge.HandleAsync("conversation-input/edit", context.Payload(new
            {
                inputId, expectedRevision = revision, prompt = "edited"
            }), CancellationToken.None);
            context.LastResult().GetProperty("ok").GetBoolean().Should().BeTrue();

            await context.Bridge.HandleAsync("conversation-input/edit", context.Payload(new
            {
                inputId, expectedRevision = revision, prompt = "stale"
            }), CancellationToken.None);
            var conflict = context.LastResult();
            conflict.GetProperty("ok").GetBoolean().Should().BeFalse();
            conflict.GetProperty("conflict").GetBoolean().Should().BeTrue();

            var queueRevision = await context.RevisionAsync(conversation);
            await context.Bridge.HandleAsync("conversation-input/pause", context.Payload(new
            {
                conversationId = conversation, expectedQueueRevision = queueRevision
            }), CancellationToken.None);
            context.LastResult().GetProperty("paused").GetBoolean().Should().BeTrue();

            var pausedRevision = await context.RevisionAsync(conversation);
            await context.Bridge.HandleAsync("conversation-input/resume", context.Payload(new
            {
                conversationId = conversation, expectedQueueRevision = pausedRevision
            }), CancellationToken.None);
            context.LastResult().GetProperty("paused").GetBoolean().Should().BeFalse();

            await context.Bridge.HandleAsync("conversation-input/get-state", context.Payload(new
            {
                subscriptionId = Guid.NewGuid()
            }), CancellationToken.None);
            context.LastResult().GetProperty("errorCode").GetString()
                .Should().Be("conversation-input-subscription-invalid");
        });

    [Fact]
    public Task Steer_and_promote_are_rejected_explicitly()
        => WpfDispatcherTest.RunAsync(async () =>
        {
            using var context = new HostContext();
            var conversation = await context.CreateConversationAsync();
            await context.AcceptAsync(conversation, "queued");
            await context.Bridge.HandleAsync("conversation-input/subscribe", context.Payload(new
            {
                subscriptionId = context.SubscriptionId, conversationId = conversation
            }), CancellationToken.None);

            await context.Bridge.HandleAsync("conversation-input/promote", context.Payload(new
            {
                inputId = Guid.NewGuid(), expectedRevision = 1, targetTurnId = Guid.NewGuid()
            }), CancellationToken.None);
            context.LastResult().GetProperty("errorCode").GetString().Should().Be("steer-not-enabled");

            await context.Bridge.HandleAsync("conversation-input/steer", context.Payload(new
            {
                inputId = Guid.NewGuid(), expectedRevision = 1, targetTurnId = Guid.NewGuid()
            }), CancellationToken.None);
            context.LastResult().GetProperty("errorCode").GetString().Should().Be("steer-not-enabled");
        });

    [Fact]
    public Task Publisher_ignores_other_conversations_and_refreshes_on_reload()
        => WpfDispatcherTest.RunAsync(async () =>
        {
            using var context = new HostContext();
            var conversation = await context.CreateConversationAsync();
            var other = await context.CreateConversationAsync();
            await context.AcceptAsync(conversation, "scoped");
            await context.Bridge.HandleAsync("conversation-input/subscribe", context.Payload(new
            {
                subscriptionId = context.SubscriptionId, conversationId = conversation
            }), CancellationToken.None);
            var baseline = context.StateCount;

            context.Changes.Notify(other);
            await context.PumpAsync();
            context.StateCount.Should().Be(baseline);

            context.Changes.Notify(conversation);
            await context.WaitForCountAsync(baseline + 1);

            // A WebView reload re-pulls the current durable state without a stored copy.
            context.Channel.MarkNotReady();
            context.Channel.MarkReady();
            await context.WaitForCountAsync(baseline + 2);
            context.LastState().GetProperty("items")[0].GetProperty("preview").GetString().Should().Be("scoped");
        });

    private sealed class HostContext : IDisposable
    {
        private readonly ConversationInputTestHarness _harness = new();
        private readonly List<JsonDocument> _posted = [];
        private readonly WebViewHostChannel _channel = new();

        public HostContext()
        {
            Channel = _channel;
            Changes = _harness.Changes;
            Bridge = new ConversationInputBridge(_harness.Service, Publisher, _channel, Dispatcher.CurrentDispatcher);
            _channel.Attach(json =>
            {
                lock (_posted)
                {
                    _posted.Add(JsonDocument.Parse(json));
                }
            });
            _channel.MarkReady();
        }

        public Guid SubscriptionId { get; } = Guid.NewGuid();
        public WebViewHostChannel Channel { get; }
        public ConversationInputChangeNotifier Changes { get; }
        public ConversationInputBridge Bridge { get; }
        public ConversationInputPublisher Publisher => _publisher ??= new ConversationInputPublisher(
            _harness.Fixture.Inputs, Changes, _channel, Dispatcher.CurrentDispatcher, NullLogger<ConversationInputPublisher>.Instance);
        private ConversationInputPublisher? _publisher;

        public Task<Guid> CreateConversationAsync()
            => _harness.CreateConversationAsync();

        public async Task<long> RevisionAsync(Guid conversationId)
            => (await _harness.Fixture.Inputs.GetQueueStateAsync(conversationId)).QueueRevision;

        public async Task AcceptAsync(Guid conversationId, string prompt)
        {
            var result = await _harness.Fixture.Inputs.AcceptAsync(new ConversationInputAcceptRequest(conversationId,
                Guid.NewGuid().ToString("N"), ConversationInputKind.FollowUp, null, prompt,
                _harness.Snapshot(), null, true));
            if (result.Status != ConversationInputAcceptStatus.Accepted)
                throw new InvalidOperationException(result.Reason);
        }

        public JsonElement Payload(object payload)
            => JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement;

        public JsonElement LastState()
            => _posted.Last(document => document.RootElement.GetProperty("type").GetString() == "conversation-input/state").RootElement;

        public JsonElement LastResult()
            => _posted.Last(document => document.RootElement.GetProperty("type").GetString() == "conversation-input/result").RootElement;

        public int StateCount => _posted.Count(document => document.RootElement.GetProperty("type").GetString() == "conversation-input/state");

        public async Task PumpAsync()
        {
            for (var attempt = 0; attempt < 10; attempt++) await Task.Delay(10);
        }

        public async Task WaitForCountAsync(int expected)
        {
            for (var attempt = 0; attempt < 200; attempt++)
            {
                if (StateCount >= expected) return;
                await Task.Delay(10);
            }

            throw new TimeoutException($"Expected {expected} state pushes but saw {StateCount}.");
        }

        public void Dispose()
        {
            _publisher?.Dispose();
            foreach (var document in _posted) document.Dispose();
            _harness.Dispose();
        }
    }
}