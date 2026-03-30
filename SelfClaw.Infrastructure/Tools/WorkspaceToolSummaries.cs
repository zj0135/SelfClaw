using SelfClaw.Core.Models;

namespace SelfClaw.Infrastructure.Tools;

internal static class WorkspaceToolSummaries
{
    public static string Summarize(IReadOnlyList<WorkspaceFileEntry> entries)
        => $"Listed {entries.Count} entries.";

    public static string Summarize(IReadOnlyList<WorkspaceSearchHit> hits)
        => $"Found {hits.Count} matching lines.";

    public static string Summarize(WorkspaceFileContent content)
        => content.Truncated
            ? $"Read {content.RelativePath} (truncated)."
            : $"Read {content.RelativePath}.";

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
}
