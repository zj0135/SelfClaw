namespace SelfClaw.Infrastructure.Extensions.Skills;

/// <summary>
/// Path confinement for Skill-owned files. A Skill package is third-party content, so every read or
/// execution path is normalized back under the Skill root and every intermediate segment is checked for
/// reparse points, which would otherwise let a single link widen the reachable file set.
/// </summary>
internal static class SkillPathGuard
{
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
            throw new InvalidDataException("Skill resource path escapes the Skill directory.");
        }

        return candidate;
    }

    internal static void EnsureNoReparsePoints(string skillRoot, string filePath)
    {
        var root = Path.GetFullPath(skillRoot);
        var current = Path.GetFullPath(filePath);
        while (current.Length >= root.Length)
        {
            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException("Skill resources cannot use symbolic links or reparse points.");
            }

            if (current.Equals(root, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            current = Path.GetDirectoryName(current)
                ?? throw new InvalidDataException("Skill resource path escapes the Skill directory.");
        }
    }
}
