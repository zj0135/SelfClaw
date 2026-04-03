using System.Text;
using System.Text.RegularExpressions;

namespace SelfClaw.Infrastructure.Tools;

public static class AssistantMessageSegmenter
{
    private const string ThinkOpenTag = "<think>";
    private const string ThinkCloseTag = "</think>";
    private const string InternalThinkOpenTag = "<!--selfclaw:think:start-->";
    private const string InternalThinkCloseTag = "<!--selfclaw:think:end-->";
    private const string ToolAnchorPrefix = "<!--selfclaw:tool:";
    private const string ToolAnchorSuffix = "-->";
    private static readonly char[] TrimPrefixChars = ['\r', '\n', '\uFEFF', '\u200B', '\u200C', '\u200D', '\u2060'];

    public static string WrapThinking(string? markdown)
        => string.Concat(InternalThinkOpenTag, markdown ?? string.Empty, InternalThinkCloseTag);

    public static string AppendToolAnchor(string? markdown, Guid toolExecutionId)
        => string.Concat(markdown ?? string.Empty, CreateToolAnchor(toolExecutionId));

    public static bool HasToolAnchors(string? markdown)
        => !string.IsNullOrEmpty(markdown) &&
           markdown.Contains(ToolAnchorPrefix, StringComparison.OrdinalIgnoreCase);

    public static string MergeFinalMarkdown(string? markdown, string? streamedMarkdown)
    {
        var finalMarkdown = markdown ?? string.Empty;
        if (string.IsNullOrEmpty(streamedMarkdown))
        {
            return finalMarkdown;
        }

        var streamedSegments = Split(streamedMarkdown);
        var finalSegments = Split(finalMarkdown);
        var streamedHasPendingThinking = streamedSegments.Segments.Any(item =>
            item.Kind == AssistantMessageSegmentKind.Thinking && item.IsPending);

        if (!streamedHasPendingThinking &&
            string.Equals(streamedSegments.ContentMarkdown, finalSegments.ContentMarkdown, StringComparison.Ordinal) &&
            (streamedSegments.HasThinking || HasToolAnchors(streamedMarkdown)))
        {
            return streamedMarkdown;
        }

        return HasToolAnchors(streamedMarkdown)
            ? RestoreToolAnchors(finalMarkdown, streamedMarkdown)
            : finalMarkdown;
    }

    public static string RestoreToolAnchors(string? markdown, string? anchoredMarkdown)
    {
        var anchors = ExtractToolAnchors(anchoredMarkdown);
        if (anchors.Count == 0)
        {
            return markdown ?? string.Empty;
        }

        var plainMarkdown = RemoveToolAnchors(markdown);
        var builder = new StringBuilder(plainMarkdown);
        var insertedLength = 0;

        foreach (var anchor in anchors)
        {
            var marker = CreateToolAnchor(anchor.ToolExecutionId);
            var insertIndex = Math.Clamp(anchor.Offset + insertedLength, 0, builder.Length);
            builder.Insert(insertIndex, marker);
            insertedLength += marker.Length;
        }

        return builder.ToString();
    }

    public static string RemoveToolAnchors(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return string.Empty;
        }

        var source = NormalizeThinkTagTransitions(markdown);
        var builder = new StringBuilder(source.Length);
        var cursor = 0;

        while (cursor < source.Length)
        {
            if (TryReadToolAnchor(source, cursor, out _, out var markerLength))
            {
                cursor += markerLength;
                continue;
            }

            builder.Append(source[cursor]);
            cursor++;
        }

        return builder.ToString();
    }

    public static AssistantMessageSegments Split(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return new AssistantMessageSegments(string.Empty, []);
        }

        var internalThinking = ExtractInternalThinking(markdown);
        var source = internalThinking.ContentMarkdown;
        var firstContentIndex = FindFirstNonWhitespace(source);
        var firstThinkIndex = FindNextThinkBlockIndex(source, firstContentIndex);
        var hasThinkBlock = firstThinkIndex >= 0;
        var hasToolAnchors = source.Contains(ToolAnchorPrefix, StringComparison.OrdinalIgnoreCase);

        if (!hasThinkBlock && !hasToolAnchors)
        {
            var visibleOnly = BuildVisibleContentSegments(source);
            return new AssistantMessageSegments(
                visibleOnly.ContentMarkdown,
                MergeInternalThinkingSegments(visibleOnly.Segments, internalThinking));
        }

        var segments = new List<AssistantMessageSegment>();
        var visibleContent = new StringBuilder();
        var cursor = 0;

        while (cursor < source.Length)
        {
            if (TryReadToolAnchor(source, cursor, out var toolExecutionId, out var toolAnchorLength))
            {
                segments.Add(new AssistantMessageSegment(
                    AssistantMessageSegmentKind.ToolAnchor,
                    string.Empty,
                    false,
                    toolExecutionId));
                cursor += toolAnchorLength;
                continue;
            }

            if (StartsWithIgnoreCase(source, cursor, ThinkOpenTag) &&
                (segments.LastOrDefault()?.Kind == AssistantMessageSegmentKind.Thinking ||
                 IsThinkBlockBoundary(source, cursor)))
            {
                cursor += ThinkOpenTag.Length;

                var closeIndex = source.IndexOf(ThinkCloseTag, cursor, StringComparison.OrdinalIgnoreCase);
                if (closeIndex < 0)
                {
                    AppendThinkingSegment(segments, source[cursor..], isPending: true);
                    break;
                }

                AppendThinkingSegment(segments, source.Substring(cursor, closeIndex - cursor));
                cursor = closeIndex + ThinkCloseTag.Length;
                continue;
            }

            var nextSpecialIndex = FindNextSpecialIndex(source, cursor, hasThinkBlock);
            if (nextSpecialIndex < 0)
            {
                AppendContentSegment(segments, visibleContent, source[cursor..]);
                break;
            }

            var betweenSegments = source.Substring(cursor, nextSpecialIndex - cursor);
            if (hasThinkBlock &&
                segments.LastOrDefault()?.Kind == AssistantMessageSegmentKind.Thinking &&
                string.IsNullOrWhiteSpace(betweenSegments))
            {
                AppendThinkingSeparator(segments, betweenSegments);
                cursor = nextSpecialIndex;
                continue;
            }

            AppendContentSegment(segments, visibleContent, betweenSegments);
            cursor = nextSpecialIndex;
        }

        var mergedSegments = MergeInternalThinkingSegments(MergeAdjacentThinkingSegments(segments), internalThinking);

        return new AssistantMessageSegments(
            NormalizeContentMarkdown(visibleContent.ToString()),
            mergedSegments);
    }

    private static AssistantMessageSegments BuildVisibleContentSegments(string markdown)
    {
        var normalized = NormalizeContentMarkdown(markdown);
        return string.IsNullOrWhiteSpace(normalized)
            ? new AssistantMessageSegments(normalized, [])
            : new AssistantMessageSegments(
                normalized,
                [new AssistantMessageSegment(AssistantMessageSegmentKind.Content, normalized)]);
    }

    private static void AppendContentSegment(
        ICollection<AssistantMessageSegment> segments,
        StringBuilder visibleContent,
        string segment)
    {
        if (string.IsNullOrEmpty(segment))
        {
            return;
        }

        visibleContent.Append(segment);

        var normalized = NormalizeContentMarkdown(segment);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        segments.Add(new AssistantMessageSegment(AssistantMessageSegmentKind.Content, normalized));
    }

    private static void AppendThinkingSegment(
        ICollection<AssistantMessageSegment> segments,
        string segment,
        bool isPending = false)
    {
        var normalized = NormalizeThinkingMarkdown(segment);
        if (!isPending && normalized.Length == 0)
        {
            return;
        }

        segments.Add(new AssistantMessageSegment(AssistantMessageSegmentKind.Thinking, normalized, isPending));
    }

    private static void AppendThinkingSeparator(
        ICollection<AssistantMessageSegment> segments,
        string separator)
    {
        if (separator.Length == 0)
        {
            return;
        }

        segments.Add(new AssistantMessageSegment(AssistantMessageSegmentKind.Thinking, " "));
    }

    private static IReadOnlyList<AssistantMessageSegment> MergeAdjacentThinkingSegments(IReadOnlyList<AssistantMessageSegment> segments)
    {
        if (segments.Count < 2)
        {
            return segments;
        }

        var mergedSegments = new List<AssistantMessageSegment>(segments.Count);

        foreach (var segment in segments)
        {
            if (segment.Kind == AssistantMessageSegmentKind.Thinking &&
                mergedSegments.LastOrDefault() is { Kind: AssistantMessageSegmentKind.Thinking } previousThinking)
            {
                mergedSegments[^1] = previousThinking with
                {
                    Markdown = JoinThinkingMarkdown(previousThinking.Markdown, segment.Markdown),
                    IsPending = previousThinking.IsPending || segment.IsPending
                };

                continue;
            }

            mergedSegments.Add(segment);
        }

        return mergedSegments;
    }

    private static IReadOnlyList<AssistantMessageSegment> MergeInternalThinkingSegments(
        IReadOnlyList<AssistantMessageSegment> segments,
        InternalThinkingExtraction internalThinking)
    {
        if (!internalThinking.HasThinking)
        {
            return segments;
        }

        var internalSegment = new AssistantMessageSegment(
            AssistantMessageSegmentKind.Thinking,
            internalThinking.ThinkingMarkdown,
            internalThinking.IsPending);

        if (segments.Count > 0 && segments[0].Kind == AssistantMessageSegmentKind.Thinking)
        {
            return
            [
                internalSegment with
                {
                    Markdown = JoinThinkingMarkdown(internalSegment.Markdown, segments[0].Markdown),
                    IsPending = internalSegment.IsPending || segments[0].IsPending
                },
                .. segments.Skip(1)
            ];
        }

        return [internalSegment, .. segments];
    }

    private static string JoinThinkingMarkdown(string current, string next)
    {
        if (string.IsNullOrWhiteSpace(current))
        {
            return next;
        }

        if (string.IsNullOrWhiteSpace(next))
        {
            return current;
        }

        return current + next;
    }

    private static string NormalizeThinkingMarkdown(string markdown)
    {
        var normalized = markdown.ReplaceLineEndings("\n").Trim(TrimPrefixChars);
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        normalized = Regex.Replace(normalized, @"[ \t]{2,}", " ");
        normalized = Regex.Replace(normalized, @"[ \t]+\n", "\n");
        normalized = Regex.Replace(normalized, @"\n[ \t]+", "\n");
        normalized = Regex.Replace(normalized, @"\n{3,}", "\n\n");
        return normalized;
    }

    private static string NormalizeContentMarkdown(string markdown)
        => RemoveToolAnchors(markdown).TrimStart(TrimPrefixChars);

    private static string NormalizeThinkTagTransitions(string markdown)
        => InsertThinkBoundaryNewline(markdown, ThinkCloseTag, ThinkOpenTag);

    private static int FindNextSpecialIndex(string source, int startIndex, bool includeThinkBlocks)
    {
        var nextToolAnchorIndex = source.IndexOf(ToolAnchorPrefix, startIndex, StringComparison.OrdinalIgnoreCase);
        if (!includeThinkBlocks)
        {
            return nextToolAnchorIndex;
        }

        var nextThinkIndex = FindNextThinkBlockIndex(source, startIndex);
        if (nextToolAnchorIndex < 0)
        {
            return nextThinkIndex;
        }

        if (nextThinkIndex < 0)
        {
            return nextToolAnchorIndex;
        }

        return Math.Min(nextToolAnchorIndex, nextThinkIndex);
    }

    private static int FindNextThinkBlockIndex(string source, int startIndex)
        => FindNextExternalThinkBlockIndex(source, startIndex);

    private static List<ToolAnchorPlacement> ExtractToolAnchors(string? markdown)
    {
        var result = new List<ToolAnchorPlacement>();
        if (string.IsNullOrEmpty(markdown))
        {
            return result;
        }

        var source = markdown;
        var plainOffset = 0;
        var cursor = 0;
        while (cursor < source.Length)
        {
            if (TryReadToolAnchor(source, cursor, out var toolExecutionId, out var markerLength))
            {
                result.Add(new ToolAnchorPlacement(toolExecutionId, plainOffset));
                cursor += markerLength;
                continue;
            }

            plainOffset++;
            cursor++;
        }

        return result;
    }

    private static string CreateToolAnchor(Guid toolExecutionId)
        => $"{ToolAnchorPrefix}{toolExecutionId:D}{ToolAnchorSuffix}";

    private static string InsertThinkBoundaryNewline(string markdown, string closeTag, string openTag)
        => Regex.Replace(
            markdown,
            $"(?is)({Regex.Escape(closeTag)})[ \\t]*({Regex.Escape(openTag)})",
            "$1\n$2");

    private static int FindNextExternalThinkBlockIndex(string source, int startIndex)
    {
        var searchIndex = Math.Max(0, startIndex);
        while (searchIndex < source.Length)
        {
            var thinkIndex = source.IndexOf(ThinkOpenTag, searchIndex, StringComparison.OrdinalIgnoreCase);
            if (thinkIndex < 0)
            {
                return -1;
            }

            if (IsThinkBlockBoundary(source, thinkIndex))
            {
                return thinkIndex;
            }

            searchIndex = thinkIndex + ThinkOpenTag.Length;
        }

        return -1;
    }

    private static bool TryReadToolAnchor(string source, int index, out Guid toolExecutionId, out int markerLength)
    {
        toolExecutionId = Guid.Empty;
        markerLength = 0;

        if (!StartsWithIgnoreCase(source, index, ToolAnchorPrefix))
        {
            return false;
        }

        var idStartIndex = index + ToolAnchorPrefix.Length;
        var endIndex = source.IndexOf(ToolAnchorSuffix, idStartIndex, StringComparison.OrdinalIgnoreCase);
        if (endIndex < 0)
        {
            return false;
        }

        var idText = source.Substring(idStartIndex, endIndex - idStartIndex).Trim();
        if (!Guid.TryParse(idText, out toolExecutionId))
        {
            return false;
        }

        markerLength = endIndex + ToolAnchorSuffix.Length - index;
        return true;
    }

    private static int FindFirstNonWhitespace(string source, int startIndex = 0)
    {
        var index = startIndex;
        while (index < source.Length && IsIgnorableLeadingCharacter(source[index]))
        {
            index++;
        }

        return index;
    }

    private static bool IsIgnorableLeadingCharacter(char value)
        => char.IsWhiteSpace(value) ||
           value is '\uFEFF' or '\u200B' or '\u200C' or '\u200D' or '\u2060';

    private static bool IsThinkBlockBoundary(string source, int index)
    {
        if (index <= 0)
        {
            return true;
        }

        for (var current = index - 1; current >= 0; current--)
        {
            var value = source[current];
            if (value == '\n' || value == '\r')
            {
                return true;
            }

            if (!IsInlineIgnorableCharacter(value))
            {
                return EndsWithIgnoreCase(source, current + 1, ThinkCloseTag);
            }
        }

        return true;
    }

    private static bool StartsWithIgnoreCase(string source, int index, string value)
        => index >= 0 &&
           index + value.Length <= source.Length &&
           string.Compare(source, index, value, 0, value.Length, StringComparison.OrdinalIgnoreCase) == 0;

    private static bool EndsWithIgnoreCase(string source, int endExclusive, string value)
        => endExclusive >= value.Length &&
           string.Compare(source, endExclusive - value.Length, value, 0, value.Length, StringComparison.OrdinalIgnoreCase) == 0;

    private static bool IsInlineIgnorableCharacter(char value)
        => value is ' ' or '\t' or '\uFEFF' or '\u200B' or '\u200C' or '\u200D' or '\u2060';

    private sealed record ToolAnchorPlacement(Guid ToolExecutionId, int Offset);

    private static InternalThinkingExtraction ExtractInternalThinking(string markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return new InternalThinkingExtraction(string.Empty, string.Empty, false);
        }

        var visibleContent = new StringBuilder(markdown.Length);
        var thinkingContent = new StringBuilder();
        var cursor = 0;
        var isPending = false;

        while (cursor < markdown.Length)
        {
            if (!StartsWithIgnoreCase(markdown, cursor, InternalThinkOpenTag))
            {
                visibleContent.Append(markdown[cursor]);
                cursor++;
                continue;
            }

            cursor += InternalThinkOpenTag.Length;
            var closeIndex = markdown.IndexOf(InternalThinkCloseTag, cursor, StringComparison.OrdinalIgnoreCase);
            if (closeIndex < 0)
            {
                thinkingContent.Append(markdown[cursor..]);
                isPending = true;
                break;
            }

            thinkingContent.Append(markdown, cursor, closeIndex - cursor);
            cursor = closeIndex + InternalThinkCloseTag.Length;
        }

        return new InternalThinkingExtraction(
            visibleContent.ToString(),
            NormalizeThinkingMarkdown(thinkingContent.ToString()),
            isPending);
    }

    private sealed record InternalThinkingExtraction(
        string ContentMarkdown,
        string ThinkingMarkdown,
        bool IsPending)
    {
        public bool HasThinking => IsPending || !string.IsNullOrWhiteSpace(ThinkingMarkdown);
    }
}

