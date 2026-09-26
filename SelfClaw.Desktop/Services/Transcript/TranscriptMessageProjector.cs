using SelfClaw.Desktop.Services.Transcript.Views;
using System.IO;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Desktop.Services.Transcript;

internal sealed class TranscriptMessageProjector
{
    private const string AttachmentHostName = "attachments.selfclaw.local";
    private readonly StoragePaths _storagePaths;
    private readonly Dictionary<Guid, (ToolExecutionRecord Record, TranscriptRenderSegment Segment)> _toolSegmentCache = [];

    internal TranscriptMessageProjector(StoragePaths storagePaths)
    {
        ArgumentNullException.ThrowIfNull(storagePaths);
        _storagePaths = storagePaths;
    }

    internal TranscriptRenderItem Build(
        MessageRecord message,
        IReadOnlyList<ToolExecutionRecord> conversationToolRuns)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(conversationToolRuns);

        return new TranscriptRenderItem(
            message.Id.ToString("D"),
            "message",
            message.Role.ToString().ToLowerInvariant(),
            message.Status.ToString().ToLowerInvariant(),
            BuildSegments(message, conversationToolRuns),
            message.Role == MessageRole.Assistant && message.Status == MessageStatus.Streaming,
            message.CreatedAtUtc.LocalDateTime.ToString("yyyy-MM-dd HH:mm"),
            BuildImageAttachments(message),
            message.Status is MessageStatus.Failed or MessageStatus.Cancelled or MessageStatus.Truncated
                or MessageStatus.Blocked
                ? message.ErrorMessage
                : null);
    }

    internal void PruneToolSegmentCache(IReadOnlyList<ToolExecutionRecord> toolRuns)
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

    private IReadOnlyList<TranscriptRenderSegment> BuildSegments(
        MessageRecord message,
        IReadOnlyList<ToolExecutionRecord> conversationToolRuns)
    {
        var renderSegments = new List<TranscriptRenderSegment>();
        var segments = message.Segments ?? [];
        if (message.Role == MessageRole.Assistant && segments.Count > 0)
        {
            var toolRunsById = conversationToolRuns
                .Where(toolRun => toolRun.MessageId == message.Id)
                .ToDictionary(toolRun => toolRun.Id);

            var thinkingOrdinal = 0;
            foreach (var segment in segments)
            {
                var thinkingIndex = segment.Kind == MessageSegmentKind.Thinking ? thinkingOrdinal++ : -1;
                switch (segment.Kind)
                {
                    case MessageSegmentKind.Text when !string.IsNullOrEmpty(segment.Text):
                        renderSegments.Add(new TranscriptRenderSegment(
                            "content",
                            segment.Text,
                            false,
                            SegmentId: $"{message.Id:D}:text:{segment.Ordinal}",
                            SegmentOrdinal: segment.Ordinal));
                        break;
                    case MessageSegmentKind.Thinking when !string.IsNullOrEmpty(segment.Text):
                        renderSegments.Add(new TranscriptRenderSegment(
                            "thinking",
                            segment.Text,
                            message.Status == MessageStatus.Streaming && segment.Ordinal == segments[^1].Ordinal,
                            SegmentId: $"{message.Id:D}:thinking:{thinkingIndex}",
                            SegmentOrdinal: segment.Ordinal));
                        break;
                    case MessageSegmentKind.Notice when !string.IsNullOrEmpty(segment.Text):
                        renderSegments.Add(new TranscriptRenderSegment(
                            "notice",
                            segment.Text,
                            false,
                            SegmentId: $"{message.Id:D}:notice:{segment.Ordinal}",
                            SegmentOrdinal: segment.Ordinal));
                        break;
                    case MessageSegmentKind.ToolCall when segment.ToolRunId is Guid toolRunId:
                        if (toolRunsById.TryGetValue(toolRunId, out var toolRun))
                        {
                            renderSegments.Add(BuildToolSegmentCached(toolRun) with { SegmentOrdinal = segment.Ordinal });
                        }

                        break;
                    default:
                        break;
                }
            }
        }
        else if (!string.IsNullOrWhiteSpace(message.MarkdownContent))
        {
            renderSegments.Add(new TranscriptRenderSegment(
                "content",
                message.MarkdownContent,
                false,
                SegmentId: $"{message.Id:D}:text:legacy"));
        }

        return renderSegments;
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


}
