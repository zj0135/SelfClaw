using System.Text.Json;
using SelfClaw.Desktop.Services.ProgrammingAssistant.Models;

namespace SelfClaw.Desktop.Services.ProgrammingAssistant;

internal sealed class ProgrammingAssistantSettingsBridge
{
    private readonly ProgrammingAssistantSettingsService _settingsService;

    public ProgrammingAssistantSettingsBridge(ProgrammingAssistantSettingsService settingsService)
    {
        _settingsService = settingsService;
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

    private static object BuildSettingsResponse(string? requestId, ProgrammingAssistantSettings settings)
        => new
        {
            type = "programming-assistant-settings",
            requestId,
            settings.SelectedCliId,
            settings.SelectedModel,
            settings.SelectedReasoningLevel,
            settings.Tools,
            settings.ScannedAtUtc
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
