using System;
using System.Globalization;
using System.IO;

namespace SelfClaw.Infrastructure.ImageGeneration;

/// <summary>
/// Single source of truth for the generated-image layout, mirroring <c>AttachmentPathResolver</c>:
/// the writer, the WebView virtual host and the cleanup pass must agree on it, and the renderer must
/// only ever receive a URL — never a filesystem path.
/// </summary>
public static class GeneratedImagePathResolver
{
    /// <summary>Virtual host the workbench loads generated images from; mapped in <c>MainWindow</c>.</summary>
    public const string HostName = "generated-images.selfclaw.local";

    private const string RootDirectoryName = "generated-images";

    public static string ResolveRoot(string appDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataDirectory);
        return Path.Combine(appDataDirectory, RootDirectoryName);
    }

    /// <summary>Day-bucketed so the retention pass can drop whole folders without stat-ing every file.</summary>
    public static string CreateRelativePath(DateTimeOffset createdAtUtc, Guid id, string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        var day = createdAtUtc.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        return Path.Combine(day, id.ToString("N") + extension);
    }

    public static string CreateSourceUrl(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        var normalized = relativePath
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
        return $"https://{HostName}/{normalized}";
    }

    public static string ResolveFullPath(string root, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        return Path.Combine(root, relativePath);
    }
}
