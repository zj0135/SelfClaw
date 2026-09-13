namespace SelfClaw.Infrastructure.Tools.Workspace;

internal static class WorkspaceFileAccess
{
    internal const long MaxFileBytes = 1_000_000;
    internal static readonly IReadOnlySet<string> SkippedDirectoryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "out", "build", "dist", "node_modules", "packages",
        "__pycache__", "target", "vendor"
    };

    internal static string NormalizeRoot(string workspaceRootPath)
    {
        if (string.IsNullOrWhiteSpace(workspaceRootPath))
        {
            throw new ArgumentException("A workspace root is required.", nameof(workspaceRootPath));
        }

        var root = Path.GetFullPath(workspaceRootPath);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Workspace root '{workspaceRootPath}' was not found.");
        }

        return Path.TrimEndingDirectorySeparator(root);
    }

    internal static string ResolvePath(string root, string relativePath)
    {
        var combined = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!IsPathWithinRoot(root, combined))
        {
            throw new InvalidOperationException("Path traversal outside the workspace root is not allowed.");
        }

        return combined;
    }

    internal static async Task<string> ResolveTextFileAsync(
        string root, string relativePath, string operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = ResolvePath(root, relativePath);
        var file = new FileInfo(fullPath);
        if (!file.Exists) throw new FileNotFoundException("File was not found.", relativePath);
        if (file.Length > MaxFileBytes)
        {
            throw new InvalidOperationException($"The file is too large to {operation} safely. Limit: {MaxFileBytes} bytes.");
        }

        if (!await IsTextFileAsync(fullPath, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(operation == "read" ? "Only text files can be read." : "Only text files can be edited.");
        }

        return fullPath;
    }

    private static bool IsPathWithinRoot(string root, string candidatePath)
    {
        if (string.Equals(root, candidatePath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        return candidatePath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsIgnoredListingEntry(FileSystemInfo info)
    {
        if (info.Attributes.HasFlag(FileAttributes.Hidden))
        {
            return true;
        }

        var name = info.Name;
        if (name.StartsWith('.'))
        {
            return true;
        }

        return info.Attributes.HasFlag(FileAttributes.Directory) && SkippedDirectoryNames.Contains(name);
    }

    internal static IEnumerable<string> EnumerateSearchableFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var directory = stack.Pop();

            // Enumerate child directories and push non-skipped ones.
            IEnumerable<string> subDirectories;
            try
            {
                subDirectories = Directory.EnumerateDirectories(directory);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var subDirectory in subDirectories)
            {
                var dirName = Path.GetFileName(subDirectory);

                // Skip dot-prefixed hidden directories (.git, .vs, .idea, etc.).
                if (dirName.StartsWith('.'))
                {
                    continue;
                }

                // Skip known build/dependency directories.
                if (SkippedDirectoryNames.Contains(dirName))
                {
                    continue;
                }

                // Skip directories marked Hidden by the OS.
                try
                {
                    var attributes = File.GetAttributes(subDirectory);
                    if (attributes.HasFlag(FileAttributes.Hidden))
                    {
                        continue;
                    }
                }
                catch (IOException)
                {
                    continue;
                }

                stack.Push(subDirectory);
            }

            // Enumerate files in the current directory.
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(directory);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }
        }
    }

    private static async Task<bool> IsTextFileAsync(string path, CancellationToken cancellationToken)
    {
        var buffer = new byte[1024];
        await using var stream = File.OpenRead(path);
        var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
        for (var index = 0; index < read; index++)
        {
            if (buffer[index] == 0)
            {
                return false;
            }
        }

        return true;
    }
}
