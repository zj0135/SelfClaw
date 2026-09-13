using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services.Activities.Models;
using SelfClaw.Desktop.Services.Subagents;
using SelfClaw.Desktop.Services.Subagents.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Desktop.Services.Activities;

internal sealed class ActivityPanelSnapshotBuilder
{
    private readonly SubagentActivityService _activity;
    private readonly ActivityPanelProjection _projection;
    private readonly ILogger<ActivityPanelSnapshotBuilder> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _captureSequence;

    public ActivityPanelSnapshotBuilder(SubagentActivityService activity, StoragePaths paths,
        ILogger<ActivityPanelSnapshotBuilder>? logger = null)
    {
        _activity = activity;
        _projection = new ActivityPanelProjection(paths);
        _logger = logger ?? NullLogger<ActivityPanelSnapshotBuilder>.Instance;
    }

    internal async Task<ActivityPanelWireState> BuildAsync(ActivityPanelQuery query, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await CaptureAsync(query, cancellationToken).ConfigureAwait(false);
            return state with { CaptureSequence = ++_captureSequence };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Activity read failed. SubscriptionId={SubscriptionId}", query.SubscriptionId);
            return new ActivityPanelWireState(query.SubscriptionId, query.ParentConversationId, 0, [],
                StateError: exception.Message, CaptureSequence: ++_captureSequence);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ActivityPanelWireState> CaptureAsync(ActivityPanelQuery query, CancellationToken cancellationToken)
    {
        if (query.ParentConversationId is not Guid parent)
        {
            return new ActivityPanelWireState(query.SubscriptionId, null, 0, []);
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var revision = _activity.PersistenceRevision;
            var (page, reset) = await ReadPageAsync(query, parent, cancellationToken).ConfigureAwait(false);
            var detail = query.TaskId is Guid task
                ? await _activity.GetDetailAsync(parent, task, cancellationToken: cancellationToken).ConfigureAwait(false)
                : null;
            if (revision != _activity.PersistenceRevision) continue;
            return _projection.Build(query, page, detail, reset);
        }
    }

    private async Task<(SubagentActivityPageSnapshot Page, bool Reset)> ReadPageAsync(ActivityPanelQuery query,
        Guid parent, CancellationToken cancellationToken)
    {
        try
        {
            return (await _activity.ListAsync(new SubagentActivityQuery(parent, Cursor: query.Cursor),
                cancellationToken: cancellationToken).ConfigureAwait(false), false);
        }
        catch (SubagentActivityReadException exception) when (query.Cursor is not null && exception.Message == "task-list-changed")
        {
            return (await _activity.ListAsync(new SubagentActivityQuery(parent), cancellationToken: cancellationToken)
                .ConfigureAwait(false), true);
        }
    }
}
