namespace SelfClaw.Infrastructure.Extensions.Processes;

internal static class PluginCommandTemplate
{
    internal static void Validate(string packageRoot, string value, string fieldName)
    {
        ArgumentNullException.ThrowIfNull(value);
        var remainder = value
            .Replace("${pluginRoot}", string.Empty, StringComparison.Ordinal)
            .Replace("${workspaceRoot}", string.Empty, StringComparison.Ordinal);
        if (remainder.Contains("${", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{fieldName} contains an unsupported template variable.");
        }

        // The DLL ban applies to every value, not just ${pluginRoot}-prefixed ones: a bare relative
        // "server/entry.dll" is the same declaration with the prefix omitted.
        if (string.Equals(Path.GetExtension(value.TrimEnd()), ".dll", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Plugin DLL entry points are not supported.");
        }

        // Existence is only checkable for package-relative values; a bare command such as "node" is
        // resolved from PATH at launch and must stay legal.
        if (value.Contains("${pluginRoot}", StringComparison.Ordinal))
        {
            var relative = value.Replace("${pluginRoot}", string.Empty, StringComparison.Ordinal)
                .TrimStart('/', '\\');
            var path = ResolvePackagePath(packageRoot, relative, fieldName);
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                throw new InvalidDataException($"{fieldName} references a missing package entry.");
            }
        }
    }

    internal static string? TryExpand(string value, string? workspaceRoot, string? pluginRoot)
    {
        var expanded = value;
        if (expanded.Contains("${pluginRoot}", StringComparison.Ordinal))
        {
            if (pluginRoot is null)
            {
                return null;
            }

            expanded = expanded.Replace("${pluginRoot}", pluginRoot, StringComparison.Ordinal);
        }

        if (expanded.Contains("${workspaceRoot}", StringComparison.Ordinal))
        {
            if (workspaceRoot is null)
            {
                return null;
            }

            expanded = expanded.Replace("${workspaceRoot}", workspaceRoot, StringComparison.Ordinal);
        }

        return expanded.Contains("${", StringComparison.Ordinal) ? null : expanded;
    }

    /// <summary>
    /// A hook command must be resolvable without the host's process directory: a bare PATH name, an
    /// absolute path, or a package-relative path introduced by <c>${pluginRoot}</c>. A relative path
    /// with a separator would otherwise be resolved by <c>Process.Start</c> against the host process
    /// directory rather than the package, launching an unrelated executable.
    /// </summary>
    internal static void ValidateCommand(string packageRoot, string value, string fieldName)
    {
        Validate(packageRoot, value, fieldName);
        if (string.IsNullOrWhiteSpace(value) ||
            value.Contains("${pluginRoot}", StringComparison.Ordinal) ||
            Path.IsPathFullyQualified(value))
        {
            return;
        }

        // A bare PATH-resolved name carries neither a directory separator nor a drive colon.
        // Everything else — including "C:guard.exe" (relative to the current directory on drive C:)
        // and "\tools\guard.exe" (relative to the host's current drive) — is resolved against the
        // host process, which is exactly what this rule prevents.
        if (value.IndexOf('/') < 0 && value.IndexOf('\\') < 0 && value.IndexOf(':') < 0)
        {
            return;
        }

        throw new InvalidDataException(
            $"{fieldName} must be a bare executable name, an absolute path, or a ${{pluginRoot}} path.");
    }

    internal static string ResolvePackagePath(string packageRoot, string relativePath, string fieldName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException($"{fieldName} must be package-relative.");
        }

        var root = Path.GetFullPath(packageRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{fieldName} escapes the package root.");
        }

        return candidate;
    }
}
