using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services.WebView;
using SelfClaw.Infrastructure.Extensions;
using SelfClaw.Infrastructure.Extensions.Abstractions;
using SelfClaw.Infrastructure.Extensions.Plugins;

namespace SelfClaw.Desktop.Services.Plugins;

/// <summary>
/// Owns the host side of the right-hand plugin panels: which panels are open, the origin each one is
/// served from, the version directory its files are pinned to, and the security headers its responses
/// carry. The shell renders the tabs; everything that grants a capability lives here.
/// </summary>
internal sealed class PluginPanelHostController : IPluginPanelSessionRegistry, IDisposable
{
    private const string TabsSettingsNode = "pluginPanels";
    private const string MessagePrefix = "plugin-host/";
    private const string ResourceFilter = "https://*" + PluginPanelOrigin.HostSuffix + "/*";
    private const int MaximumOpenPanels = 8;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly ExtensionCatalog _catalog;
    private readonly IExtensionPackageRepository _packageRepository;
    private readonly IPluginVersionLeaseManager _versionLeaseManager;
    private readonly DesktopSettingsJsonStore _settingsStore;
    private readonly WebViewHostChannel _hostChannel;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, OpenPlugin> _openPlugins = new(StringComparer.OrdinalIgnoreCase);
    private CoreWebView2? _webView;

    public PluginPanelHostController(
        ExtensionCatalog catalog,
        IExtensionPackageRepository packageRepository,
        IPluginVersionLeaseManager versionLeaseManager,
        DesktopSettingsJsonStore settingsStore,
        WebViewHostChannel hostChannel,
        Dispatcher dispatcher)
    {
        _catalog = catalog;
        _packageRepository = packageRepository;
        _versionLeaseManager = versionLeaseManager;
        _settingsStore = settingsStore;
        _hostChannel = hostChannel;
        _dispatcher = dispatcher;
    }

    /// <summary>
    /// Raised after a panel has been opened. A panel that just appeared has no event history to replay,
    /// so this is what prompts an unconditional context push in its direction.
    /// </summary>
    public event Action? PanelOpened;

    public void Attach(CoreWebView2 webView)
    {
        ArgumentNullException.ThrowIfNull(webView);
        _webView = webView;
        webView.AddWebResourceRequestedFilter(ResourceFilter, CoreWebView2WebResourceContext.All);
        webView.WebResourceRequested += OnWebResourceRequested;
    }

    public async Task<object?> TryHandleAsync(
        string type,
        JsonElement payload,
        CancellationToken cancellationToken = default)
    {
        if (!type.StartsWith(MessagePrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var requestId = ReadString(payload, "requestId");
        try
        {
            return type switch
            {
                "plugin-host/get-panels" => new
                {
                    type,
                    requestId,
                    panels = await ListAvailablePanelsAsync(cancellationToken),
                    tabs = await ReadPersistedTabsAsync(cancellationToken)
                },
                "plugin-host/open" => await OpenAsync(type, requestId, payload, cancellationToken),
                "plugin-host/close" => CloseTab(type, requestId, payload),
                "plugin-host/save-tabs" => await SaveTabsAsync(type, requestId, payload, cancellationToken),
                _ => new { type, requestId, error = $"Unsupported plugin panel message type '{type}'." }
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new { type, requestId, error = exception.Message };
        }
    }

    /// <summary>
    /// Called by the extension settings module before it drains a Plugin's version directories. The host
    /// tears its own state down immediately rather than waiting for the shell to acknowledge: once the
    /// mapping and the lease are gone the panel can no longer fetch anything, so a slow or wedged frame
    /// cannot hold a settings mutation open.
    /// </summary>
    public Task CloseAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return Task.CompletedTask;
        }

        if (_dispatcher.CheckAccess())
        {
            EvictPlugin(pluginId);
            return Task.CompletedTask;
        }

        return _dispatcher.InvokeAsync(() => EvictPlugin(pluginId)).Task;
    }

    /// <summary>
    /// The permissions a panel actually holds, or null when it is not open. Resolved from host state
    /// rather than from the caller's payload so that a capability check never depends on what the shell
    /// chose to relay.
    /// </summary>
    public IReadOnlyList<string>? GetPermissions(string? panelKey)
    {
        if (string.IsNullOrWhiteSpace(panelKey))
        {
            return null;
        }

        return _openPlugins.TryGetValue(panelKey.Split('/')[0], out var open) && open.PanelKeys.Contains(panelKey)
            ? open.Permissions
            : null;
    }

    public void Dispose()
    {
        if (_webView is not null)
        {
            _webView.WebResourceRequested -= OnWebResourceRequested;
        }

        foreach (var pluginId in _openPlugins.Keys.ToArray())
        {
            ReleasePlugin(pluginId);
        }

        _webView = null;
    }

    private void EvictPlugin(string pluginId)
    {
        if (!_openPlugins.ContainsKey(pluginId))
        {
            return;
        }

        ReleasePlugin(pluginId);
        _hostChannel.PostPush(new { type = "plugin-host/evict", pluginId });
    }

    private void ReleasePlugin(string pluginId)
    {
        if (!_openPlugins.Remove(pluginId, out var open))
        {
            return;
        }

        try
        {
            _webView?.ClearVirtualHostNameToFolderMapping(open.HostName);
        }
        catch (InvalidOperationException)
        {
            // The WebView is already torn down; the lease still has to be released.
        }

        // PluginVersionLease releases synchronously and hands back an already-completed ValueTask, so
        // there is nothing to await here.
        _ = open.Lease.DisposeAsync();
    }

    private async Task<object> OpenAsync(
        string type,
        string? requestId,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var key = ReadString(payload, "panelKey")
            ?? throw new ArgumentException("panelKey is required.");
        var panel = (await ListAvailablePanelsAsync(cancellationToken))
            .FirstOrDefault(candidate => string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Panel '{key}' is not available.");
        if (!_openPlugins.TryGetValue(panel.PluginId, out var open))
        {
            if (_openPlugins.Count >= MaximumOpenPanels)
            {
                throw new InvalidOperationException($"At most {MaximumOpenPanels} plugins can be open at once.");
            }

            open = await AcquirePluginAsync(panel, cancellationToken);
            _openPlugins.Add(panel.PluginId, open);
        }

        open.PanelKeys.Add(panel.Key);
        PanelOpened?.Invoke();
        return new
        {
            type,
            requestId,
            ok = true,
            panel,
            url = $"{panel.Url}?__selfclaw_panel={Uri.EscapeDataString(panel.Key)}"
        };
    }

    private async Task<OpenPlugin> AcquirePluginAsync(PluginPanelView panel, CancellationToken cancellationToken)
    {
        var package = await _packageRepository.GetPackageAsync(ExtensionKind.Plugin, panel.PluginId, cancellationToken)
            ?? throw new KeyNotFoundException($"Plugin '{panel.PluginId}' is not installed.");
        // The lease is what keeps this exact version directory on disk while the tab is open, so an
        // update that lands mid-session cannot swap files out from under a running panel.
        var lease = _versionLeaseManager.Acquire(package.InstallPath);
        try
        {
            var hostName = $"{panel.PluginId}{PluginPanelOrigin.HostSuffix}";
            _webView?.SetVirtualHostNameToFolderMapping(
                hostName,
                package.InstallPath,
                CoreWebView2HostResourceAccessKind.DenyCors);
            return new OpenPlugin(
                panel.PluginId,
                hostName,
                Path.GetFullPath(package.InstallPath),
                lease,
                BuildContentSecurityPolicy(panel.NetworkOrigins),
                panel.Permissions);
        }
        catch
        {
            _ = lease.DisposeAsync();
            throw;
        }
    }

    private object CloseTab(string type, string? requestId, JsonElement payload)
    {
        var key = ReadString(payload, "panelKey") ?? throw new ArgumentException("panelKey is required.");
        var pluginId = key.Split('/')[0];
        if (_openPlugins.TryGetValue(pluginId, out var open) &&
            open.PanelKeys.Remove(key) &&
            open.PanelKeys.Count == 0)
        {
            ReleasePlugin(pluginId);
        }

        return new { type, requestId, ok = true };
    }

    private async Task<object> SaveTabsAsync(
        string type,
        string? requestId,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var tabs = payload.TryGetProperty("tabs", out var tabsElement) && tabsElement.ValueKind == JsonValueKind.Array
            ? tabsElement.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!)
                .Take(MaximumOpenPanels)
                .ToArray()
            : [];
        await _settingsStore.WriteNodeAsync(
            TabsSettingsNode,
            new PersistedTabs(tabs, ReadString(payload, "activeKey")),
            JsonOptions,
            cancellationToken);
        return new { type, requestId, ok = true };
    }

    private async Task<IReadOnlyList<string>> ReadPersistedTabsAsync(CancellationToken cancellationToken)
    {
        var persisted = await _settingsStore.ReadNodeAsync<PersistedTabs>(
            TabsSettingsNode,
            JsonOptions,
            cancellationToken);
        return persisted?.Tabs ?? [];
    }

    // Only panels whose Plugin is enabled and whose permissions were acknowledged are offered. A panel
    // that is merely installed must not be reachable, otherwise enabling would stop being the moment the
    // user grants a Plugin its capabilities.
    private async Task<IReadOnlyList<PluginPanelView>> ListAvailablePanelsAsync(CancellationToken cancellationToken)
        => (await _catalog.ListPluginPanelViewsAsync(cancellationToken))
            .Where(panel => panel.Enabled && panel.Status == ExtensionStatus.Ready)
            .ToArray();

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var environment = _webView?.Environment;
        if (environment is null)
        {
            return;
        }

        if (!TryResolveAsset(e.Request.Uri, out var filePath, out var open))
        {
            e.Response = environment.CreateWebResourceResponse(
                null,
                404,
                "Not Found",
                "Content-Type: text/plain");
            return;
        }

        var bytes = File.ReadAllBytes(filePath);
        e.Response = environment.CreateWebResourceResponse(
            new MemoryStream(bytes),
            200,
            "OK",
            string.Join(
                "\r\n",
                $"Content-Type: {ResolveContentType(filePath)}",
                $"Content-Security-Policy: {open.ContentSecurityPolicy}",
                "X-Content-Type-Options: nosniff",
                "Cache-Control: no-cache"));
    }

    private bool TryResolveAsset(string requestUri, out string filePath, out OpenPlugin open)
    {
        filePath = string.Empty;
        open = null!;
        if (!Uri.TryCreate(requestUri, UriKind.Absolute, out var uri) ||
            !uri.Host.EndsWith(PluginPanelOrigin.HostSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var pluginId = uri.Host[..^PluginPanelOrigin.HostSuffix.Length];
        // A closed panel's origin must stop resolving even if the virtual host mapping outlives it,
        // otherwise a disabled Plugin could keep serving from a directory that is about to be deleted.
        if (!_openPlugins.TryGetValue(pluginId, out var candidate))
        {
            return false;
        }

        var relativePath = Uri.UnescapeDataString(uri.AbsolutePath);
        if (!TryResolvePackageAsset(candidate.RootPath, relativePath, out filePath))
        {
            return false;
        }

        open = candidate;
        return true;
    }

    /// <summary>
    /// Resolves a request path against the version directory a panel is pinned to. Kept separate so the
    /// containment rule can be tested directly: everything a panel is allowed to read is decided here.
    /// </summary>
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

    // A panel that declares no network permission gets connect-src 'self', which means it cannot reach
    // anything off the local package. frame-ancestors keeps the panel from being embedded anywhere but
    // the shell, so its postMessage parent is always the shell.
    private static string BuildContentSecurityPolicy(IReadOnlyList<string> networkOrigins)
    {
        var connect = networkOrigins.Count == 0
            ? "'self'"
            : $"'self' {string.Join(' ', networkOrigins)}";
        return string.Join(
            " ",
            "default-src 'self';",
            "script-src 'self' 'unsafe-inline';",
            "style-src 'self' 'unsafe-inline';",
            "img-src 'self' data: blob:;",
            "font-src 'self' data:;",
            $"connect-src {connect};",
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

    private static string? ReadString(JsonElement payload, string propertyName)
    {
        if (!payload.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = element.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private sealed record PersistedTabs(IReadOnlyList<string> Tabs, string? ActiveKey);

    private sealed class OpenPlugin(
        string pluginId,
        string hostName,
        string rootPath,
        PluginVersionLease lease,
        string contentSecurityPolicy,
        IReadOnlyList<string> permissions)
    {
        public string PluginId { get; } = pluginId;
        public string HostName { get; } = hostName;
        public string RootPath { get; } = rootPath;
        public PluginVersionLease Lease { get; } = lease;
        public string ContentSecurityPolicy { get; } = contentSecurityPolicy;
        public IReadOnlyList<string> Permissions { get; } = permissions;
        public HashSet<string> PanelKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
