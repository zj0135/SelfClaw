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
/// Owns the host side of Plugin views — docked and floating alike: which views are open, the origin
/// each one is served from, the version directory its files are pinned to, and the security headers its
/// responses carry. The shell decides where a view is presented; everything that grants a capability
/// lives here, and only the per-slot open cap depends on the slot.
/// </summary>
internal sealed class PluginViewHostController : IPluginViewSessionRegistry, IDisposable
{
    private const string ViewsSettingsNode = "pluginViews";
    private const string MessagePrefix = "plugin-host/";
    private const string ResourceFilter = "https://*" + PluginViewOrigin.HostSuffix + "/*";

    // Separate caps rather than one total: the docked number is a tab-bar affordance, while a floating
    // view can cover the whole window, so having many of them open is a legibility and abuse concern.
    private const int MaximumOpenDockedViews = 8;
    private const int MaximumOpenFloatingViews = 4;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly IPluginViewCatalog _catalog;
    private readonly IExtensionPackageRepository _packageRepository;
    private readonly IPluginVersionLeaseManager _versionLeaseManager;
    private readonly DesktopSettingsJsonStore _settingsStore;
    private readonly WebViewHostChannel _hostChannel;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, OpenPluginViewSession> _openViews = new(StringComparer.OrdinalIgnoreCase);
    private CoreWebView2? _webView;
    private readonly PluginViewResourceReader _resources;
    private readonly ILogger<PluginViewHostController> _logger;
    private readonly SemaphoreSlim _openGate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly Dictionary<string, long> _pluginGenerations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _viewGenerations = new(StringComparer.OrdinalIgnoreCase);
    // Slot per open key, so the per-slot cap can be applied without re-reading the catalog.
    private readonly Dictionary<string, PluginViewSlot> _viewSlots = new(StringComparer.OrdinalIgnoreCase);
    private bool _stopping;
    private bool _disposed;
    private int _resourceCount;
    private TaskCompletionSource? _resourcesDrained;


    public PluginViewHostController(
        IPluginViewCatalog catalog,
        IExtensionPackageRepository packageRepository,
        IPluginVersionLeaseManager versionLeaseManager,
        DesktopSettingsJsonStore settingsStore,
        WebViewHostChannel hostChannel,
        Dispatcher dispatcher,
        PluginViewResourceReader resources,
        ILogger<PluginViewHostController> logger)
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
    /// Raised after a view has been opened. A view that just appeared has no event history to replay,
    /// so this is what prompts an unconditional context push in its direction.
    /// </summary>
    public event Action? ViewOpened;

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
                "plugin-host/get-views" => await GetViewsResponseAsync(type, requestId, cancellationToken),
                "plugin-host/open" => await OpenAsync(type, requestId, payload, cancellationToken),
                "plugin-host/close" => CloseView(type, requestId, payload),
                "plugin-host/save-views" => await SaveViewsAsync(type, requestId, payload, cancellationToken),
                _ => new { type, requestId, error = $"Unsupported plugin view message type '{type}'." }
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
    /// mapping and the lease are gone the view can no longer fetch anything, so a slow or wedged frame
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
    /// The permissions a view actually holds, or null when it is not open. Resolved from host state
    /// rather than from the caller's payload so that a capability check never depends on what the shell
    /// chose to relay.
    /// </summary>
    public IReadOnlyList<string>? GetPermissions(string? viewKey)
    {
        if (string.IsNullOrWhiteSpace(viewKey))
        {
            return null;
        }

        lock (_stateGate) return _openViews.TryGetValue(viewKey.Split('/')[0], out var open) && open.ViewKeys.Contains(viewKey)
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
            foreach (var pluginId in _openViews.Keys.ToArray()) ReleasePlugin(pluginId);
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
        if (!_openViews.Remove(pluginId, out var open)) return;
        foreach (var viewKey in open.ViewKeys) _viewSlots.Remove(viewKey);
        try { _webView?.ClearVirtualHostNameToFolderMapping(open.HostName); }
        catch (InvalidOperationException exception) { _logger.LogDebug(exception, "Plugin WebView mapping already closed."); }
        finally { open.Dispose(); }
    }

    private async Task<object> OpenAsync(string type, string? requestId, JsonElement payload, CancellationToken cancellationToken)
    {
        var key = ReadString(payload, "viewKey") ?? throw new ArgumentException("viewKey is required.");
        var pluginId = key.Split('/')[0];
        long pluginGeneration;
        long viewGeneration;
        lock (_stateGate)
        {
            _pluginGenerations.TryAdd(pluginId, 0);
            pluginGeneration = _pluginGenerations.GetValueOrDefault(pluginId);
            viewGeneration = _viewGenerations.GetValueOrDefault(key);
        }
        await _openGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        OpenPluginViewSession? candidate = null;
        var transferred = false;
        var ownsCandidate = false;
        try
        {
            lock (_stateGate) ObjectDisposedException.ThrowIf(_stopping || _disposed, this);
            var view = (await ListAvailableViewsAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase))
                ?? throw new KeyNotFoundException($"View '{key}' is not available.");
            lock (_stateGate) _openViews.TryGetValue(view.PluginId, out candidate);
            var existing = candidate is not null;
            if (!existing)
            {
                candidate = await AcquirePluginAsync(view, cancellationToken).ConfigureAwait(false);
                ownsCandidate = true;
            }
            var session = candidate ?? throw new InvalidOperationException("The view has no version session.");
            void Commit()
            {
                lock (_stateGate)
                {
                    if (_stopping || _disposed || _pluginGenerations.GetValueOrDefault(pluginId) != pluginGeneration ||
                        _viewGenerations.GetValueOrDefault(key) != viewGeneration)
                        throw new InvalidOperationException("The view was closed while it was opening.");
                    if (!session.ViewKeys.Contains(view.Key) && !HasRoomFor(view.Slot))
                        throw new InvalidOperationException(view.Slot == PluginViewSlot.Floating
                            ? $"At most {MaximumOpenFloatingViews} floating views can be open at once."
                            : $"At most {MaximumOpenDockedViews} docked views can be open at once.");
                    if (!existing)
                    {
                        _webView?.SetVirtualHostNameToFolderMapping(session.HostName, session.RootPath, CoreWebView2HostResourceAccessKind.DenyCors);
                        _openViews.Add(view.PluginId, session);
                    }
                    session.ViewKeys.Add(view.Key);
                    _viewSlots[view.Key] = view.Slot;
                    transferred = true;
                }
                foreach (var handler in ViewOpened?.GetInvocationList().Cast<Action>() ?? [])
                {
                    try { handler(); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception exception) { _logger.LogError(exception, "Plugin view observer failed after open."); }
                }
            }
            if (_webView is not null && !_dispatcher.CheckAccess()) await _dispatcher.InvokeAsync(Commit, DispatcherPriority.Normal, cancellationToken);
            else { cancellationToken.ThrowIfCancellationRequested(); Commit(); }
            return new { type, requestId, ok = true, view, url = $"{view.Url}?__selfclaw_view={Uri.EscapeDataString(view.Key)}" };
        }
        finally
        {
            if (!transferred && ownsCandidate) candidate?.Dispose();
            _openGate.Release();
        }
    }

    // Counted per slot: the two caps are independent budgets, so opening a floating view must not consume
    // a docked one. Called under _stateGate.
    private bool HasRoomFor(PluginViewSlot slot)
    {
        var openCount = _openViews.Values
            .SelectMany(session => session.ViewKeys)
            .Count(key => _viewSlots.GetValueOrDefault(key) == slot);
        return openCount < (slot == PluginViewSlot.Floating ? MaximumOpenFloatingViews : MaximumOpenDockedViews);
    }

    private async Task<OpenPluginViewSession> AcquirePluginAsync(PluginView view, CancellationToken cancellationToken)
    {
        var package = await _packageRepository.GetPackageAsync(ExtensionKind.Plugin, view.PluginId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Plugin '{view.PluginId}' is not installed.");
        if (!package.IsEnabled) throw new InvalidOperationException("The plugin was disabled while opening.");
        var currentView = (await ListAvailableViewsAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(item => string.Equals(item.Key, view.Key, StringComparison.OrdinalIgnoreCase));
        if (currentView is null || currentView.Url != view.Url ||
            !currentView.Permissions.SequenceEqual(view.Permissions, StringComparer.Ordinal) ||
            !currentView.NetworkOrigins.SequenceEqual(view.NetworkOrigins, StringComparer.Ordinal))
            throw new InvalidOperationException("The plugin permissions or view changed while opening; retry the operation.");
        var lease = _versionLeaseManager.Acquire(package.InstallPath);
        try
        {
            return new OpenPluginViewSession(view.PluginId, $"{view.PluginId}{PluginViewOrigin.HostSuffix}",
                Path.GetFullPath(package.InstallPath), lease,
                PluginViewResourceReader.BuildContentSecurityPolicy(view.NetworkOrigins), view.Permissions);
        }
        catch { lease.Dispose(); throw; }
    }

    private object CloseView(string type, string? requestId, JsonElement payload)
    {
        var key = ReadString(payload, "viewKey") ?? throw new ArgumentException("viewKey is required.");
        var pluginId = key.Split('/')[0];
        lock (_stateGate)
        {
            _viewGenerations[key] = _viewGenerations.GetValueOrDefault(key) + 1;
            _viewSlots.Remove(key);
            if (_openViews.TryGetValue(pluginId, out var open) && open.ViewKeys.Remove(key) && open.ViewKeys.Count == 0)
                ReleasePlugin(pluginId);
        }

        return new { type, requestId, ok = true };
    }

    private async Task<object> SaveViewsAsync(
        string type,
        string? requestId,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var views = payload.TryGetProperty("views", out var viewsElement) && viewsElement.ValueKind == JsonValueKind.Array
            ? viewsElement.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? string.Empty)
                .Take(MaximumOpenDockedViews + MaximumOpenFloatingViews)
                .ToArray()
            : [];
        await _settingsStore.WriteNodeAsync(
            ViewsSettingsNode,
            new PersistedPluginViews(views, ReadString(payload, "activeView")),
            JsonOptions,
            cancellationToken);
        return new { type, requestId, ok = true };
    }

    private async Task<object> GetViewsResponseAsync(string type, string? requestId, CancellationToken cancellationToken)
    {
        var views = await ListAvailableViewsAsync(cancellationToken).ConfigureAwait(false);
        var persisted = await _settingsStore.ReadNodeAsync<PersistedPluginViews>(ViewsSettingsNode, JsonOptions, cancellationToken).ConfigureAwait(false);
        var openViews = persisted?.Views ?? [];
        return new
        {
            type,
            requestId,
            views,
            openViews,
            activeView = persisted?.ActiveView is { } active && openViews.Contains(active) ? active : null
        };
    }

    private void OnHostReadyChanged(bool ready)
    {
        if (ready) return;
        lock (_stateGate)
            foreach (var pluginId in _pluginGenerations.Keys.ToArray()) _pluginGenerations[pluginId]++;
    }

    // Only views whose Plugin is enabled and whose permissions were acknowledged are offered. A view
    // that is merely installed must not be reachable, otherwise enabling would stop being the moment the
    // user grants a Plugin its capabilities.
    private async Task<IReadOnlyList<PluginView>> ListAvailableViewsAsync(CancellationToken cancellationToken)
        => (await _catalog.ListPluginViewsAsync(cancellationToken))
            .Where(view => view.Enabled && view.Status == ExtensionStatus.Ready)
            .ToArray();

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var environment = _webView?.Environment;
        if (environment is null) return;
        OpenPluginViewSession? session = null;
        Uri? uri = null;
        lock (_stateGate)
        {
            if (!_stopping && Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out uri) && uri.Scheme == "https" && uri.IsDefaultPort &&
                uri.Host.EndsWith(PluginViewOrigin.HostSuffix, StringComparison.OrdinalIgnoreCase))
                _openViews.TryGetValue(uri.Host[..^PluginViewOrigin.HostSuffix.Length], out session);
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
        CoreWebView2Deferral deferral, OpenPluginViewSession session, string path)
    {
        try
        {
            using var lease = _versionLeaseManager.Acquire(session.RootPath);
            var resource = await _resources.ReadAsync(session.RootPath, path, session.ContentSecurityPolicy);
            lock (_stateGate)
                if (_stopping || !_openViews.TryGetValue(session.PluginId, out var current) || !ReferenceEquals(current, session))
                    resource = PluginViewResourceReader.Failure(410, "Gone");
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
