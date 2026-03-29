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
}