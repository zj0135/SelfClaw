using System.Text.Json;
using System.Text.Json.Serialization;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services.Settings;

namespace SelfClaw.Desktop.Services.Network;

/// <summary>
/// Owns the "which network exit do SelfClaw's requests use" setting in desktop-settings.json. The service is
/// also the single source Infrastructure reads from (per HTTP handler creation and per child process
/// launch), so it keeps a cached snapshot that callers can read synchronously. An unreadable or invalid
/// stored node falls back to the default - connecting directly, the same exit as a fresh install - rather
/// than failing a request.
/// </summary>
public sealed class NetworkProxySettingsService : INetworkProxySource
{
    private const string SettingsNodeName = "network_proxy";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly DesktopSettingsJsonStore _settingsStore;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NetworkProxySettings _settings = new();
    private bool _loaded;

    public NetworkProxySettingsService(DesktopSettingsJsonStore settingsStore)
    {
        ArgumentNullException.ThrowIfNull(settingsStore);
        _settingsStore = settingsStore;
    }

    /// <summary>Raised after a committed change; the payload is the new settings.</summary>
    public event Action<NetworkProxySettings>? Changed;

    /// <summary>
    /// The cached settings. Before the first load this answers the default, so a request that races the
    /// startup warm-up connects directly instead of waiting for the settings file.
    /// </summary>
    public NetworkProxySettings Current => _settings;

    public async Task<NetworkProxySettings> GetAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedCoreAsync(cancellationToken).ConfigureAwait(false);
            return _settings;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<NetworkProxySettings> SaveAsync(
        NetworkProxySettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var normalized = Normalize(settings);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedCoreAsync(cancellationToken).ConfigureAwait(false);
            if (_settings == normalized)
            {
                return _settings;
            }

            await _settingsStore
                .WriteNodeAsync(SettingsNodeName, normalized, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            _settings = normalized;
        }
        finally
        {
            _gate.Release();
        }

        // 在锁外触发：订阅者可能同步回读 Current/GetAsync。
        Changed?.Invoke(normalized);
        return normalized;
    }

    private async Task EnsureLoadedCoreAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
        {
            return;
        }

        _settings = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
        _loaded = true;
    }

    private async Task<NetworkProxySettings> LoadCoreAsync(CancellationToken cancellationToken)
    {
        NetworkProxySettings? stored;
        try
        {
            stored = await _settingsStore
                .ReadNodeAsync<NetworkProxySettings>(SettingsNodeName, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            // A hand-edited or schema-drifted node is not worth failing every request over; the default
            // exit keeps the application working and the next save rewrites the node.
            return new NetworkProxySettings();
        }

        return Normalize(stored);
    }

    /// <summary>
    /// Drops only what cannot be used at all: an unknown mode (a hand-edited number) becomes the default,
    /// and an unparsable port becomes 0 so the incomplete endpoint stays recognisable. The host and port the
    /// user is still filling in are kept as typed - the exit is resolved at use time, so an incomplete
    /// custom endpoint does not flip the selected mode under the user's cursor.
    /// </summary>
    private static NetworkProxySettings Normalize(NetworkProxySettings? settings)
    {
        if (settings is null)
        {
            return new NetworkProxySettings();
        }

        var mode = Enum.IsDefined(settings.Mode) ? settings.Mode : NetworkProxyMode.Direct;
        var host = settings.Host?.Trim() ?? string.Empty;
        if (host.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            host = host[7..].Trim();
        }
        else if (host.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            host = host[8..].Trim();
        }

        host = host.TrimEnd('/');
        var port = settings.Port is >= 1 and <= 65535 ? settings.Port : 0;
        return new NetworkProxySettings(mode, host, port);
    }
}
