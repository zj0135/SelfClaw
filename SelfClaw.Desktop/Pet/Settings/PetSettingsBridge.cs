using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using SelfClaw.Desktop.Pet;
using SelfClaw.Desktop.Services.WebView;

namespace SelfClaw.Desktop.Pet;

internal sealed class PetSettingsBridge : IDisposable
{
    private const string AssetsHostName = "appassets.selfclaw.local";
    private readonly PetHost _petHost;
    private readonly WebViewHostChannel _channel;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<PetSettingsBridge> _logger;
    private bool _disposed;

    public PetSettingsBridge(PetHost petHost, WebViewHostChannel channel, Dispatcher dispatcher, ILogger<PetSettingsBridge> logger)
    {
        _petHost = petHost;
        _channel = channel;
        _dispatcher = dispatcher;
        _logger = logger;
        _petHost.Changed += OnChanged;
    }

    public void Dispose() { _disposed = true; _petHost.Changed -= OnChanged; }
    private void OnChanged() => _ = PublishAsync();
    private async Task PublishAsync()
    {
        if (_disposed || _dispatcher.HasShutdownStarted) return;
        try
        {
            var state = await _petHost.GetStateAsync().ConfigureAwait(false);
            await _dispatcher.InvokeAsync(() => { if (!_disposed) _channel.PostPush(BuildStateResponse(null, state, "pet-settings-changed")); });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) { _logger.LogWarning(exception, "Failed to publish pet state."); }
    }

    public async Task<object?> TryHandleAsync(
        string type,
        JsonElement payload,
        CancellationToken cancellationToken = default)
    {
        if (type is not ("get-pet-settings" or "set-pet-visible" or "select-builtin-pet"))
        {
            return null;
        }

        var requestId = ReadOptionalString(payload, "requestId");
        try
        {
            var state = type switch
            {
                "get-pet-settings" => await _petHost.GetStateAsync(cancellationToken),
                "set-pet-visible" => await _petHost.ExecuteAsync(
                    new PetHostCommand(
                        ReadBoolean(payload, "enabled")
                            ? PetHostCommandKind.Show
                            : PetHostCommandKind.Hide),
                    cancellationToken),
                "select-builtin-pet" => await _petHost.ExecuteAsync(
                    new PetHostCommand(
                        PetHostCommandKind.SelectBuiltInPet,
                        ReadOptionalString(payload, "petId")),
                    cancellationToken),
                _ => throw new InvalidOperationException($"Unsupported pet settings message type '{type}'.")
            };
            return BuildStateResponse(requestId, state);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new
            {
                type = "pet-settings",
                requestId,
                enabled = false,
                selectedPetId = (string?)null,
                spriteSheetPath = (string?)null,
                error = exception.Message
            };
        }
    }

    private static bool ReadBoolean(JsonElement payload, string propertyName)
        => payload.TryGetProperty(propertyName, out var element) &&
           element.ValueKind is JsonValueKind.True or JsonValueKind.False &&
           element.GetBoolean();

    private static string? ReadOptionalString(JsonElement payload, string propertyName)
    {
        if (!payload.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = element.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static object BuildStateResponse(string? requestId, PetHostState state, string type = "pet-settings")
        => new
        {
            type,
            requestId,
            state.Revision,
            state.IsVisible,
            state.ActualPetId,
            state.LoadStatus,
            state.LoadError,
            enabled = state.Settings.Enabled,
            selectedPetId = state.SelectedBuiltInPetId,
            spriteSheetPath = state.Settings.SpriteSheetPath,
            pets = state.BuiltInPackages.Select(package => new
            {
                package.Id,
                package.DisplayName,
                package.Description,
                package.Author,
                package.Tags,
                package.Source,
                package.SourceUrl,
                previewSrc = $"https://{AssetsHostName}/{package.PreviewAssetPath}",
                cols = package.Columns,
                rows = package.Rows
            }).ToArray()
        };
}
