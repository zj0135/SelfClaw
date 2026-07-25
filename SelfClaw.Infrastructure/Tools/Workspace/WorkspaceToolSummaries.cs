using System.Globalization;
using System.Text;
using SelfClaw.Core.Models;

namespace SelfClaw.Infrastructure.Tools.Workspace;

internal static class WorkspaceToolSummaries
{
    public static string Summarize(IReadOnlyList<WorkspaceFileEntry> entries)
        => $"Listed {entries.Count} entries.";

    public static string Summarize(IReadOnlyList<WorkspaceSearchHit> hits)
        => $"Found {hits.Count} matching lines.";

    public static string Summarize(WorkspaceFileContent content)
    {
        // Report the line window when the read was paged (EndLine set), so the
        // model can see what slice of a large file it received.
        var range = content.EndLine > 0 && content.TotalLines > 0
            ? $" (lines {content.StartLine}-{content.EndLine} of {content.TotalLines})"
            : string.Empty;

        return content.Truncated
            ? $"Read {content.RelativePath}{range} (truncated)."
            : $"Read {content.RelativePath}{range}.";
    }

    public static string Summarize(WorkspaceFileWriteResult result)
        => !result.Applied
            ? $"Skipped writing {result.RelativePath}: {result.Message}"
            : result.OverwroteExisting
                ? $"Updated {result.RelativePath}."
                : $"Created {result.RelativePath}.";

    public static string Summarize(ShellCommandResult result)
        => !result.Executed
            ? result.Message
            : result.ExitCode == 0
                ? result.OutputTruncated
                    ? "PowerShell command completed (output truncated)."
                    : "PowerShell command completed."
                : $"PowerShell exited with code {result.ExitCode}.";

    public static string Describe(IReadOnlyList<WorkspaceFileEntry> entries)
    {
        if (entries.Count == 0)
        {
            return "No files or folders were returned.";
        }

        return string.Join(
            Environment.NewLine,
            entries.Select(entry =>
                entry.IsDirectory
                    ? $"[DIR]  {entry.RelativePath}"
                    : $"[FILE] {entry.RelativePath}{FormatSizeSuffix(entry.SizeBytes)}"));
    }

    public static string Describe(IReadOnlyList<WorkspaceSearchHit> hits)
    {
        if (hits.Count == 0)
        {
            return "No matches were found.";
        }

        return string.Join(
            Environment.NewLine + Environment.NewLine,
            hits.Select(hit => $"{hit.RelativePath}:{hit.LineNumber}{Environment.NewLine}{hit.LineText}"));
    }

    public static string Describe(WorkspaceFileContent content)
    {
        // Render cat -n style line numbers anchored to the actual start line so the
        // numbers line up with search hits and let the model page precisely.
        var builder = new StringBuilder(content.Content.Length + 128);
        var startLine = content.StartLine < 1 ? 1 : content.StartLine;
        var lines = content.Content.Length == 0
            ? []
            : content.Content.Split('\n');

        // A trailing newline yields a final empty element that is not a real line.
        var lineCount = lines.Length;
        if (lineCount > 0 && lines[^1].Length == 0 && content.Content.EndsWith('\n'))
        {
            lineCount--;
        }

        var width = (startLine + Math.Max(lineCount - 1, 0)).ToString().Length;
        for (var index = 0; index < lineCount; index++)
        {
            if (index > 0)
            {
                builder.Append(Environment.NewLine);
            }

            var number = (startLine + index).ToString().PadLeft(width);
            builder.Append(number);
            builder.Append('\t');
            builder.Append(lines[index].TrimEnd('\r'));
        }

        if (content.Truncated)
        {
            builder.Append(Environment.NewLine);
            builder.Append(Environment.NewLine);
            builder.Append(content.TotalLines > 0
                ? $"[truncated — showing lines {startLine}-{content.EndLine} of {content.TotalLines}]"
                : "[truncated]");
        }

        return builder.ToString();
    }

    public static string Describe(WorkspaceFileWriteResult result)
        => string.Join(
            Environment.NewLine,
            $"Path: {result.RelativePath}",
            $"Characters: {result.CharacterCount}",
            $"Result: {result.Message}");

    public static string Describe(ShellCommandResult result)
    {
        var builder = new StringBuilder();
        builder.Append("$ ");
        builder.Append(result.Command.ReplaceLineEndings(Environment.NewLine));

        if (!string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            builder.AppendLine();
            builder.AppendLine();
            builder.Append(result.StandardOutput.TrimEnd());
        }

        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            builder.AppendLine();
            builder.AppendLine();
            builder.AppendLine("[stderr]");
            builder.Append(result.StandardError.TrimEnd());
        }

        if (result.OutputTruncated)
        {
            builder.AppendLine();
            builder.AppendLine();
            builder.Append("[output truncated]");
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatSizeSuffix(long? sizeBytes)
    {
        if (!sizeBytes.HasValue)
        {
            return string.Empty;
        }

        var size = sizeBytes.Value;
        if (size < 1024)
        {
            return $" ({size.ToString(CultureInfo.InvariantCulture)} B)";
        }

        if (size < 1024 * 1024)
        {
            return $" ({(size / 1024d).ToString("0.#", CultureInfo.InvariantCulture)} KB)";
        }

        return $" ({(size / (1024d * 1024d)).ToString("0.#", CultureInfo.InvariantCulture)} MB)";
    }
}
