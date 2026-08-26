using System.IO;
using System.Text;
using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services;
using SelfClaw.Infrastructure.Options;
using SelfClaw.Infrastructure.Tools.Transcript;
using SelfClaw.Infrastructure.Tools.Transcript.Models;

namespace SelfClaw.Desktop.Services.Transcript;

public sealed class TranscriptProjection
{
    private const string AttachmentHostName = "attachments.selfclaw.local";
    private readonly StoragePaths _storagePaths;
    private readonly Dictionary<Guid, (string Fingerprint, TranscriptRenderItem Item)> _messageCache = [];
    private readonly Dictionary<Guid, (ToolExecutionRecord Record, TranscriptRenderSegment Segment)> _toolSegmentCache = [];
    private string? _lastFingerprint;

    public TranscriptProjection(StoragePaths storagePaths)
    {
        _storagePaths = storagePaths;
    }

    internal TranscriptRenderState? Build(TranscriptProjectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var fingerprint = BuildFingerprint(request);
        if (string.Equals(_lastFingerprint, fingerprint, StringComparison.Ordinal))
        {
            return null;
        }

        _lastFingerprint = fingerprint;
        var toolRunsByMessageId = TranscriptToolRunPresenter.BuildToolRunsByMessageId(
            request.Messages,
            request.ToolRuns,
            request.ToolRunAnchors);
        PruneMessageCache(request.Messages);
        PruneToolSegmentCache(request.ToolRuns);
        var items = request.Messages
            .OrderBy(message => message.CreatedAtUtc)
            .Select(message => BuildMessageItemCached(
                message,
                toolRunsByMessageId.TryGetValue(message.Id, out var toolRuns) ? toolRuns : []))
            .ToArray();

        return new TranscriptRenderState(
            items,
            request.AutoScroll,
            BuildConversationItems(request),
            request.SelectedConversationId?.ToString("D"),
            request.IsBusy,
            request.ActivityText,
            request.AgentMode,
            request.SelectedAgentId,
            request.SelectedAgentName,
            request.CapabilityRevision,
            request.ToolPermissionMode);
    }

    internal void Invalidate() => _lastFingerprint = null;

    private static string BuildFingerprint(TranscriptProjectionRequest request)
    {
        var conversations = request.Conversations
            .OrderByDescending(conversation => conversation.UpdatedAtUtc)
            .ThenBy(conversation => conversation.CreatedAtUtc)
            .ToArray();
        var builder = new StringBuilder();
        builder.Append(request.AutoScroll ? '1' : '0')
            .Append('|')
            .Append(request.SelectedConversationId?.ToString("D"))
            .Append('|')
            .Append(request.IsBusy ? '1' : '0')
            .Append('|')
            .Append(request.ActivityText)
            .Append('|')
            .Append(request.AgentMode)
            .Append('|')
            .Append(request.SelectedAgentId)
            .Append('|')
            .Append(request.SelectedAgentName)
            .Append('|')
            .Append(request.CapabilityRevision)
            .Append('|')
            .Append(request.ToolPermissionMode)
            .Append('|')
            .Append(conversations.Length)
            .Append('|');

        foreach (var conversation in conversations)
        {
            builder.Append(conversation.Id.ToString("D"))
                .Append(':')
                .Append(conversation.CreatedAtUtc.UtcTicks)
                .Append(':')
                .Append(conversation.UpdatedAtUtc.UtcTicks)
                .Append(':')
                .Append(conversation.WorkspaceRootId?.ToString("D"))
                .Append(':');
            AppendTextFingerprint(builder, conversation.Title);
            builder.Append(';');
        }

        builder.Append('|')
            .Append(request.WorkspaceRoots.Count)
            .Append('|');
        foreach (var workspaceRoot in request.WorkspaceRoots.OrderBy(workspaceRoot => workspaceRoot.Id))
        {
            builder.Append(workspaceRoot.Id.ToString("D"))
                .Append(':');
            AppendTextFingerprint(builder, workspaceRoot.Name);
            AppendTextFingerprint(builder, workspaceRoot.RootPath);
            AppendTextFingerprint(builder, workspaceRoot.GitRepositoryId?.ToString("D"));
            AppendTextFingerprint(builder, workspaceRoot.GitRepositoryName);
            AppendTextFingerprint(builder, workspaceRoot.GitBranchName);
            builder.Append(workspaceRoot.IsManagedWorktree ? '1' : '0');
            builder.Append(';');
        }

        builder.Append('|')
            .Append(request.Messages.Count)
            .Append('|');
        foreach (var message in request.Messages.OrderBy(message => message.CreatedAtUtc))
        {
            AppendMessageFingerprint(builder, message);
            builder.Append(';');
        }

        builder.Append('|')
            .Append(request.ToolRuns.Count)
            .Append('|');
        foreach (var toolRun in request.ToolRuns.OrderBy(toolRun => toolRun.CreatedAtUtc))
        {
            AppendToolRunFingerprint(builder, toolRun);
            builder.Append(';');
        }

        builder.Append('|');
        foreach (var anchor in request.ToolRunAnchors.OrderBy(anchor => anchor.Key))
        {
            builder.Append(anchor.Key.ToString("D"))
                .Append(':')
                .Append(anchor.Value.MessageId.ToString("D"))
                .Append(':')
                .Append(anchor.Value.AfterSegmentIndex)
                .Append(';');
        }

        return builder.ToString();
    }

    private static TranscriptConversationItem[] BuildConversationItems(TranscriptProjectionRequest request)
        => request.Conversations
            .OrderByDescending(conversation => conversation.UpdatedAtUtc)
            .ThenBy(conversation => conversation.CreatedAtUtc)
            .Select(conversation => BuildConversationItem(conversation, request.WorkspaceRoots))
            .ToArray();

    private static TranscriptConversationItem BuildConversationItem(
        ConversationRecord conversation,
        IReadOnlyList<WorkspaceRoot> workspaceRoots)
    {
        var workspaceRoot = conversation.WorkspaceRootId is Guid workspaceRootId
            ? workspaceRoots.FirstOrDefault(root => root.Id == workspaceRootId)
            : null;

        return new TranscriptConversationItem(
            conversation.Id.ToString("D"),
            conversation.Title,
            conversation.UpdatedAtUtc.LocalDateTime.ToString("yyyy-MM-dd HH:mm"),
            conversation.WorkspaceRootId?.ToString("D"),
            workspaceRoot?.Name,
            workspaceRoot?.RootPath,
            workspaceRoot?.GitRepositoryId?.ToString("D"),
            workspaceRoot?.GitRepositoryName,
            workspaceRoot?.GitBranchName,
            workspaceRoot?.IsManagedWorktree == true);
    }

    private TranscriptRenderItem BuildMessageItemCached(
        MessageRecord message,
        IReadOnlyList<ToolRunPlacement> toolRuns)
    {
        var fingerprint = BuildMessageFingerprint(message, toolRuns);
        if (_messageCache.TryGetValue(message.Id, out var cached) &&
            string.Equals(cached.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            return cached.Item;
        }

        var item = BuildMessageItem(message, toolRuns);
        _messageCache[message.Id] = (fingerprint, item);
        return item;
    }

    private void PruneMessageCache(IReadOnlyList<MessageRecord> messages)
    {
        if (_messageCache.Count == 0)
        {
            return;
        }

        var liveIds = messages.Select(message => message.Id).ToHashSet();
        foreach (var staleId in _messageCache.Keys.Where(id => !liveIds.Contains(id)).ToArray())
        {
            _messageCache.Remove(staleId);
        }
    }

    private void PruneToolSegmentCache(IReadOnlyList<ToolExecutionRecord> toolRuns)
    {
        if (_toolSegmentCache.Count == 0)
        {
            return;
        }

        var liveIds = toolRuns.Select(toolRun => toolRun.Id).ToHashSet();
        foreach (var staleId in _toolSegmentCache.Keys.Where(id => !liveIds.Contains(id)).ToArray())
        {
            _toolSegmentCache.Remove(staleId);
        }
    }

    private static string BuildMessageFingerprint(
        MessageRecord message,
        IReadOnlyList<ToolRunPlacement> toolRuns)
    {
        var builder = new StringBuilder();
        AppendMessageFingerprint(builder, message);
        builder.Append('|');

        foreach (var placement in toolRuns)
        {
            AppendToolRunFingerprint(builder, placement.Record);
            builder.Append(':')
                .Append(placement.AfterSegmentIndex)
                .Append(';');
        }

        return builder.ToString();
    }

    private TranscriptRenderItem BuildMessageItem(
        MessageRecord message,
        IReadOnlyList<ToolRunPlacement> toolRuns)
    {
        var renderSegments = new List<TranscriptRenderSegment>();
        if (message.Role == MessageRole.Assistant)
        {
            var segments = AssistantMessageSegmenter.Split(message.MarkdownContent);
            var toolRunsById = toolRuns.ToDictionary(toolRun => toolRun.Record.Id);
            var consumedToolRunIds = new HashSet<Guid>();

            foreach (var segment in segments.Segments)
            {
                if (segment.Kind == AssistantMessageSegmentKind.ToolAnchor)
                {
                    if (segment.ToolExecutionId is Guid toolExecutionId &&
                        toolRunsById.TryGetValue(toolExecutionId, out var placement) &&
                        consumedToolRunIds.Add(toolExecutionId))
                    {
                        renderSegments.Add(BuildToolSegmentCached(placement.Record));
                    }

                    continue;
                }

                renderSegments.Add(new TranscriptRenderSegment(
                    segment.Kind == AssistantMessageSegmentKind.Thinking ? "thinking" : "content",
                    segment.Markdown,
                    segment.IsPending));
            }

            toolRuns = toolRuns.Count > consumedToolRunIds.Count
                ? toolRuns.Where(toolRun => !consumedToolRunIds.Contains(toolRun.Record.Id)).ToArray()
                : [];
        }
        else if (!string.IsNullOrWhiteSpace(message.MarkdownContent))
        {
            renderSegments.Add(new TranscriptRenderSegment(
                "content",
                message.MarkdownContent,
                false));
        }

        if (message.Role == MessageRole.Assistant && toolRuns.Count > 0)
        {
            TranscriptToolRunPresenter.InsertToolSegments(renderSegments, toolRuns, BuildToolSegmentCached);
        }

        return new TranscriptRenderItem(
            message.Id.ToString("D"),
            "message",
            message.Role.ToString().ToLowerInvariant(),
            message.Status.ToString().ToLowerInvariant(),
            renderSegments,
            message.Role == MessageRole.Assistant && message.Status == MessageStatus.Streaming,
            message.CreatedAtUtc.LocalDateTime.ToString("yyyy-MM-dd HH:mm"),
            BuildImageAttachments(message),
            message.Status is MessageStatus.Failed or MessageStatus.Cancelled ? message.ErrorMessage : null);
    }

    private TranscriptRenderSegment BuildToolSegmentCached(ToolExecutionRecord toolRun)
    {
        if (_toolSegmentCache.TryGetValue(toolRun.Id, out var cached) && ReferenceEquals(cached.Record, toolRun))
        {
            return cached.Segment;
        }

        var segment = TranscriptToolRunPresenter.BuildToolSegment(toolRun);
        _toolSegmentCache[toolRun.Id] = (toolRun, segment);
        return segment;
    }

    private IReadOnlyList<TranscriptImageAttachment> BuildImageAttachments(MessageRecord message)
    {
        if (message.Attachments is not { Count: > 0 } attachments)
        {
            return [];
        }

        return attachments
            .Where(attachment => attachment.Kind == MessageAttachmentKind.Image)
            .Select(attachment => new TranscriptImageAttachment(
                attachment.Id.ToString("D"),
                attachment.FileName,
                attachment.MediaType,
                attachment.ByteLength,
                TryCreateAttachmentSourceUrl(attachment)))
            .ToArray();
    }

    private string? TryCreateAttachmentSourceUrl(MessageAttachmentRecord attachment)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(attachment.StoragePath) || !File.Exists(attachment.StoragePath))
            {
                return null;
            }

            var attachmentsRoot = Path.GetFullPath(Path.Combine(_storagePaths.AppDataDirectory, "attachments"));
            var attachmentPath = Path.GetFullPath(attachment.StoragePath);
            var relativePath = Path.GetRelativePath(attachmentsRoot, attachmentPath);
            if (relativePath.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relativePath))
            {
                return null;
            }

            var normalizedPath = relativePath
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');
            return $"https://{AttachmentHostName}/{Uri.EscapeDataString(normalizedPath).Replace("%2F", "/", StringComparison.OrdinalIgnoreCase)}";
        }
        catch
        {
            return null;
        }
    }

    private static void AppendAttachments(
        StringBuilder builder,
        IReadOnlyList<MessageAttachmentRecord>? attachments)
    {
        if (attachments is not { Count: > 0 })
        {
            return;
        }

        foreach (var attachment in attachments)
        {
            builder.Append(attachment.Id.ToString("D"))
                .Append(',')
                .Append((int)attachment.Kind)
                .Append(',')
                .Append(attachment.ByteLength)
                .Append(',');
            AppendTextFingerprint(builder, attachment.FileName);
            AppendTextFingerprint(builder, attachment.MediaType);
            AppendTextFingerprint(builder, attachment.StoragePath);
            builder.Append(File.Exists(attachment.StoragePath) ? '1' : '0')
                .Append(';');
        }
    }

    private static void AppendMessageFingerprint(StringBuilder builder, MessageRecord message)
    {
        builder.Append(message.Id.ToString("D"))
            .Append(':')
            .Append((int)message.Role)
            .Append(':')
            .Append((int)message.Status)
            .Append(':')
            .Append(message.CreatedAtUtc.UtcTicks)
            .Append(':')
            .Append(message.UpdatedAtUtc.UtcTicks)
            .Append(':');
        AppendTextLength(builder, message.MarkdownContent);
        AppendTextFingerprint(builder, message.ErrorMessage);
        AppendAttachments(builder, message.Attachments);
    }

    private static void AppendToolRunFingerprint(StringBuilder builder, ToolExecutionRecord toolRun)
    {
        builder.Append(toolRun.Id.ToString("D"))
            .Append(':')
            .Append((int)toolRun.Status)
            .Append(':')
            .Append(toolRun.CreatedAtUtc.UtcTicks)
            .Append(':')
            .Append(toolRun.UpdatedAtUtc.UtcTicks)
            .Append(':')
            .Append(toolRun.DurationMs)
            .Append(':')
            .Append(toolRun.MessageId)
            .Append(':')
            .Append(toolRun.AfterSegmentIndex)
            .Append(':')
            .Append(toolRun.SourceKind)
            .Append(':');
        AppendTextFingerprint(builder, toolRun.ToolName);
        AppendTextLength(builder, toolRun.ArgumentsJson);
        AppendTextFingerprint(builder, toolRun.ResultSummary);
        AppendTextLength(builder, toolRun.ResultContent);
        AppendTextFingerprint(builder, toolRun.SourceId);
        AppendTextFingerprint(builder, toolRun.DisplayName);
    }

    private static void AppendTextFingerprint(StringBuilder builder, string? value)
    {
        if (value is null)
        {
            builder.Append("null:");
            return;
        }

        builder.Append(value.Length)
            .Append(':')
            .Append(StringComparer.Ordinal.GetHashCode(value))
            .Append(':');
    }

    private static void AppendTextLength(StringBuilder builder, string? value)
        => builder.Append(value?.Length ?? -1).Append(':');
}
