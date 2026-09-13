using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Desktop.Services.Activities;
using SelfClaw.Desktop.Services.WebView;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Tests.TestDoubles;

internal sealed class ActivityPanelTestContext : IDisposable
{
    internal ActivityPanelTestContext(SubagentActivityTestContext activity, Guid? parent)
    {
        Source = new ActivityPanelTestScope(parent);
        var dispatcher = Dispatcher.CurrentDispatcher;
        Channel.Attach(json =>
        {
            dispatcher.VerifyAccess();
            using var document = JsonDocument.Parse(json);
            Messages.Add(document.RootElement.Clone());
        });
        Channel.MarkReady();
        Publisher = new ActivityPanelPublisher(activity.Service,
            new ActivityPanelSnapshotBuilder(activity.Service, StoragePathDefaults.CreateDefault()),
            Source, Channel, Dispatcher.CurrentDispatcher, NullLogger<ActivityPanelPublisher>.Instance);
    }

    internal WebViewHostChannel Channel { get; } = new();
    internal ActivityPanelTestScope Source { get; }
    internal ActivityPanelPublisher Publisher { get; }
    internal List<JsonElement> Messages { get; } = [];
    internal JsonElement LatestState => Messages.Last(message => message.GetProperty("type").GetString() == "activity-panel/state");

    internal void AcknowledgeLatest()
        => Publisher.Acknowledge(LatestState.GetProperty("subscriptionId").GetGuid(), LatestState.GetProperty("revision").GetInt64());

    public void Dispose() => Publisher.Dispose();
}
