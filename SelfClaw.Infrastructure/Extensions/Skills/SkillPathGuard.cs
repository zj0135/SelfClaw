namespace SelfClaw.Infrastructure.Extensions.Skills;

/// <summary>
/// Path confinement for Skill-owned files. A Skill package is third-party content, so every read or
/// execution path is normalized back under the Skill root and every segment below that root is checked for
/// reparse points, which would otherwise let a single link widen the reachable file set.
/// </summary>
internal static class SkillPathGuard
{
    private const string ReparsePointMessage = "Skill resources cannot use symbolic links or reparse points.";
    private const string EscapeMessage = "Skill resource path escapes the Skill directory.";

    internal static string ResolveWithinRoot(string skillRoot, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath) || relativePath.Contains(':'))
        {
            throw new InvalidDataException("Skill resource path must be relative.");
        }

        var segments = relativePath.Replace('\\', '/').Split('/');
        if (segments.Any(segment => string.IsNullOrWhiteSpace(segment) || segment is "." or ".."))
        {
            throw new InvalidDataException("Skill resource path contains an unsafe segment.");
        }

        var root = Path.GetFullPath(skillRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(root, Path.Combine(segments)));
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(EscapeMessage);
        }

        return candidate;
    }

    /// <summary>
    /// The root is picked by the user or the installer, never by the package, and a user Skill is often a
    /// junction into a working copy, so the root's own attributes say nothing about what the package can
    /// reach. Only the segments below it are package-controlled, and those must be link-free.
    /// </summary>
    internal static void EnsureNoReparsePoints(string skillRoot, string filePath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(skillRoot));
        var current = Path.GetFullPath(filePath);
        while (current.Length > root.Length)
        {
            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException(ReparsePointMessage);
            }

            current = Path.GetDirectoryName(current)
                ?? throw new InvalidDataException(EscapeMessage);
        }

        // Walking up must land on the root itself; anything else was never under the root to begin with.
        if (!current.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(EscapeMessage);
        }
    }
}
