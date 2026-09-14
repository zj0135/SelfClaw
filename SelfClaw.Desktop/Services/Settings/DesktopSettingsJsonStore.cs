using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Desktop.Services.Settings;

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
        ArgumentNullException.ThrowIfNull(storagePaths);
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
        try
        {
            await using var stream = File.OpenRead(_settingsPath);
            return await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false) as JsonObject
                   ?? throw new JsonException("Desktop settings must contain a JSON object.");
        }
        catch (FileNotFoundException)
        {
            return [];
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
    }

    private async Task SaveCoreAsync(JsonObject root, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_settingsPath)
            ?? throw new InvalidOperationException("Desktop settings require a parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{SettingsFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, root, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(_settingsPath))
                File.Replace(temporaryPath, _settingsPath, null);
            else
                File.Move(temporaryPath, _settingsPath);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }
}
