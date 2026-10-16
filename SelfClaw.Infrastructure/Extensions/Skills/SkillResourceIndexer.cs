using SelfClaw.Infrastructure.Extensions.Skills.Models;

namespace SelfClaw.Infrastructure.Extensions.Skills;

/// <summary>
/// Builds the inventory a Skill exposes to the model. Without it the model can only guess file names
/// from the SKILL.md prose, so resources that are not spelled out in the body are effectively invisible.
/// Enumeration is bounded in depth and entry count and never follows reparse points: a Skill package is
/// third-party content, so one link must not widen the reachable file set.
/// </summary>
internal static class SkillResourceIndexer
{
    internal const int MaximumDepth = 3;
    internal const int MaximumEntries = 200;

    private static readonly HashSet<string> ResourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".txt", ".json", ".jsonl", ".yaml", ".yml", ".toml", ".ini", ".xml", ".csv", ".tsv"
    };

    internal static readonly HashSet<string> ScriptExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ps1", ".psm1", ".sh", ".py", ".js", ".mjs", ".cjs", ".cs", ".csx", ".cmd", ".bat"
    };

    private static readonly HashSet<string> SkippedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "__pycache__"
    };

    /// <summary>
    /// Classifies a file extension, or returns <see langword="null"/> when the file is neither a readable
    /// resource nor an executable script. Binary file types are intentionally absent from both tables.
    /// </summary>
    internal static SkillResourceKind? Classify(string extension)
    {
        if (ResourceExtensions.Contains(extension))
        {
            return SkillResourceKind.Resource;
        }

        return ScriptExtensions.Contains(extension) ? SkillResourceKind.Script : null;
    }

    internal static Task<SkillResourceIndex> IndexAsync(string skillRoot, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillRoot);
        cancellationToken.ThrowIfCancellationRequested();
        var root = Path.GetFullPath(skillRoot);
        var entries = new List<SkillResourceEntry>();
        var truncated = false;
        if (Directory.Exists(root))
        {
            Walk(root, entries, ref truncated);
        }

        return Task.FromResult(new SkillResourceIndex(
            entries.OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray(),
            truncated));
    }

    private static void Walk(string root, List<SkillResourceEntry> entries, ref bool truncated)
    {
        // Iterative rather than recursive: depth is capped, but a deep tree must not grow the call stack.
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 1));
        while (pending.Count > 0)
        {
            var (directory, depth) = pending.Pop();
            FileSystemInfo[] children;
            try
            {
                children = new DirectoryInfo(directory).GetFileSystemInfos();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // An unreadable subdirectory shrinks the inventory; it never fails the whole Skill.
                continue;
            }

            foreach (var child in children)
            {
                if (child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                if (child is DirectoryInfo childDirectory)
                {
                    if (depth < MaximumDepth && !SkippedDirectoryNames.Contains(childDirectory.Name))
                    {
                        pending.Push((childDirectory.FullName, depth + 1));
                    }

                    continue;
                }

                var kind = Classify(child.Extension);
                if (kind is null)
                {
                    continue;
                }

                if (entries.Count >= MaximumEntries)
                {
                    truncated = true;
                    return;
                }

                entries.Add(new SkillResourceEntry(
                    Path.GetRelativePath(root, child.FullName).Replace('\\', '/'),
                    kind.Value,
                    ((FileInfo)child).Length));
            }
        }
    }
}
