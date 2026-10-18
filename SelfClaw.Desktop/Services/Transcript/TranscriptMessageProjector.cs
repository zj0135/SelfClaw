using SelfClaw.Desktop.Services.Transcript.Views;
using System.IO;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Attachments;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Desktop.Services.Transcript;

internal sealed class TranscriptMessageProjector
{
    private readonly StoragePaths _storagePaths;
    private readonly Dictionary<Guid, (ToolExecutionRecord Record, TranscriptRenderSegment Segment)> _toolSegmentCache = [];

    internal TranscriptMessageProjector(StoragePaths storagePaths)
    {
        ArgumentNullException.ThrowIfNull(storagePaths);
        _storagePaths = storagePaths;
    }

    internal TranscriptRenderItem Build(
        MessageRecord message,
        ConversationTurnRecord turn,
        IReadOnlyList<ToolExecutionRecord> conversationToolRuns,
        bool includeTurnOutcome = true)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(turn);
        ArgumentNullException.ThrowIfNull(conversationToolRuns);
        if (message.TurnId != turn.Id || message.ConversationId != turn.ConversationId || message.Sequence <= 0)
        {
            throw new InvalidDataException("Transcript fragments require explicit turn ownership and a positive sequence.");
        }

        return new TranscriptRenderItem(
            message.Id.ToString("D"),
            "message",
            message.Role.ToString().ToLowerInvariant(),
            message.Status.ToString().ToLowerInvariant(),
            BuildSegments(message, conversationToolRuns),
            message.Role == MessageRole.Assistant && message.Status == MessageStatus.Streaming,
            message.CreatedAtUtc.LocalDateTime.ToString("yyyy-MM-dd HH:mm"),
            BuildImageAttachments(message),
            includeTurnOutcome && message.Role == MessageRole.Assistant ? CreateTurnOutcome(turn) : null);
    }

    internal TranscriptRenderItem BuildTurnOutcome(ConversationTurnRecord turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        return new TranscriptRenderItem(
            $"turn-outcome:{turn.Id:D}", "turn-outcome", "system", turn.Status.ToString().ToLowerInvariant(),
            [], false, (turn.CompletedAtUtc ?? turn.StartedAtUtc).LocalDateTime.ToString("yyyy-MM-dd HH:mm"),
            TurnOutcome: CreateTurnOutcome(turn));
    }

    private static TranscriptTurnOutcome CreateTurnOutcome(ConversationTurnRecord turn)
        => new(turn.Id.ToString("D"), turn.Status.ToString().ToLowerInvariant(), turn.ErrorMessage,
            turn.CompletedAtUtc is { } completed ? Math.Max(0L, (long)(completed - turn.StartedAtUtc).TotalMilliseconds) : null,
            turn.Usage);

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
        if (message.Role == MessageRole.Assistant)
        {
            var toolRunsById = conversationToolRuns
                .Where(toolRun => toolRun.MessageId == message.Id && toolRun.ConversationId == message.ConversationId)
                .ToDictionary(toolRun => toolRun.Id);

            var thinkingOrdinal = 0;
            foreach (var segment in segments.OrderBy(segment => segment.Ordinal))
            {
                if (segment.MessageId != message.Id)
                {
                    throw new InvalidDataException("A transcript segment must belong to its message.");
                }
                var thinkingIndex = segment.Kind == MessageSegmentKind.Thinking ? thinkingOrdinal++ : -1;
                switch (segment.Kind)
                {
                    // 纯空白段（流式残留的换行/缩进）不是内容：投影时直接丢掉，
                    // 否则 Vue 会在两个块之间渲染一个空的 .body 占位。
                    case MessageSegmentKind.Text when !string.IsNullOrWhiteSpace(segment.Text):
                        renderSegments.Add(new TranscriptRenderSegment(
                            "content",
                            segment.Text,
                            false,
                            SegmentId: $"{message.Id:D}:text:{segment.Ordinal}",
                            SegmentOrdinal: segment.Ordinal));
                        break;
                    case MessageSegmentKind.Thinking when !string.IsNullOrWhiteSpace(segment.Text):
                        renderSegments.Add(new TranscriptRenderSegment(
                            "thinking",
                            segment.Text,
                            message.Status == MessageStatus.Streaming && segment.Ordinal == segments[^1].Ordinal,
                            SegmentId: $"{message.Id:D}:thinking:{thinkingIndex}",
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
                SegmentId: $"{message.Id:D}:text:0"));
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
        => AttachmentPathResolver.TryCreateSourceUrl(
            AttachmentPathResolver.ResolveRoot(_storagePaths.AppDataDirectory),
            attachment.StoragePath,
            out var sourceUrl)
            ? sourceUrl
            : null;
}
