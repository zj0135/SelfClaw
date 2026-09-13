using System.Text;

namespace SelfClaw.Infrastructure.Tools.Workspace;

internal static class WorkspaceTextEditor
{
    internal static (string Content, string LineEnding, int Replacements, string? Error) Apply(
        string original, string oldText, string newText, bool replaceAll)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentException.ThrowIfNullOrEmpty(oldText);
        ArgumentNullException.ThrowIfNull(newText);
        var lineEnding = DetectLineEnding(original);
        var normalizedOriginal = NormalizeToLf(original);
        var normalizedOld = NormalizeToLf(oldText);
        var normalizedNew = NormalizeToLf(newText);
        var occurrences = CountOccurrences(normalizedOriginal, normalizedOld);
        if (occurrences == 0)
        {
            if (!replaceAll && TryReplaceUniqueLineBlock(normalizedOriginal, normalizedOld, normalizedNew) is { } updatedLines)
            {
                return (updatedLines, lineEnding, 1, null);
            }

            return (normalizedOriginal, lineEnding, 0, BuildNotFoundMessage(normalizedOriginal, normalizedOld));
        }

        if (occurrences > 1 && !replaceAll)
        {
            return (normalizedOriginal, lineEnding, 0,
                $"The text to replace appears {occurrences} times. Provide more context to make it unique, or set replaceAll to replace every occurrence.");
        }

        var updated = replaceAll
            ? normalizedOriginal.Replace(normalizedOld, normalizedNew, StringComparison.Ordinal)
            : ReplaceFirst(normalizedOriginal, normalizedOld, normalizedNew);
        return (updated, lineEnding, replaceAll ? occurrences : 1, null);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static string ReplaceFirst(string haystack, string needle, string replacement)
    {
        var index = haystack.IndexOf(needle, StringComparison.Ordinal);
        if (index < 0)
        {
            return haystack;
        }

        return string.Concat(
            haystack.AsSpan(0, index),
            replacement,
            haystack.AsSpan(index + needle.Length));
    }

    private static string DetectLineEnding(string text)
        => text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    internal static string NormalizeToLf(string text)
    {
        if (text.Length == 0 || !text.Contains('\r'))
        {
            return text;
        }

        return text.Replace("\r\n", "\n").Replace('\r', '\n');
    }

    private static string? TryReplaceUniqueLineBlock(string haystack, string needle, string replacement)
    {
        var needleLines = SplitLines(needle);
        if (needleLines.Length == 0)
        {
            return null;
        }

        var needleSignatures = new string[needleLines.Length];
        for (var i = 0; i < needleLines.Length; i++)
        {
            needleSignatures[i] = LineSignature(needleLines[i]);
        }

        var haystackLines = SplitLines(haystack);
        if (needleSignatures.Length > haystackLines.Length)
        {
            return null;
        }

        var lineSignatures = new string[haystackLines.Length];
        var lineStarts = new int[haystackLines.Length];
        var offset = 0;
        for (var i = 0; i < haystackLines.Length; i++)
        {
            lineSignatures[i] = LineSignature(haystackLines[i]);
            lineStarts[i] = offset;
            offset += haystackLines[i].Length + 1; // content + the trailing '\n'
        }

        var matchStart = -1;
        for (var start = 0; start + needleSignatures.Length <= haystackLines.Length; start++)
        {
            var ok = true;
            for (var k = 0; k < needleSignatures.Length; k++)
            {
                if (!string.Equals(lineSignatures[start + k], needleSignatures[k], StringComparison.Ordinal))
                {
                    ok = false;
                    break;
                }
            }

            if (!ok)
            {
                continue;
            }

            // Ambiguous — refuse rather than risk editing the wrong location.
            if (matchStart >= 0)
            {
                return null;
            }

            matchStart = start;
        }

        if (matchStart < 0)
        {
            return null;
        }

        var lastLineIndex = matchStart + needleSignatures.Length - 1;
        var beforeOffset = lineStarts[matchStart];
        var hasFollowing = lastLineIndex < haystackLines.Length - 1;
        var afterOffset = hasFollowing
            ? lineStarts[lastLineIndex + 1]
            : haystack.Length;
        var eofHadNewline = !hasFollowing
            && haystack.Length > 0
            && haystack[^1] == '\n';

        // Splice the replacement in place of the matched block. When the block had a
        // trailing newline (following content, or EOF that originally ended with one)
        // and the replacement does not, add it back so line structure is preserved.
        var finalReplacement = replacement;
        if (finalReplacement.Length > 0
            && !finalReplacement.EndsWith('\n')
            && (hasFollowing || eofHadNewline))
        {
            finalReplacement += "\n";
        }

        return string.Concat(
            haystack.AsSpan(0, beforeOffset),
            finalReplacement,
            haystack.AsSpan(afterOffset));
    }

    private static string[] SplitLines(string text)
    {
        if (text.Length == 0)
        {
            return [];
        }

        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                lines.Add(text.Substring(start, i - start));
                start = i + 1;
            }
        }

        if (start < text.Length)
        {
            lines.Add(text.Substring(start, text.Length - start));
        }

        return lines.ToArray();
    }

    private static string LineSignature(string line)
    {
        var start = 0;
        var end = line.Length;
        while (start < end && char.IsWhiteSpace(line[start]))
        {
            start++;
        }

        while (end > start && char.IsWhiteSpace(line[end - 1]))
        {
            end--;
        }

        var builder = new StringBuilder(end - start);
        var inWhitespace = false;
        for (var i = start; i < end; i++)
        {
            var current = line[i];
            if (char.IsWhiteSpace(current))
            {
                if (!inWhitespace)
                {
                    builder.Append(' ');
                    inWhitespace = true;
                }

                continue;
            }

            builder.Append(current);
            inWhitespace = false;
        }

        return builder.ToString();
    }

    private static string BuildNotFoundMessage(string haystack, string needle)
    {
        var needleFirstLine = SplitLines(needle);
        var needleSignature = needleFirstLine.Length > 0
            ? LineSignature(needleFirstLine[0])
            : string.Empty;

        var haystackLines = SplitLines(haystack);
        var bestLine = -1;
        var bestScore = 0;
        for (var i = 0; i < haystackLines.Length; i++)
        {
            var signature = LineSignature(haystackLines[i]);
            if (signature.Length == 0)
            {
                continue;
            }

            var score = LongestCommonPrefix(signature, needleSignature);
            if (score > bestScore)
            {
                bestScore = score;
                bestLine = i;
            }
        }

        if (bestLine < 0)
        {
            return "The text to replace was not found. Re-read the file with read_file and copy the exact snippet (including indentation) into oldText.";
        }

        var fromLine = Math.Max(0, bestLine - 2);
        var toLine = Math.Min(haystackLines.Length - 1, bestLine + 2);
        var builder = new StringBuilder();
        builder.Append("The text to replace was not found. The closest lines in the file are:");
        for (var i = fromLine; i <= toLine; i++)
        {
            builder.Append('\n');
            builder.Append(i + 1);
            builder.Append(": ");
            builder.Append(haystackLines[i]);
        }

        builder.Append("\nRe-read the file and ensure oldText matches the exact characters (indentation, punctuation, and surrounding context).");
        return builder.ToString();
    }

    private static int LongestCommonPrefix(string a, string b)
    {
        var limit = Math.Min(a.Length, b.Length);
        for (var i = 0; i < limit; i++)
        {
            if (a[i] != b[i])
            {
                return i;
            }
        }

        return limit;
    }
}
