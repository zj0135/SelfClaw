using SelfClaw.Infrastructure.Attachments;

namespace SelfClaw.Infrastructure.Tools.Workspace;

internal static class WorkspaceFileAccess
{
    internal const long MaxTextFileBytes = 1_000_000;

    /// <summary>How many leading bytes every "what kind of file is this" probe reads.</summary>
    internal const int HeaderProbeBytes = 1024;

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
        if (file.Length > MaxTextFileBytes)
        {
            throw new InvalidOperationException($"The file is too large to {operation} safely. Limit: {MaxTextFileBytes} bytes.");
        }

        // An image is not a corrupt text file: naming the real format explains why the operation
        // cannot touch it, while the plain text rejection below still covers every other binary.
        var header = await ReadHeaderAsync(fullPath, HeaderProbeBytes, cancellationToken).ConfigureAwait(false);
        if (ImageSignatures.TryDetect(header, out var imageMediaType))
        {
            throw new InvalidOperationException(
                $"'{relativePath}' is an image ({imageMediaType}), not a text file.");
        }

        if (!await IsTextFileAsync(fullPath, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(operation == "read" ? "Only text files can be read." : "Only text files can be edited.");
        }

        return fullPath;
    }

    /// <summary>
    /// Reads at most <paramref name="maximumBytes"/> from the start of a file (less when the file is
    /// shorter) so callers can sniff signatures without loading a whole image or archive.
    /// </summary>
    internal static async Task<byte[]> ReadHeaderAsync(
        string fullPath, int maximumBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var buffer = new byte[maximumBytes];
        await using var stream = File.OpenRead(fullPath);
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total == buffer.Length ? buffer : buffer[..total];
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

    private static async Task<bool> IsTextFileAsync(string path, CancellationToken cancellationToken)
    {
        var header = await ReadHeaderAsync(path, HeaderProbeBytes, cancellationToken).ConfigureAwait(false);
        return !header.Contains((byte)0);
    }
}
