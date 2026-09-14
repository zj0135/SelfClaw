using System.Text.Json;
using System.Windows.Threading;
using SelfClaw.Desktop.Services.ProgrammingAssistant.Models;
using SelfClaw.Desktop.Services.WebView;

namespace SelfClaw.Desktop.Services.ProgrammingAssistant;

internal sealed class ProgrammingAssistantSettingsBridge : IDisposable
{
    private readonly ProgrammingAssistantSettingsService _settingsService;
    private readonly WebViewHostChannel _channel;
    private readonly Dispatcher _dispatcher;
    private bool _disposed;

    public ProgrammingAssistantSettingsBridge(ProgrammingAssistantSettingsService settingsService,
        WebViewHostChannel channel, Dispatcher dispatcher)
    {
        _settingsService = settingsService;
        _channel = channel;
        _dispatcher = dispatcher;
        _settingsService.Changed += OnChanged;
    }

    public void Dispose()
    {
        _disposed = true;
        _settingsService.Changed -= OnChanged;
    }

    private void OnChanged(ProgrammingAssistantSettings settings)
    {
        if (_disposed || _dispatcher.HasShutdownStarted) return;
        if (!_dispatcher.CheckAccess())
        {
            _ = _dispatcher.InvokeAsync(() => OnChanged(settings));
            return;
        }
        _channel.PostPush(BuildSettingsResponse(null, settings, "programming-assistant-settings-changed"));
    }

    public async Task<object?> TryHandleAsync(
        string type,
        JsonElement payload,
        CancellationToken cancellationToken = default)
    {
        if (!IsSupported(type))
        {
            return null;
        }

        var requestId = ReadOptionalString(payload, "requestId");
        try
        {
            return type switch
            {
                "scan-programming-clis" => BuildSettingsResponse(
                    requestId,
                    await _settingsService.RescanAsync(cancellationToken)),
                "get-programming-assistant-settings" => BuildSettingsResponse(
                    requestId,
                    await _settingsService.GetCurrentAsync(cancellationToken)),
                "select-programming-cli" => BuildSettingsResponse(
                        requestId,
                        await _settingsService.SelectCliAsync(ReadOptionalString(payload, "cliId"), cancellationToken)),
                "select-programming-model" => BuildSettingsResponse(
                        requestId,
                        await _settingsService.SelectModelAsync(ReadOptionalString(payload, "model"), cancellationToken)),
                "select-programming-reasoning" => BuildSettingsResponse(
                        requestId,
                        await _settingsService.SelectReasoningLevelAsync(
                            ReadOptionalString(payload, "reasoningLevel"),
                            cancellationToken)),
                "test-programming-cli" => BuildTestResultResponse(
                        requestId,
                        await _settingsService.TestCliAsync(ReadOptionalString(payload, "cliId"), cancellationToken)),
                _ => throw new InvalidOperationException($"Unsupported programming assistant message type '{type}'.")
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (string.Equals(type, "test-programming-cli", StringComparison.Ordinal))
            {
                return BuildTestResultResponse(
                    requestId,
                    new CliTestResult(
                        ReadOptionalString(payload, "cliId") ?? string.Empty,
                        false,
                        null,
                        exception.Message));
            }

            return new
            {
                type = "programming-assistant-settings",
                requestId,
                tools = Array.Empty<DetectedProgrammingCli>(),
                selectedCliId = (string?)null,
                error = exception.Message
            };
        }
    }

    private static bool IsSupported(string type)
        => type is
            "scan-programming-clis" or
            "get-programming-assistant-settings" or
            "select-programming-cli" or
            "select-programming-model" or
            "select-programming-reasoning" or
            "test-programming-cli";

    private static string? ReadOptionalString(JsonElement payload, string propertyName)
    {
        if (!payload.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = element.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private object BuildSettingsResponse(string? requestId, ProgrammingAssistantSettings settings,
        string type = "programming-assistant-settings")
        => new
        {
            type,
            requestId,
            settings.SelectedCliId,
            settings.SelectedModel,
            settings.SelectedReasoningLevel,
            settings.Tools,
            settings.ScannedAtUtc,
            settings.Revision,
            isScanning = _settingsService.IsScanning,
            scanError = _settingsService.ScanError
        };

    private static object BuildTestResultResponse(string? requestId, CliTestResult result)
        => new
        {
            type = "programming-cli-test-result",
            requestId,
            cliId = result.CliId,
            success = result.Success,
            version = result.Version,
            error = result.Error
        };
}
