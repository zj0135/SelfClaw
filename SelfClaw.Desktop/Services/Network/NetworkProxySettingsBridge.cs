using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services.WebView;

namespace SelfClaw.Desktop.Services.Network;

/// <summary>
/// Settings-page protocol for the network exit. Saving delegates to the service, so an unusable value never
/// errors back to the page - it is normalized and echoed, the same contract the workspace-tools and
/// compression bridges follow.
/// </summary>
internal sealed class NetworkProxySettingsBridge : IDisposable
{
    private readonly NetworkProxySettingsService _settingsService;
    private readonly WebViewHostChannel _channel;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<NetworkProxySettingsBridge> _logger;
    private bool _disposed;

    public NetworkProxySettingsBridge(
        NetworkProxySettingsService settingsService,
        WebViewHostChannel channel,
        Dispatcher dispatcher,
        ILogger<NetworkProxySettingsBridge> logger)
    {
        ArgumentNullException.ThrowIfNull(settingsService);
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(logger);
        _settingsService = settingsService;
        _channel = channel;
        _dispatcher = dispatcher;
        _logger = logger;
        _settingsService.Changed += OnChanged;
    }

    public void Dispose()
    {
        _disposed = true;
        _settingsService.Changed -= OnChanged;
    }

    /// <summary>Shaped like the workspace-tools bridge: null means the message is not ours; the command slot
    /// stays unused until a host side effect is needed.</summary>
    public async Task<(object Response, WebViewHostCommand? Command)?> TryHandleAsync(
        string type,
        JsonElement payload,
        CancellationToken cancellationToken = default)
    {
        if (type is not ("network-proxy/get" or "network-proxy/save"))
        {
            return null;
        }

        var requestId = ReadOptionalString(payload, "requestId");
        try
        {
            var settings = type == "network-proxy/save"
                ? await _settingsService.SaveAsync(ReadSettings(payload), cancellationToken)
                : await _settingsService.GetAsync(cancellationToken);
            return (BuildResponse(requestId, settings), null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // 回包必须仍是一份可用状态，否则前端会停在半截：错误时回显最后一次确认的设置。
            var current = _settingsService.Current;
            return (new
            {
                type = "network-proxy/state",
                requestId,
                mode = current.Mode.ToString(),
                host = current.Host,
                port = current.Port,
                error = exception.Message
            }, null);
        }
    }

    private void OnChanged(NetworkProxySettings settings) => _ = PublishAsync();

    // The change may come from any caller, so reread the service instead of reusing a save's return value.
    private async Task PublishAsync()
    {
        if (_disposed || _dispatcher.HasShutdownStarted)
        {
            return;
        }

        try
        {
            var settings = await _settingsService.GetAsync().ConfigureAwait(false);
            if (_dispatcher.CheckAccess())
            {
                Push(settings);
                return;
            }

            await _dispatcher.InvokeAsync(() => Push(settings));
        }
        catch (OperationCanceledException)
        {
            // No token is held here; real cancellation only happens during shutdown, when nobody listens.
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to publish network proxy state.");
        }
    }

    private void Push(NetworkProxySettings settings)
    {
        // PostPush is a no-op before the shell is ready; the next get will pick the state up.
        if (!_disposed) _channel.PostPush(BuildResponse(null, settings));
    }

    private static object BuildResponse(string? requestId, NetworkProxySettings settings) => new
    {
        type = "network-proxy/state",
        requestId,
        mode = settings.Mode.ToString(),
        host = settings.Host,
        port = settings.Port
    };

    private static NetworkProxySettings ReadSettings(JsonElement payload)
        => new(ReadMode(payload), ReadOptionalString(payload, "host") ?? string.Empty, ReadPort(payload));

    private static NetworkProxyMode ReadMode(JsonElement payload)
        => Enum.TryParse<NetworkProxyMode>(ReadOptionalString(payload, "mode"), ignoreCase: false, out var mode)
            ? mode
            : NetworkProxyMode.Direct;

    private static int ReadPort(JsonElement payload)
        => payload.TryGetProperty("port", out var element) &&
           element.ValueKind == JsonValueKind.Number &&
           element.TryGetInt32(out var port)
            ? port
            : 0;

    private static string? ReadOptionalString(JsonElement payload, string propertyName)
    {
        if (!payload.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = element.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
