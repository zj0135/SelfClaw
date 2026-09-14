using SelfClaw.Desktop.Services.Settings;
using SelfClaw.Core.Runtime;
using System.IO;
using Microsoft.Extensions.Logging;
using SelfClaw.Desktop.Services.Plugins.Models;
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

    private readonly IPluginPanelCatalog _catalog;
    private readonly IExtensionPackageRepository _packageRepository;
    private readonly IPluginVersionLeaseManager _versionLeaseManager;
    private readonly DesktopSettingsJsonStore _settingsStore;
    private readonly WebViewHostChannel _hostChannel;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, OpenPluginPanelSession> _openPlugins = new(StringComparer.OrdinalIgnoreCase);
    private CoreWebView2? _webView;
    private readonly PluginPanelResourceReader _resources;
    private readonly ILogger<PluginPanelHostController> _logger;
    private readonly SemaphoreSlim _openGate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly Dictionary<string, long> _pluginGenerations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _panelGenerations = new(StringComparer.OrdinalIgnoreCase);
    private bool _stopping;
    private bool _disposed;
    private int _resourceCount;
    private TaskCompletionSource? _resourcesDrained;


    public PluginPanelHostController(
        IPluginPanelCatalog catalog,
        IExtensionPackageRepository packageRepository,
        IPluginVersionLeaseManager versionLeaseManager,
        DesktopSettingsJsonStore settingsStore,
        WebViewHostChannel hostChannel,
        Dispatcher dispatcher,
        PluginPanelResourceReader resources,
        ILogger<PluginPanelHostController> logger)
    {
        _catalog = catalog;
        _packageRepository = packageRepository;
        _versionLeaseManager = versionLeaseManager;
        _settingsStore = settingsStore;
        _hostChannel = hostChannel;
        _dispatcher = dispatcher;
        _resources = resources;
        _logger = logger;
        _hostChannel.ReadyChanged += OnHostReadyChanged;
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
                "plugin-host/get-panels" => await GetPanelsResponseAsync(type, requestId, cancellationToken),
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

        lock (_stateGate) return _openPlugins.TryGetValue(panelKey.Split('/')[0], out var open) && open.PanelKeys.Contains(panelKey)
            ? open.Permissions
            : null;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_stateGate) _stopping = true;
        await _openGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        _openGate.Release();
        Task pending;
        lock (_stateGate)
            pending = _resourceCount == 0 ? Task.CompletedTask :
                (_resourcesDrained ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_webView is not null && !_dispatcher.CheckAccess())
        {
            _dispatcher.Invoke(Dispose);
            return;
        }
        lock (_stateGate)
        {
            if (_disposed) return;
            _disposed = _stopping = true;
            _hostChannel.ReadyChanged -= OnHostReadyChanged;
            if (_webView is not null)
            {
                _webView.WebResourceRequested -= OnWebResourceRequested;
                _webView.RemoveWebResourceRequestedFilter(ResourceFilter, CoreWebView2WebResourceContext.All);
            }
            foreach (var pluginId in _openPlugins.Keys.ToArray()) ReleasePlugin(pluginId);
            _webView = null;
        }
    }

    private void EvictPlugin(string pluginId)
    {
        lock (_stateGate)
        {
            _pluginGenerations[pluginId] = _pluginGenerations.GetValueOrDefault(pluginId) + 1;
            ReleasePlugin(pluginId);
        }
        _hostChannel.PostPush(new { type = "plugin-host/evict", pluginId });
    }

    private void ReleasePlugin(string pluginId)
    {
        if (!_openPlugins.Remove(pluginId, out var open)) return;
        try { _webView?.ClearVirtualHostNameToFolderMapping(open.HostName); }
        catch (InvalidOperationException exception) { _logger.LogDebug(exception, "Plugin WebView mapping already closed."); }
        finally { open.Dispose(); }
    }

    private async Task<object> OpenAsync(string type, string? requestId, JsonElement payload, CancellationToken cancellationToken)
    {
        var key = ReadString(payload, "panelKey") ?? throw new ArgumentException("panelKey is required.");
        var pluginId = key.Split('/')[0];
        long pluginGeneration;
        long panelGeneration;
        lock (_stateGate)
        {
            _pluginGenerations.TryAdd(pluginId, 0);
            pluginGeneration = _pluginGenerations.GetValueOrDefault(pluginId);
            panelGeneration = _panelGenerations.GetValueOrDefault(key);
        }
        await _openGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        OpenPluginPanelSession? candidate = null;
        var transferred = false;
        var ownsCandidate = false;
        try
        {
            lock (_stateGate) ObjectDisposedException.ThrowIf(_stopping || _disposed, this);
            var panel = (await ListAvailablePanelsAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase))
                ?? throw new KeyNotFoundException($"Panel '{key}' is not available.");
            lock (_stateGate) _openPlugins.TryGetValue(panel.PluginId, out candidate);
            var existing = candidate is not null;
            if (!existing)
            {
                candidate = await AcquirePluginAsync(panel, cancellationToken).ConfigureAwait(false);
                ownsCandidate = true;
            }
            var session = candidate ?? throw new InvalidOperationException("The panel has no version session.");
            void Commit()
            {
                lock (_stateGate)
                {
                    if (_stopping || _disposed || _pluginGenerations.GetValueOrDefault(pluginId) != pluginGeneration ||
                        _panelGenerations.GetValueOrDefault(key) != panelGeneration)
                        throw new InvalidOperationException("The panel was closed while it was opening.");
                    if (!session.PanelKeys.Contains(panel.Key) && _openPlugins.Values.Sum(item => item.PanelKeys.Count) >= MaximumOpenPanels)
                        throw new InvalidOperationException($"At most {MaximumOpenPanels} panels can be open at once.");
                    if (!existing)
                    {
                        _webView?.SetVirtualHostNameToFolderMapping(session.HostName, session.RootPath, CoreWebView2HostResourceAccessKind.DenyCors);
                        _openPlugins.Add(panel.PluginId, session);
                    }
                    session.PanelKeys.Add(panel.Key);
                    transferred = true;
                }
                foreach (var handler in PanelOpened?.GetInvocationList().Cast<Action>() ?? [])
                {
                    try { handler(); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception exception) { _logger.LogError(exception, "Plugin panel observer failed after open."); }
                }
            }
            if (_webView is not null && !_dispatcher.CheckAccess()) await _dispatcher.InvokeAsync(Commit, DispatcherPriority.Normal, cancellationToken);
            else { cancellationToken.ThrowIfCancellationRequested(); Commit(); }
            return new { type, requestId, ok = true, panel, url = $"{panel.Url}?__selfclaw_panel={Uri.EscapeDataString(panel.Key)}" };
        }
        finally
        {
            if (!transferred && ownsCandidate) candidate?.Dispose();
            _openGate.Release();
        }
    }

    private async Task<OpenPluginPanelSession> AcquirePluginAsync(PluginPanelView panel, CancellationToken cancellationToken)
    {
        var package = await _packageRepository.GetPackageAsync(ExtensionKind.Plugin, panel.PluginId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Plugin '{panel.PluginId}' is not installed.");
        if (!package.IsEnabled) throw new InvalidOperationException("The plugin was disabled while opening.");
        var currentPanel = (await ListAvailablePanelsAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(item => string.Equals(item.Key, panel.Key, StringComparison.OrdinalIgnoreCase));
        if (currentPanel is null || currentPanel.Url != panel.Url ||
            !currentPanel.Permissions.SequenceEqual(panel.Permissions, StringComparer.Ordinal) ||
            !currentPanel.NetworkOrigins.SequenceEqual(panel.NetworkOrigins, StringComparer.Ordinal))
            throw new InvalidOperationException("The plugin permissions or panel changed while opening; retry the operation.");
        var lease = _versionLeaseManager.Acquire(package.InstallPath);
        try
        {
            return new OpenPluginPanelSession(panel.PluginId, $"{panel.PluginId}{PluginPanelOrigin.HostSuffix}",
                Path.GetFullPath(package.InstallPath), lease,
                PluginPanelResourceReader.BuildContentSecurityPolicy(panel.NetworkOrigins), panel.Permissions);
        }
        catch { lease.Dispose(); throw; }
    }

    private object CloseTab(string type, string? requestId, JsonElement payload)
    {
        var key = ReadString(payload, "panelKey") ?? throw new ArgumentException("panelKey is required.");
        var pluginId = key.Split('/')[0];
        lock (_stateGate)
        {
            _panelGenerations[key] = _panelGenerations.GetValueOrDefault(key) + 1;
            if (_openPlugins.TryGetValue(pluginId, out var open) && open.PanelKeys.Remove(key) && open.PanelKeys.Count == 0)
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
                .Select(item => item.GetString() ?? string.Empty)
                .Take(MaximumOpenPanels)
                .ToArray()
            : [];
        await _settingsStore.WriteNodeAsync(
            TabsSettingsNode,
            new PersistedPluginTabs(tabs, ReadString(payload, "activeKey")),
            JsonOptions,
            cancellationToken);
        return new { type, requestId, ok = true };
    }

    private async Task<object> GetPanelsResponseAsync(string type, string? requestId, CancellationToken cancellationToken)
    {
        var panels = await ListAvailablePanelsAsync(cancellationToken).ConfigureAwait(false);
        var persisted = await _settingsStore.ReadNodeAsync<PersistedPluginTabs>(TabsSettingsNode, JsonOptions, cancellationToken).ConfigureAwait(false);
        var tabs = persisted?.Tabs ?? [];
        return new { type, requestId, panels, tabs, activeKey = persisted?.ActiveKey is { } active && tabs.Contains(active) ? active : null };
    }

    private void OnHostReadyChanged(bool ready)
    {
        if (ready) return;
        lock (_stateGate)
            foreach (var pluginId in _pluginGenerations.Keys.ToArray()) _pluginGenerations[pluginId]++;
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
        if (environment is null) return;
        OpenPluginPanelSession? session = null;
        Uri? uri = null;
        lock (_stateGate)
        {
            if (!_stopping && Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out uri) && uri.Scheme == "https" && uri.IsDefaultPort &&
                uri.Host.EndsWith(PluginPanelOrigin.HostSuffix, StringComparison.OrdinalIgnoreCase))
                _openPlugins.TryGetValue(uri.Host[..^PluginPanelOrigin.HostSuffix.Length], out session);
        }
        if (session is null || uri is null)
        {
            e.Response = environment.CreateWebResourceResponse(null, 404, "Not Found", "Content-Type: text/plain");
            return;
        }
        var deferral = e.GetDeferral();
        lock (_stateGate) _resourceCount++;
        _ = CompleteResourceAsync(environment, e, deferral, session, Uri.UnescapeDataString(uri.AbsolutePath));
    }

    private async Task CompleteResourceAsync(CoreWebView2Environment environment, CoreWebView2WebResourceRequestedEventArgs request,
        CoreWebView2Deferral deferral, OpenPluginPanelSession session, string path)
    {
        try
        {
            using var lease = _versionLeaseManager.Acquire(session.RootPath);
            var resource = await _resources.ReadAsync(session.RootPath, path, session.ContentSecurityPolicy);
            lock (_stateGate)
                if (_stopping || !_openPlugins.TryGetValue(session.PluginId, out var current) || !ReferenceEquals(current, session))
                    resource = PluginPanelResourceReader.Failure(410, "Gone");
            request.Response = environment.CreateWebResourceResponse(new MemoryStream(resource.Content, writable: false),
                resource.StatusCode, resource.Reason, resource.Headers);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Plugin resource response failed for {PluginId}.", session.PluginId);
            request.Response = environment.CreateWebResourceResponse(null, 500, "Resource Error", "Content-Type: text/plain");
        }
        finally
        {
            try { deferral.Complete(); }
            finally { lock (_stateGate) if (--_resourceCount == 0) _resourcesDrained?.TrySetResult(); }
        }
    }

    private static string? ReadString(JsonElement payload, string propertyName)
    {
        if (!payload.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = element.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

}
