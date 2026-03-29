using System.Text;

namespace SelfClaw.Infrastructure.Tools;

public static class AssistantMessageSegmenter
{
    private const string ThinkOpenTag = "<think>";
    private const string ThinkCloseTag = "</think>";

    public static AssistantMessageSegments Split(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return new AssistantMessageSegments(string.Empty, null);
        }

        var source = markdown;
        var firstContentIndex = FindFirstNonWhitespace(source);
        if (firstContentIndex >= source.Length || !StartsWithIgnoreCase(source, firstContentIndex, ThinkOpenTag))
        {
            return new AssistantMessageSegments(source, null);
        }

        var thinkingSegments = new List<string>();
        var cursor = firstContentIndex;

        while (cursor < source.Length && StartsWithIgnoreCase(source, cursor, ThinkOpenTag))
        {
            cursor += ThinkOpenTag.Length;

            var closeIndex = source.IndexOf(ThinkCloseTag, cursor, StringComparison.OrdinalIgnoreCase);
            if (closeIndex < 0)
            {
                return new AssistantMessageSegments(
                    string.Empty,
                    JoinThinkingSegments(thinkingSegments, source[cursor..]));
            }

            thinkingSegments.Add(source.Substring(cursor, closeIndex - cursor));
            cursor = closeIndex + ThinkCloseTag.Length;

            var nextContentIndex = FindFirstNonWhitespace(source, cursor);
            if (nextContentIndex >= source.Length || !StartsWithIgnoreCase(source, nextContentIndex, ThinkOpenTag))
            {
                return new AssistantMessageSegments(
                    NormalizeContentMarkdown(source[cursor..]),
                    JoinThinkingSegments(thinkingSegments));
            }

            cursor = nextContentIndex;
        }

        return new AssistantMessageSegments(source, null);
    }

    private static string JoinThinkingSegments(IEnumerable<string> segments, string? trailingSegment = null)
    {
        var builder = new StringBuilder();

        foreach (var segment in segments)
        {
            AppendNormalizedSegment(builder, segment);
        }

        if (!string.IsNullOrEmpty(trailingSegment))
        {
            AppendNormalizedSegment(builder, trailingSegment);
        }

        return builder.Length == 0 ? string.Empty : builder.ToString();
    }

    private static void AppendNormalizedSegment(StringBuilder builder, string segment)
    {
        var normalized = NormalizeThinkingMarkdown(segment);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.Append("\n\n");
        }

        builder.Append(normalized);
    }

    private static string NormalizeThinkingMarkdown(string markdown)
        => markdown.Trim('\r', '\n');

    private static string NormalizeContentMarkdown(string markdown)
        => markdown.TrimStart('\r', '\n');

    private static int FindFirstNonWhitespace(string source, int startIndex = 0)
    {
        var index = startIndex;
        while (index < source.Length && char.IsWhiteSpace(source[index]))
        {
            index++;
        }

        return index;
    }

    private static bool StartsWithIgnoreCase(string source, int index, string value)
        => index >= 0 &&
           index + value.Length <= source.Length &&
           string.Compare(source, index, value, 0, value.Length, StringComparison.OrdinalIgnoreCase) == 0;
}

