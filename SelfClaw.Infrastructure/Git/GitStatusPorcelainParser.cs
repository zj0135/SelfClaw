using System.Globalization;
using SelfClaw.Core.Models;

namespace SelfClaw.Infrastructure.Git;

internal static class GitStatusPorcelainParser
{
    internal sealed record GitStatusEntry(GitChangeScope Scope, string Path, string? OldPath, GitChangeKind Kind);

    /// <summary>
    /// Parses `git status --porcelain=v2 -z` output. Paths arrive relative to the repository root;
    /// <paramref name="prefix"/> (from `rev-parse --show-prefix`) is stripped so every path is
    /// workspace-relative, and entries outside the workspace subtree are dropped.
    /// </summary>
    internal static IReadOnlyList<GitStatusEntry> ParseStatus(string output, string prefix)
    {
        var entries = new List<GitStatusEntry>();
        var tokens = output.Split('\0');
        for (var index = 0; index < tokens.Length; index++)
        {
            var token = tokens[index];
            if (token.Length == 0)
            {
                continue;
            }

            switch (token[0])
            {
                case '1':
                    if (TryReadPath(token, 9, out var ordinaryFields, out var ordinaryPath))
                    {
                        AddTrackedEntries(entries, ordinaryFields[1], StripPrefix(ordinaryPath, prefix), null);
                    }

                    break;
                case '2':
                    if (!TryReadPath(token, 10, out var renameFields, out var renamePath) || index + 1 >= tokens.Length)
                    {
                        break;
                    }

                    // The original path is its own NUL-separated field right after the record.
                    var originalPath = StripPrefix(tokens[++index], prefix);
                    AddTrackedEntries(entries, renameFields[1], StripPrefix(renamePath, prefix), originalPath);
                    break;
                case 'u':
                    if (TryReadPath(token, 11, out _, out var conflictedPath) &&
                        StripPrefix(conflictedPath, prefix) is { } conflictedRelative)
                    {
                        entries.Add(new GitStatusEntry(GitChangeScope.Unstaged, conflictedRelative, null, GitChangeKind.Conflicted));
                        entries.Add(new GitStatusEntry(GitChangeScope.Staged, conflictedRelative, null, GitChangeKind.Conflicted));
                    }

                    break;
                case '?':
                    if (token.Length > 2 && token[1] == ' ' && StripPrefix(token[2..], prefix) is { } untrackedRelative)
                    {
                        entries.Add(new GitStatusEntry(GitChangeScope.Unstaged, untrackedRelative, null, GitChangeKind.Untracked));
                    }

                    break;
            }
        }

        return entries;
    }

    /// <summary>
    /// Parses `git diff --numstat -z` output. Rename/copy rows end at the count tab with the old and
    /// new paths as the two following NUL fields; binary rows report "-" for both counts.
    /// </summary>
    internal static IReadOnlyDictionary<string, (int? Added, int? Removed)> ParseNumstat(string output)
    {
        var counts = new Dictionary<string, (int? Added, int? Removed)>(StringComparer.Ordinal);
        var tokens = output.Split('\0');
        for (var index = 0; index < tokens.Length; index++)
        {
            var token = tokens[index];
            if (token.Length == 0)
            {
                continue;
            }

            var firstTab = token.IndexOf('\t');
            var secondTab = firstTab < 0 ? -1 : token.IndexOf('\t', firstTab + 1);
            if (secondTab < 0)
            {
                continue;
            }

            var added = ParseCount(token.AsSpan(0, firstTab));
            var removed = ParseCount(token.AsSpan(firstTab + 1, secondTab - firstTab - 1));
            var path = token[(secondTab + 1)..];
            if (path.Length == 0)
            {
                if (index + 2 >= tokens.Length)
                {
                    break;
                }

                path = tokens[index + 2];
                index += 2;
            }

            if (path.Length > 0)
            {
                counts[path] = (added, removed);
            }
        }

        return counts;
    }

    private static void AddTrackedEntries(List<GitStatusEntry> entries, string xy, string? path, string? oldPath)
    {
        if (path is null || xy.Length < 2)
        {
            return;
        }

        if (KindFromLetter(xy[0]) is { } indexKind)
        {
            entries.Add(new GitStatusEntry(GitChangeScope.Staged, path, oldPath, indexKind));
        }

        if (KindFromLetter(xy[1]) is { } worktreeKind)
        {
            // A worktree-side change is a content diff against the index, so it carries no old path
            // even when the index side is a rename.
            entries.Add(new GitStatusEntry(GitChangeScope.Unstaged, path, null, worktreeKind));
        }
    }

    private static bool TryReadPath(string token, int fieldCount, out string[] fields, out string path)
    {
        fields = token.Split(' ', fieldCount);
        if (fields.Length < fieldCount)
        {
            path = string.Empty;
            return false;
        }

        path = fields[^1];
        return path.Length > 0;
    }

    private static string? StripPrefix(string repoRelativePath, string prefix)
    {
        if (prefix.Length == 0)
        {
            return repoRelativePath.Length == 0 ? null : repoRelativePath;
        }

        return repoRelativePath.StartsWith(prefix, StringComparison.Ordinal) && repoRelativePath.Length > prefix.Length
            ? repoRelativePath[prefix.Length..]
            : null;
    }

    private static GitChangeKind? KindFromLetter(char letter) => letter switch
    {
        'M' or 'T' => GitChangeKind.Modified,
        'A' => GitChangeKind.Added,
        'D' => GitChangeKind.Deleted,
        'R' => GitChangeKind.Renamed,
        'C' => GitChangeKind.Copied,
        _ => null
    };

    private static int? ParseCount(ReadOnlySpan<char> value)
        => value is "-" ? null
            : int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ? count : null;
}
