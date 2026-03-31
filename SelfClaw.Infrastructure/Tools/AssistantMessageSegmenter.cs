using System.Text;

namespace SelfClaw.Infrastructure.Tools;

public static class AssistantMessageSegmenter
{
    private const string ThinkOpenTag = "<think>";
    private const string ThinkCloseTag = "</think>";
    private static readonly char[] TrimPrefixChars = ['\r', '\n', '\uFEFF', '\u200B', '\u200C', '\u200D', '\u2060'];

    public static AssistantMessageSegments Split(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return new AssistantMessageSegments(string.Empty, []);
        }

        var source = markdown;
        var firstContentIndex = FindFirstNonWhitespace(source);
        if (firstContentIndex >= source.Length || !StartsWithIgnoreCase(source, firstContentIndex, ThinkOpenTag))
        {
            return BuildVisibleContentSegments(source);
        }

        var segments = new List<AssistantMessageSegment>();
        var visibleContent = new StringBuilder();
        var cursor = firstContentIndex;

        while (cursor < source.Length)
        {
            if (StartsWithIgnoreCase(source, cursor, ThinkOpenTag))
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

            var nextThinkIndex = source.IndexOf(ThinkOpenTag, cursor, StringComparison.OrdinalIgnoreCase);
            if (nextThinkIndex < 0)
            {
                AppendContentSegment(segments, visibleContent, source[cursor..]);
                break;
            }

            AppendContentSegment(segments, visibleContent, source.Substring(cursor, nextThinkIndex - cursor));
            cursor = nextThinkIndex;
        }

        return new AssistantMessageSegments(
            NormalizeContentMarkdown(visibleContent.ToString()),
            segments);
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
        if (!isPending && string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        segments.Add(new AssistantMessageSegment(AssistantMessageSegmentKind.Thinking, normalized, isPending));
    }

    private static string NormalizeThinkingMarkdown(string markdown)
        => markdown.Trim(TrimPrefixChars);

    private static string NormalizeContentMarkdown(string markdown)
        => markdown.TrimStart(TrimPrefixChars);

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
}

