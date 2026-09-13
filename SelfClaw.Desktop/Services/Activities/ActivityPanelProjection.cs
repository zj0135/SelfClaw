using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services.Activities.Models;
using SelfClaw.Desktop.Services.Subagents.Models;
using SelfClaw.Desktop.Services.Transcript;
using SelfClaw.Desktop.Services.WebView;
using SelfClaw.Core.Runtime;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Desktop.Services.Activities;

internal sealed class ActivityPanelProjection
{
    internal const int MaximumPayloadBytes = 256 * 1024;
    internal const int MaximumBlocks = 64;
    private readonly TranscriptMessageProjector _messages;

    internal ActivityPanelProjection(StoragePaths paths) => _messages = new TranscriptMessageProjector(paths);

    internal ActivityPanelWireState Build(ActivityPanelQuery query, SubagentActivityPageSnapshot page,
        SubagentActivitySnapshot? snapshot, bool listReset)
    {
        ActivityPanelWireDetail? detail = null;
        var detailError = query.TaskId is not null && snapshot is null ? "task-not-found" : null;
        if (snapshot is not null)
        {
            try { detail = BuildDetail(snapshot, query); }
            catch (SubagentActivityReadException exception) { detailError = exception.Message; }
        }
        var section = new ActivityPanelWireSection(page.Counts, page.ListVersion,
            listReset ? null : query.Cursor, page.NextCursor, listReset,
            page.Activities.Select(ToTask).ToArray(), query.DetailSelectionId, detail,
            detailError, SelectedTask: detail is null && snapshot is not null ? ToTask(snapshot.Activity) : null);
        var state = new ActivityPanelWireState(query.SubscriptionId, query.ParentConversationId, 0, [section]);
        // Reserve space for the assigned revision and the correlated response id, using the host's serializer.
        for (var maximum = 4096; ; maximum /= 2)
        {
            state = Limit(state, maximum);
            if (WebViewHostChannel.SerializeToUtf8Bytes(state).Length <= MaximumPayloadBytes - 2048)
            {
                return state;
            }

            if (maximum == 0)
            {
                throw new InvalidOperationException("activity-payload-too-large");
            }
        }
    }

    private ActivityPanelWireDetail BuildDetail(SubagentActivitySnapshot snapshot, ActivityPanelQuery query)
    {
        var detail = snapshot.Content;
        if (query.ContentVersion is string expected && expected != detail.ContentVersion)
        {
            throw new SubagentActivityReadException("content-changed");
        }

        _messages.PruneToolSegmentCache(detail.ToolRuns);
        var message = detail.Message is null ? null : _messages.Build(detail.Message, detail.ToolRuns);
        var placed = message?.Segments ?? [];
        var unplaced = detail.UnplacedToolRuns.Select(TranscriptToolRunPresenter.BuildToolSegment).ToArray();
        var total = placed.Count + unplaced.Length;
        var offset = query.BlockOffset ?? Math.Max(0, total - MaximumBlocks);
        if (offset < 0 || offset > total)
        {
            throw new SubagentActivityReadException("invalid-block-offset");
        }

        var content = new List<ActivityContentReference>
        {
            Reference("task-text", null, "taskText", detail.TaskText.Length, detail.TaskText.Length)
        };
        var window = placed.Concat(unplaced).Skip(offset).Take(MaximumBlocks).ToArray();
        foreach (var segment in window)
        {
            AddReferences(content, detail, segment);
        }

        var placedCount = Math.Clamp(placed.Count - offset, 0, window.Length);
        return new ActivityPanelWireDetail(snapshot.Activity.Task.TaskId, ToTask(snapshot.Activity), detail.TaskText,
            detail.ContentVersion, snapshot.ContentOrigin, detail.HistoryCompleteness.ToString().ToLowerInvariant(),
            offset, total, offset > 0 ? Math.Max(0, offset - MaximumBlocks) : null,
            offset + window.Length < total ? offset + window.Length : null,
            message is null ? null : message with { Segments = window.Take(placedCount).ToArray(), Attachments = null },
            window.Skip(placedCount).ToArray(), content);
    }

    private static void AddReferences(List<ActivityContentReference> content, SubagentContentSnapshot detail,
        TranscriptRenderSegment segment)
    {
        if (segment.Kind != "tool")
        {
            var id = segment.SegmentOrdinal is int ordinal ? $"segment/{ordinal}" : "final-text";
            content.Add(Reference(id, segment.SegmentId, "markdown", segment.Markdown.Length, segment.Markdown.Length));
            return;
        }

        var tool = detail.ToolRuns.First(item => item.Id.ToString("D") == segment.SegmentId);
        content.Add(Reference($"tool/{tool.Id:D}/arguments", segment.SegmentId, "arguments",
            tool.ArgumentsJson.Length, 0));
        var result = tool.ResultContent ?? tool.ResultSummary ?? string.Empty;
        content.Add(Reference($"tool/{tool.Id:D}/result", segment.SegmentId, "detailText", result.Length,
            string.Equals(result, segment.DetailText, StringComparison.Ordinal) ? result.Length : 0));
    }

    private static ActivityPanelWireState Limit(ActivityPanelWireState state, int maximum)
    {
        var section = state.Sections[0];
        var detail = section.Detail;
        if (detail is not null)
        {
            var segments = (detail.Message?.Segments ?? []).Select(segment => LimitSegment(segment, maximum)).ToArray();
            var tools = detail.UnplacedTools.Select(segment => LimitSegment(segment, maximum)).ToArray();
            var byId = segments.Concat(tools).ToDictionary(segment => segment.SegmentId ?? string.Empty);
            var taskText = Clip(detail.TaskText, Math.Min(maximum, 2048));
            var references = detail.Content.Select(reference =>
            {
                var preview = reference.Field == "taskText" ? taskText.Length
                    : reference.SegmentId is string id && byId.TryGetValue(id, out var segment)
                        ? reference.Field == "markdown" ? segment.Markdown.Length
                            : reference.Field == "detailText" ? Math.Min(segment.DetailText?.Length ?? 0, reference.PreviewCharacters) : 0
                        : 0;
                return reference with { PreviewCharacters = preview, IsTruncated = preview < reference.TotalCharacters };
            }).ToArray();
            detail = detail with
            {
                Task = LimitTask(detail.Task, maximum), TaskText = taskText,
                Message = detail.Message is null ? null : detail.Message with
                {
                    Segments = segments, ErrorMessage = ClipNullable(detail.Message.ErrorMessage, maximum)
                },
                UnplacedTools = tools, Content = references
            };
        }

        return state with
        {
            Sections = [section with
            {
                Tasks = section.Tasks.Select(task => LimitTask(task, maximum)).ToArray(),
                Detail = detail,
                SelectedTask = section.SelectedTask is null ? null : LimitTask(section.SelectedTask, maximum)
            }]
        };
    }

    private static TranscriptRenderSegment LimitSegment(TranscriptRenderSegment segment, int maximum)
        => segment with
        {
            Markdown = Clip(segment.Markdown, maximum), DetailText = ClipNullable(segment.DetailText, maximum),
            Text = ClipNullable(segment.Text, 160), ToolName = ClipNullable(segment.ToolName, 160),
            SourceId = ClipNullable(segment.SourceId, 160), DisplayName = ClipNullable(segment.DisplayName, 160),
            DetailTitle = ClipNullable(segment.DetailTitle, 160)
        };

    private static ActivityPanelWireTask LimitTask(ActivityPanelWireTask task, int maximum)
    {
        var previewLimit = Math.Min(maximum, 256);
        var errorLimit = Math.Min(maximum, 512);
        return task with
        {
            SubagentId = Clip(task.SubagentId, 80), SubagentName = Clip(task.SubagentName, 80),
            TaskPreview = Clip(task.TaskPreview, previewLimit), ModelDisplayName = ClipNullable(task.ModelDisplayName, 160),
            ErrorCode = ClipNullable(task.ErrorCode, 128), ErrorMessage = ClipNullable(task.ErrorMessage, errorLimit),
            DeliveryError = ClipNullable(task.DeliveryError, errorLimit), RecordingError = ClipNullable(task.RecordingError, errorLimit),
            MetadataTruncated = task.MetadataTruncated || task.TaskPreview.Length > previewLimit ||
                task.ErrorMessage?.Length > errorLimit || task.DeliveryError?.Length > errorLimit || task.RecordingError?.Length > errorLimit
        };
    }

    private static ActivityPanelWireTask ToTask(SubagentTaskActivity activity)
    {
        var task = activity.Task;
        return new ActivityPanelWireTask(task.TaskId, task.ParentTurnId, task.SubagentId, task.SubagentName,
            task.TaskPreview, task.Attempt, task.Status.ToString().ToLowerInvariant(), activity.Phase,
            task.CancelRequestedAtUtc is not null, activity.PendingApprovalCount,
            task.CancelRequestedAtUtc is null && task.Status is SubagentTaskStatus.Queued or SubagentTaskStatus.Running,
            task.QueuedAtUtc, task.StartedAtUtc, task.CompletedAtUtc, task.ModelDisplayName, task.InputTokens, task.OutputTokens,
            task.ErrorCode, task.ErrorMessage, task.DeliveryStatus?.ToString().ToLowerInvariant() ?? "none",
            task.DeliveryAttemptCount, task.DeliveryError, activity.RecordingError, false);
    }

    private static ActivityContentReference Reference(string id, string? segmentId, string field, int total, int preview)
        => new(id, segmentId, field, total, preview, total > preview);

    private static string? ClipNullable(string? value, int maximum) => value is null ? null : Clip(value, maximum);

    private static string Clip(string value, int maximum)
    {
        if (value.Length <= maximum) return value;
        if (maximum > 0 && char.IsHighSurrogate(value[maximum - 1]) && char.IsLowSurrogate(value[maximum])) maximum--;
        return value[..maximum];
    }
}
