namespace SelfClaw.Infrastructure.Processes;

/// <summary>
/// Locates a bare command name on PATH by trying each PATHEXT extension, mirroring how the OS shell
/// resolves a name. Both the CLI adapters and the Skill script runner resolve through this one
/// primitive: on Windows an npm-style <c>.cmd</c> shim usually sits next to a POSIX shim, and probing
/// the extensionless file first would hand <c>CreateProcess</c> a script it cannot run.
/// </summary>
internal static class ExecutableResolver
{
    internal const string DefaultWindowsPathExtensions = ".COM;.EXE;.BAT;.CMD";

    internal static string? ResolvePath(
        string command,
        string path,
        string pathExt,
        bool isWindows,
        Func<string, bool>? fileExists = null)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        fileExists ??= File.Exists;
        var hasDirectory = command.IndexOf('/') >= 0 || command.IndexOf('\\') >= 0;
        if (hasDirectory)
        {
            return ProbeWithExtensions(Path.GetFullPath(command), pathExt, isWindows, fileExists);
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = directory.Trim().Trim('"');
            if (trimmed.Length == 0)
            {
                continue;
            }

            string candidate;
            try
            {
                candidate = Path.Combine(trimmed, command);
            }
            catch (ArgumentException)
            {
                // Skip PATH entries containing invalid path characters.
                continue;
            }

            var hit = ProbeWithExtensions(candidate, pathExt, isWindows, fileExists);
            if (hit is not null)
            {
                return hit;
            }
        }

        return null;
    }

    /// <summary>
    /// Probes a candidate path for a launchable file. A candidate that already carries a recognised
    /// executable extension is probed as-is. A bare name (or one with an unrecognised extension)
    /// resolves through PATHEXT <em>first</em> — the shell never launches an extensionless file, so we
    /// must not either. The as-is probe is kept only as a last resort for an extensionless path given
    /// explicitly.
    /// </summary>
    private static string? ProbeWithExtensions(
        string candidate,
        string pathExt,
        bool isWindows,
        Func<string, bool> fileExists)
    {
        if (!isWindows)
        {
            return fileExists(candidate) ? candidate : null;
        }

        var existingExtension = Path.GetExtension(candidate);
        if (existingExtension.Length > 0 && PathExtContains(pathExt, existingExtension))
        {
            return fileExists(candidate) ? candidate : null;
        }

        foreach (var extension in pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var normalized = extension.Trim();
            if (normalized.Length == 0)
            {
                continue;
            }

            if (!normalized.StartsWith('.'))
            {
                normalized = "." + normalized;
            }

            var withExtension = candidate + normalized;
            if (fileExists(withExtension))
            {
                return withExtension;
            }
        }

        return fileExists(candidate) ? candidate : null;
    }

    private static bool PathExtContains(string pathExt, string extension)
        => pathExt
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Any(candidate => string.Equals(candidate.Trim(), extension, StringComparison.OrdinalIgnoreCase));
}
