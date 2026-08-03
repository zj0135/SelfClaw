using System.Text.Json;
using SelfClaw.Desktop.Services.ProgrammingAssistant.Models;

namespace SelfClaw.Desktop.Services.ProgrammingAssistant;

public sealed class ProgrammingAssistantSettingsBridge
{
    private readonly ProgrammingAssistantSettingsService _settingsService;

    public ProgrammingAssistantSettingsBridge(ProgrammingAssistantSettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public event Action<object>? ResponseReady;

    public async Task<bool> TryHandleAsync(
        string type,
        JsonElement payload,
        CancellationToken cancellationToken = default)
    {
        if (!IsSupported(type))
        {
            return false;
        }

        var requestId = ReadOptionalString(payload, "requestId");
        try
        {
            switch (type)
            {
                case "scan-programming-clis":
                    PostSettings(requestId, await _settingsService.RescanAsync(cancellationToken));
                    break;
                case "get-programming-assistant-settings":
                    PostSettings(requestId, await _settingsService.GetCurrentAsync(cancellationToken));
                    break;
                case "select-programming-cli":
                    PostSettings(
                        requestId,
                        await _settingsService.SelectCliAsync(ReadOptionalString(payload, "cliId"), cancellationToken));
                    break;
                case "select-programming-model":
                    PostSettings(
                        requestId,
                        await _settingsService.SelectModelAsync(ReadOptionalString(payload, "model"), cancellationToken));
                    break;
                case "select-programming-reasoning":
                    PostSettings(
                        requestId,
                        await _settingsService.SelectReasoningLevelAsync(
                            ReadOptionalString(payload, "reasoningLevel"),
                            cancellationToken));
                    break;
                case "test-programming-cli":
                    PostTestResult(
                        requestId,
                        await _settingsService.TestCliAsync(ReadOptionalString(payload, "cliId"), cancellationToken));
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (string.Equals(type, "test-programming-cli", StringComparison.Ordinal))
            {
                PostTestResult(
                    requestId,
                    new CliTestResult(
                        ReadOptionalString(payload, "cliId") ?? string.Empty,
                        false,
                        null,
                        exception.Message));
            }
            else
            {
                Post(new
                {
                    type = "programming-assistant-settings",
                    requestId,
                    tools = Array.Empty<DetectedProgrammingCli>(),
                    selectedCliId = (string?)null,
                    error = exception.Message
                });
            }
        }

        return true;
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

    private void PostSettings(string? requestId, ProgrammingAssistantSettings settings)
        => Post(new
        {
            type = "programming-assistant-settings",
            requestId,
            settings.SelectedCliId,
            settings.SelectedModel,
            settings.SelectedReasoningLevel,
            settings.Tools,
            settings.ScannedAtUtc
        });

    private void PostTestResult(string? requestId, CliTestResult result)
        => Post(new
        {
            type = "programming-cli-test-result",
            requestId,
            cliId = result.CliId,
            success = result.Success,
            version = result.Version,
            error = result.Error
        });

    private void Post(object payload) => ResponseReady?.Invoke(payload);
}
