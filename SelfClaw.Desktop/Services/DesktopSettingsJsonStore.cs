using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Desktop.Services;

public sealed class DesktopSettingsJsonStore
{
    private const string SettingsFileName = "desktop-settings.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _settingsPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DesktopSettingsJsonStore(StoragePaths storagePaths)
    {
        _settingsPath = Path.Combine(storagePaths.AppDataDirectory, SettingsFileName);
    }

    public async Task<T?> ReadNodeAsync<T>(string nodeName, JsonSerializerOptions? serializerOptions = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var root = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (!root.TryGetPropertyValue(nodeName, out var node) || node is null)
            {
                return default;
            }

            return node.Deserialize<T>(serializerOptions);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task WriteNodeAsync<T>(string nodeName, T value, JsonSerializerOptions? serializerOptions = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var root = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            root[nodeName] = JsonSerializer.SerializeToNode(value, serializerOptions);
            await SaveCoreAsync(root, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<JsonObject> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_settingsPath))
        {
            return [];
        }

        try
        {
            await using var stream = File.OpenRead(_settingsPath);
            return await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false) as JsonObject
                   ?? [];
        }
        catch (IOException)
        {
            return [];
        }
        catch (JsonException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private async Task SaveCoreAsync(JsonObject root, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        await using var stream = File.Create(_settingsPath);
        await JsonSerializer.SerializeAsync(stream, root, JsonOptions, cancellationToken).ConfigureAwait(false);
    }
}
