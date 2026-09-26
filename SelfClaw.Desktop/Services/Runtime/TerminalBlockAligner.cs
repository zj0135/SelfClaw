using System.Text;
using SelfClaw.Core.Models;

namespace SelfClaw.Desktop.Services.Runtime;

/// <summary>
/// Aligns the terminal FinalText against the streamed block sequence. Fast path keeps the
/// streamed blocks when the visible text already matches; slow path maps each ToolCall block's
/// character offset in the streamed text onto the final text and rebuilds Text/ToolCall blocks.
/// Notice and Thinking blocks are preserved from the stream and prepended, notices first.
/// </summary>
internal static class TerminalBlockAligner
{
    public static IReadOnlyList<MessageSegmentRecord> Align(
        Guid messageId,
        IReadOnlyList<MessageSegmentRecord> streamedSegments,
        string? finalText)
    {
        var streamedMarkdown = string.Concat(streamedSegments
            .Where(segment => segment.Kind == MessageSegmentKind.Text)
            .Select(segment => segment.Text ?? string.Empty));

        if (finalText is null || string.Equals(streamedMarkdown, finalText, StringComparison.Ordinal))
        {
            return streamedSegments;
        }

        // Character offsets of each ToolCall within the streamed text, using the anchor-marker
        // offset algorithm from the previous markdown implementation: walk the streamed block
        // order, counting only Text characters.
        var toolOffsets = new List<(Guid ToolRunId, int Offset)>();
        var textCursor = 0;
        foreach (var segment in streamedSegments)
        {
            if (segment.Kind == MessageSegmentKind.Text)
            {
                textCursor += (segment.Text ?? string.Empty).Length;
            }
            else if (segment.Kind == MessageSegmentKind.ToolCall && segment.ToolRunId is Guid toolRunId)
            {
                toolOffsets.Add((toolRunId, textCursor));
            }
        }

        if (toolOffsets.Count == 0)
        {
            return RebuildTextOnly(messageId, streamedSegments, finalText);
        }

        var builder = new StringBuilder(finalText);
        var insertedLength = 0;
        foreach (var (toolRunId, offset) in toolOffsets)
        {
            var insertIndex = Math.Clamp(offset + insertedLength, 0, builder.Length);
            builder.Insert(insertIndex, '\u0000');
            insertedLength += 1;
        }

        var aligned = builder.ToString();
        var result = new List<MessageSegmentRecord>();
        var ordinal = 0;
        var chunkStart = 0;
        for (var index = 0; index <= aligned.Length; index++)
        {
            if (index < aligned.Length && aligned[index] != '\u0000')
            {
                continue;
            }

            var text = aligned.Substring(chunkStart, index - chunkStart);
            if (text.Length > 0)
            {
                result.Add(new MessageSegmentRecord(messageId, ordinal++, MessageSegmentKind.Text, text, null));
            }

            if (index < aligned.Length)
            {
                var toolRunId = toolOffsets[CountMarkers(aligned, index)].ToolRunId;
                result.Add(new MessageSegmentRecord(messageId, ordinal++, MessageSegmentKind.ToolCall, null, toolRunId));
            }

            chunkStart = index + 1;
        }

        return PrependPreservedBlocks(streamedSegments, result);
    }

    private static IReadOnlyList<MessageSegmentRecord> RebuildTextOnly(
        Guid messageId,
        IReadOnlyList<MessageSegmentRecord> streamedSegments,
        string finalText)
    {
        var result = new List<MessageSegmentRecord>();
        if (finalText.Length > 0)
        {
            result.Add(new MessageSegmentRecord(messageId, 0, MessageSegmentKind.Text, finalText, null));
        }

        return PrependPreservedBlocks(streamedSegments, result);
    }

    private static IReadOnlyList<MessageSegmentRecord> PrependPreservedBlocks(
        IReadOnlyList<MessageSegmentRecord> streamedSegments,
        List<MessageSegmentRecord> result)
    {
        // Notices keep their streamed order ahead of thinking; the rebuild only replaces visible content.
        var prefix = streamedSegments
            .Where(segment => segment.Kind is MessageSegmentKind.Notice or MessageSegmentKind.Thinking)
            .ToList();
        if (prefix.Count == 0)
        {
            return result;
        }

        return prefix.Concat(result)
            .Select((segment, index) => segment with { Ordinal = index })
            .ToArray();
    }

    private static int CountMarkers(string aligned, int upToIndex)
    {
        var count = 0;
        for (var index = 0; index < upToIndex; index++)
        {
            if (aligned[index] == '\u0000')
            {
                count++;
            }
        }

        return count;
    }
}
