using System.Text;
using System.Text.RegularExpressions;

namespace SelfClaw.Infrastructure.Tools;

public static class AssistantMessageSegmenter
{
    private const string ThinkOpenTag = "<think>";
    private const string ThinkCloseTag = "</think>";
    private const string ToolAnchorPrefix = "<!--selfclaw:tool:";
    private const string ToolAnchorSuffix = "-->";
    private static readonly char[] TrimPrefixChars = ['\r', '\n', '\uFEFF', '\u200B', '\u200C', '\u200D', '\u2060'];

    public static string AppendToolAnchor(string? markdown, Guid toolExecutionId)
        => string.Concat(markdown ?? string.Empty, CreateToolAnchor(toolExecutionId));

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

        var source = markdown;
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

        var source = markdown;
        var firstContentIndex = FindFirstNonWhitespace(source);
        var hasLeadingThinkBlock = firstContentIndex < source.Length && StartsWithIgnoreCase(source, firstContentIndex, ThinkOpenTag);
        var hasToolAnchors = source.Contains(ToolAnchorPrefix, StringComparison.OrdinalIgnoreCase);

        if (!hasLeadingThinkBlock && !hasToolAnchors)
        {
            return BuildVisibleContentSegments(source);
        }

        var segments = new List<AssistantMessageSegment>();
        var visibleContent = new StringBuilder();
        var cursor = hasLeadingThinkBlock ? firstContentIndex : 0;

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

            if (hasLeadingThinkBlock && StartsWithIgnoreCase(source, cursor, ThinkOpenTag))
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

            var nextSpecialIndex = FindNextSpecialIndex(source, cursor, hasLeadingThinkBlock);
            if (nextSpecialIndex < 0)
            {
                AppendContentSegment(segments, visibleContent, source[cursor..]);
                break;
            }

            var betweenSegments = source.Substring(cursor, nextSpecialIndex - cursor);
            if (hasLeadingThinkBlock &&
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

        return new AssistantMessageSegments(
            NormalizeContentMarkdown(visibleContent.ToString()),
            MergeAdjacentThinkingSegments(segments));
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

    private static int FindNextSpecialIndex(string source, int startIndex, bool includeThinkBlocks)
    {
        var nextToolAnchorIndex = source.IndexOf(ToolAnchorPrefix, startIndex, StringComparison.OrdinalIgnoreCase);
        if (!includeThinkBlocks)
        {
            return nextToolAnchorIndex;
        }

        var nextThinkIndex = source.IndexOf(ThinkOpenTag, startIndex, StringComparison.OrdinalIgnoreCase);
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

    private static bool StartsWithIgnoreCase(string source, int index, string value)
        => index >= 0 &&
           index + value.Length <= source.Length &&
           string.Compare(source, index, value, 0, value.Length, StringComparison.OrdinalIgnoreCase) == 0;

    private sealed record ToolAnchorPlacement(Guid ToolExecutionId, int Offset);
}

