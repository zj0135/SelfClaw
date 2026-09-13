using System.Text.Json;
using System.Windows.Threading;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Desktop.Services.Activities;
using SelfClaw.Desktop.Services.Activities.Models;
using SelfClaw.Desktop.Services.WebView;
using SelfClaw.Tests.TestDoubles;

namespace SelfClaw.Tests.Desktop.Services.Activities;

public sealed class ActivityPanelDeliveryTests
{
    [Fact]
    public Task Reset_discards_old_delivery_and_dispose_permanently_rejects_reuse() => WpfDispatcherTest.RunAsync(() =>
    {
        var channel = new WebViewHostChannel();
        using var delivery = new ActivityPanelDelivery(channel, Dispatcher.CurrentDispatcher, TimeProvider.System, NullLogger.Instance);
        var received = new List<long>();
        channel.Attach(json =>
        {
            using var document = JsonDocument.Parse(json);
            received.Add(document.RootElement.GetProperty("revision").GetInt64());
        });
        channel.MarkReady();
        var state = new ActivityPanelWireState(Guid.NewGuid(), Guid.NewGuid(), 1, []);
        delivery.Offer(state);
        delivery.Offer(state with { Revision = 2 });
        delivery.Reset();
        delivery.Acknowledge(state.SubscriptionId, 1).Should().BeFalse();
        delivery.Offer(state with { Revision = 3 });
        delivery.Acknowledge(state.SubscriptionId, 3).Should().BeTrue();
        received.Should().Equal(1, 3);

        delivery.Dispose();
        var offer = () => delivery.Offer(state with { Revision = 4 });
        offer.Should().Throw<ObjectDisposedException>();
        var reset = () => delivery.Reset();
        reset.Should().Throw<ObjectDisposedException>();
        return Task.CompletedTask;
    });

    [Fact]
    public Task Reentrant_ack_and_not_ready_delivery_do_not_leave_a_phantom_inflight() => WpfDispatcherTest.RunAsync(() =>
    {
        var channel = new WebViewHostChannel();
        using var delivery = new ActivityPanelDelivery(channel, Dispatcher.CurrentDispatcher, TimeProvider.System, NullLogger.Instance);
        var received = new List<long>();
        channel.Attach(json =>
        {
            using var document = JsonDocument.Parse(json);
            var payload = document.RootElement;
            var revision = payload.GetProperty("revision").GetInt64();
            received.Add(revision);
            delivery.Acknowledge(payload.GetProperty("subscriptionId").GetGuid(), revision).Should().BeTrue();
        });
        var state = new ActivityPanelWireState(Guid.NewGuid(), Guid.NewGuid(), 1, []);
        delivery.Offer(state);
        received.Should().BeEmpty();
        channel.MarkReady();
        delivery.Ready();
        delivery.Offer(state with { Revision = 2 });
        received.Should().Equal(1, 2);
        delivery.Acknowledge(state.SubscriptionId, 2).Should().BeFalse();
        return Task.CompletedTask;
    });
}
