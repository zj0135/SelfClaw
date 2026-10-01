using System.Text;
using SelfClaw.Core.Models;

namespace SelfClaw.Desktop.Services.Runtime;

/// <summary>
/// Block accumulator for one streaming assistant message. Content arrives as typed events
/// (text/thinking/tool call); block order is the transcript order, so no textual markers are
/// needed to record tool placement.
/// </summary>
internal sealed class StreamingAssistantContent
{
    private readonly List<ContentBlock> _blocks = [];
    private Guid? _messageId;

    private sealed record ContentBlock(
        MessageSegmentKind Kind,
        StringBuilder Text,
        Guid? ToolRunId);

    public long Revision { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public void Initialize(Guid messageId, IReadOnlyList<MessageSegmentRecord>? initialSegments, DateTimeOffset updatedAtUtc)
    {
        _messageId = messageId;
        if (initialSegments is { Count: > 0 })
        {
            foreach (var segment in initialSegments.OrderBy(item => item.Ordinal))
            {
                _blocks.Add(new ContentBlock(
                    segment.Kind,
                    segment.Text is null ? new StringBuilder() : new StringBuilder(segment.Text),
                    segment.ToolRunId));
            }

            MarkChanged(updatedAtUtc);
        }
    }

    public void AppendText(string delta, DateTimeOffset updatedAtUtc)
    {
        if (AppendToTail(MessageSegmentKind.Text, delta))
        {
            MarkChanged(updatedAtUtc);
        }
    }

    public void AppendThinking(string delta, DateTimeOffset updatedAtUtc)
    {
        if (AppendToTail(MessageSegmentKind.Thinking, delta))
        {
            MarkChanged(updatedAtUtc);
        }
    }

    public void AppendToolCall(Guid toolRunId, DateTimeOffset updatedAtUtc)
    {
        _blocks.Add(new ContentBlock(MessageSegmentKind.ToolCall, new StringBuilder(), toolRunId));
        MarkChanged(updatedAtUtc);
    }

    /// <summary>
    /// Turn-level notices each stay their own block: two consecutive notices are distinct messages and
    /// must not merge into one line.
    /// </summary>
    public void AppendNotice(string text, DateTimeOffset updatedAtUtc)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        _blocks.Add(new ContentBlock(MessageSegmentKind.Notice, new StringBuilder(text), null));
        MarkChanged(updatedAtUtc);
    }

    public void CompleteThinking(DateTimeOffset updatedAtUtc)
    {
        // A Thinking block is closed implicitly when a different kind of content follows; this
        // exists so "pending thinking" can be finalized without content changes.
        if (_blocks.LastOrDefault() is { Kind: MessageSegmentKind.Thinking })
        {
            MarkChanged(updatedAtUtc);
        }
    }

    public IReadOnlyList<MessageSegmentRecord> BuildSegments()
    {
        var messageId = _messageId ?? Guid.Empty;
        return _blocks
            .Select((item, index) => new MessageSegmentRecord(
                messageId,
                index,
                item.Kind,
                item.Kind == MessageSegmentKind.ToolCall ? null : item.Text.ToString(),
                item.Kind == MessageSegmentKind.ToolCall ? item.ToolRunId : null))
            .ToArray();
    }

    /// <summary>Derived plain-text markdown: all Text blocks joined, no markers.</summary>
    public string BuildMarkdown()
        => string.Concat(_blocks
            .Where(item => item.Kind == MessageSegmentKind.Text)
            .Select(item => item.Text.ToString()));

    private bool AppendToTail(MessageSegmentKind kind, string delta)
    {
        if (_blocks.LastOrDefault() is { } tail && tail.Kind == kind)
        {
            if (delta.Length == 0)
            {
                return false;
            }

            tail.Text.Append(delta);
            return true;
        }

        // 纯空白只在延续已有块时有意义。用它开一个新块只会落下一个空段，
        // 投影时渲染成占位空行，还会把前后两个工具调用切成两组。
        if (string.IsNullOrWhiteSpace(delta))
        {
            return false;
        }

        _blocks.Add(new ContentBlock(kind, new StringBuilder(delta), null));
        return true;
    }

    private void MarkChanged(DateTimeOffset updatedAtUtc)
    {
        Revision = checked(Revision + 1);
        UpdatedAtUtc = updatedAtUtc;
    }
}
