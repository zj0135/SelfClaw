using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services.WebView;

namespace SelfClaw.Desktop.Services.Tools;

/// <summary>
/// Settings-page protocol for the workspace shell preference. Saving delegates to the service, so an
/// unparsable kind never errors back to the page - it is corrected to the default and echoed, the same
/// normalization contract the compression settings bridge follows.
/// </summary>
internal sealed class WorkspaceToolsSettingsBridge : IDisposable
{
    private readonly WorkspaceToolsSettingsService _settingsService;
    private readonly WebViewHostChannel _channel;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<WorkspaceToolsSettingsBridge> _logger;
    private bool _disposed;

    public WorkspaceToolsSettingsBridge(
        WorkspaceToolsSettingsService settingsService,
        WebViewHostChannel channel,
        Dispatcher dispatcher,
        ILogger<WorkspaceToolsSettingsBridge> logger)
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

    /// <summary>Shaped like the direct-compression settings bridge: null means the message is not ours;
    /// the command slot stays unused until a host side effect is needed.</summary>
    public async Task<(object Response, WebViewHostCommand? Command)?> TryHandleAsync(
        string type,
        JsonElement payload,
        CancellationToken cancellationToken = default)
    {
        if (type is not ("workspace-tools/get" or "workspace-tools/save"))
        {
            return null;
        }

        var requestId = ReadOptionalString(payload, "requestId");
        try
        {
            var preference = type == "workspace-tools/save"
                ? await _settingsService.SaveAsync(ReadShellKind(payload), cancellationToken)
                : await _settingsService.GetPreferenceAsync(cancellationToken);
            return (BuildResponse(requestId, preference), null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return (new
            {
                type = "workspace-tools/state",
                requestId,
                shellKind = WorkspaceShellKind.WindowsPowerShell.ToString(),
                shells = Array.Empty<object>(),
                error = exception.Message
            }, null);
        }
    }

    private void OnChanged(WorkspaceShellPreference preference) => _ = PublishAsync();

    // The change may come from any caller, so reread the service instead of reusing a save's return value.
    private async Task PublishAsync()
    {
        if (_disposed || _dispatcher.HasShutdownStarted)
        {
            return;
        }

        try
        {
            var preference = await _settingsService.GetPreferenceAsync().ConfigureAwait(false);
            if (_dispatcher.CheckAccess())
            {
                Push(preference);
                return;
            }

            await _dispatcher.InvokeAsync(() => Push(preference));
        }
        catch (OperationCanceledException)
        {
            // No token is held here; real cancellation only happens during shutdown, when nobody listens.
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to publish workspace tools state.");
        }
    }

    private void Push(WorkspaceShellPreference preference)
    {
        // PostPush is a no-op before the shell is ready; the next get will pick the state up.
        if (!_disposed) _channel.PostPush(BuildResponse(null, preference));
    }

    private object BuildResponse(string? requestId, WorkspaceShellPreference preference) => new
    {
        type = "workspace-tools/state",
        requestId,
        shellKind = preference.ShellKind.ToString(),
        shells = _settingsService.GetShells().Select(ToShellPayload).ToArray()
    };

    private static object ToShellPayload(WorkspaceShellAvailability shell) => new
    {
        kind = shell.Kind.ToString(),
        available = shell.Available,
        resolvedPath = shell.ResolvedPath
    };

    private static WorkspaceShellKind ReadShellKind(JsonElement payload)
    {
        var raw = ReadOptionalString(payload, "shellKind");
        return Enum.TryParse<WorkspaceShellKind>(raw, ignoreCase: false, out var kind) ? kind : WorkspaceShellKind.WindowsPowerShell;
    }

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
