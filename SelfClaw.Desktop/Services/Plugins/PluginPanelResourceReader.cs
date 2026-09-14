using System.IO;
using Microsoft.Extensions.Logging;
using SelfClaw.Desktop.Services.WebView;
using SelfClaw.Desktop.Services.Plugins.Models;

namespace SelfClaw.Desktop.Services.Plugins;

internal sealed class PluginPanelResourceReader
{
    internal const int MaximumResourceBytes = 8 * 1024 * 1024;
    private readonly SemaphoreSlim _reads = new(4, 4);
    private readonly ILogger<PluginPanelResourceReader> _logger;

    public PluginPanelResourceReader(ILogger<PluginPanelResourceReader> logger) => _logger = logger;

    public async Task<PluginPanelResource> ReadAsync(string rootPath, string requestPath, string contentSecurityPolicy,
        CancellationToken cancellationToken = default)
    {
        if (!await _reads.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return Failure(503, "Busy");
        try
        {
            return await Task.Run(() => ReadCoreAsync(rootPath, requestPath, contentSecurityPolicy, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (FileNotFoundException) { return Failure(404, "Not Found"); }
        catch (DirectoryNotFoundException) { return Failure(404, "Not Found"); }
        catch (UnauthorizedAccessException exception)
        {
            _logger.LogWarning(exception, "Plugin resource access was denied.");
            return Failure(403, "Forbidden");
        }
        catch (IOException exception)
        {
            _logger.LogWarning(exception, "Plugin resource could not be read.");
            return Failure(500, "Read Failed");
        }
        finally { _reads.Release(); }
    }

    private static async Task<PluginPanelResource> ReadCoreAsync(string rootPath, string requestPath, string policy, CancellationToken cancellationToken)
    {
        if (!TryResolvePackageAsset(rootPath, requestPath, out var path)) return Failure(404, "Not Found");
        if ((File.GetAttributes(rootPath) & FileAttributes.ReparsePoint) != 0) return Failure(403, "Forbidden");
        var current = rootPath;
        foreach (var segment in Path.GetRelativePath(rootPath, path).Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return Failure(403, "Forbidden");
        }
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous);
        if (stream.Length > MaximumResourceBytes) return Failure(413, "Too Large");
        using var content = new MemoryStream((int)stream.Length);
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (content.Length + read > MaximumResourceBytes) return Failure(413, "Too Large");
            content.Write(buffer, 0, read);
        }
        return new(200, "OK", string.Join("\r\n", $"Content-Type: {ResolveContentType(path)}",
            $"Content-Security-Policy: {policy}", "X-Content-Type-Options: nosniff", "Cache-Control: no-cache"), content.ToArray());
    }

    internal static PluginPanelResource Failure(int status, string reason)
        => new(status, reason, "Content-Type: text/plain\r\nX-Content-Type-Options: nosniff", []);
    internal static bool TryResolvePackageAsset(string rootPath, string requestPath, out string filePath)
    {
        filePath = string.Empty;
        var relativePath = requestPath.TrimStart('/');
        if (relativePath.Length == 0 ||
            Path.IsPathRooted(relativePath) ||
            relativePath.Split('/', '\\').Any(segment => segment is ".." or "."))
        {
            return false;
        }

        var root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string resolved;
        try
        {
            resolved = Path.GetFullPath(Path.Combine(root, relativePath));
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (!resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(resolved))
        {
            return false;
        }

        filePath = resolved;
        return true;
    }


    internal static string BuildContentSecurityPolicy(IReadOnlyList<string> networkOrigins)
    {
        // The approved origins are the same for image loads and script requests: reusing one list keeps
        // "what the manifest approved" == "what the CSP sends through" on every directive that splits.
        var approved = networkOrigins.Count == 0
            ? "'self'"
            : $"'self' {string.Join(' ', networkOrigins)}";
        return string.Join(
            " ",
            "default-src 'self';",
            "script-src 'self' 'unsafe-inline';",
            "style-src 'self' 'unsafe-inline';",
            $"img-src data: blob: {approved};",
            "font-src 'self' data:;",
            $"connect-src {approved};",
            $"frame-ancestors https://{WebViewMessageRouter.ApplicationHostName};",
            "frame-src 'none';",
            "object-src 'none';",
            "base-uri 'none';",
            "form-action 'none'");
    }

    private static string ResolveContentType(string filePath)
        => Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".html" or ".htm" => "text/html; charset=utf-8",
            ".js" or ".mjs" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".json" or ".map" => "application/json; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".ico" => "image/x-icon",
            ".woff2" => "font/woff2",
            ".woff" => "font/woff",
            ".wasm" => "application/wasm",
            ".txt" or ".md" => "text/plain; charset=utf-8",
            _ => "application/octet-stream"
        };


}
