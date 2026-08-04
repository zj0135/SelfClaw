using System.Text.Json;
using System.Text.Json.Serialization;
using SelfClaw.Infrastructure.AiProviders.Abstractions;
using SelfClaw.Infrastructure.AiProviders.Models;
using SelfClaw.Infrastructure.AiProviders.Models.Views;

namespace SelfClaw.Desktop.Services.AiProviders;

internal sealed class AiProviderSettingsBridge
{
    private const string MessagePrefix = "ai-providers/";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IAiProviderSettingsService _settingsService;

    public AiProviderSettingsBridge(IAiProviderSettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public event Action<Guid?>? ModelSelectionChanged;

    public async Task<object?> TryHandleAsync(
        string type,
        JsonElement payload,
        CancellationToken cancellationToken = default)
    {
        if (!type.StartsWith(MessagePrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var requestId = ReadOptionalString(payload, "requestId");
        try
        {
            object response;
            switch (type)
            {
                case "ai-providers/get-state":
                {
                    var state = await _settingsService.GetStateAsync(cancellationToken);
                    response = new { type, requestId, state };
                    break;
                }
                case "ai-providers/save-provider":
                {
                    var provider = await _settingsService.SaveProviderAsync(
                        ReadSaveProviderCommand(payload),
                        cancellationToken);
                    response = new { type, requestId, provider };
                    break;
                }
                case "ai-providers/set-provider-enabled":
                    await _settingsService.SetProviderEnabledAsync(
                        ReadRequiredGuid(payload, "id", "providerId", "connectionId"),
                        ReadRequiredBoolean(payload, "enabled"),
                        cancellationToken);
                    response = new { type, requestId, ok = true };
                    break;
                case "ai-providers/delete-provider":
                    await _settingsService.DeleteProviderAsync(
                        ReadRequiredGuid(payload, "id", "providerId", "connectionId"),
                        cancellationToken);
                    response = new { type, requestId, ok = true };
                    break;
                case "ai-providers/fetch-models":
                {
                    var models = await _settingsService.FetchAndMergeRemoteModelsAsync(
                        ReadRequiredGuid(payload, "providerId", "connectionId", "id"),
                        cancellationToken);
                    response = new { type, requestId, models };
                    break;
                }
                case "ai-providers/check":
                {
                    var result = await _settingsService.CheckConnectivityAsync(
                        ReadRequiredGuid(payload, "providerId", "connectionId"),
                        ReadRequiredGuid(payload, "modelProfileId"),
                        cancellationToken);
                    response = new
                    {
                        type,
                        requestId,
                        ok = result.Ok,
                        latencyMs = result.LatencyMs,
                        error = result.ErrorMessage
                    };
                    break;
                }
                case "ai-providers/upsert-model":
                {
                    var model = await _settingsService.UpsertModelAsync(
                        ReadUpsertModelCommand(payload),
                        cancellationToken);
                    response = new { type, requestId, model };
                    break;
                }
                case "ai-providers/set-model-enabled":
                    await _settingsService.SetModelEnabledAsync(
                        ReadRequiredGuid(payload, "modelProfileId", "id"),
                        ReadRequiredBoolean(payload, "enabled"),
                        cancellationToken);
                    response = new { type, requestId, ok = true };
                    break;
                case "ai-providers/set-all-models-enabled":
                    await _settingsService.SetAllModelsEnabledAsync(
                        ReadRequiredGuid(payload, "providerId", "connectionId"),
                        ReadRequiredBoolean(payload, "enabled"),
                        cancellationToken);
                    response = new { type, requestId, ok = true };
                    break;
                case "ai-providers/delete-model":
                    await _settingsService.DeleteModelAsync(
                        ReadRequiredGuid(payload, "modelProfileId", "id"),
                        cancellationToken);
                    response = new { type, requestId, ok = true };
                    break;
                case "ai-providers/set-default-model":
                {
                    var scope = ReadRequiredString(payload, "scope");
                    var modelProfileId = ReadRequiredGuid(payload, "modelProfileId");
                    await _settingsService.SetDefaultModelAsync(
                        scope,
                        modelProfileId,
                        cancellationToken);
                    if (string.Equals(scope, AiModelSelectionScopes.DesktopDefault, StringComparison.Ordinal))
                    {
                        ModelSelectionChanged?.Invoke(modelProfileId);
                    }

                    response = new { type, requestId, ok = true };
                    break;
                }
                case "ai-providers/list-enabled-models":
                {
                    var models = await _settingsService.ListEnabledModelsAsync(cancellationToken);
                    var defaultModelProfileId = await _settingsService.GetDefaultModelAsync(
                        AiModelSelectionScopes.DesktopDefault,
                        cancellationToken);
                    ModelSelectionChanged?.Invoke(defaultModelProfileId);
                    response = new { type, requestId, models, defaultModelProfileId };
                    break;
                }
                default:
                    response = new { type, requestId, error = $"Unsupported AI provider message type '{type}'." };
                    break;
            }

            return response;
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

    private static SaveProviderCommand ReadSaveProviderCommand(JsonElement payload)
    {
        var id = ReadOptionalGuid(payload, "id", "connectionId");
        var catalogId = ReadRequiredString(payload, "catalogId");
        var name = ReadRequiredString(payload, "name");
        var endpointText = ReadOptionalString(payload, "endpoint") ?? ReadRequiredString(payload, "base");
        if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint))
        {
            throw new ArgumentException("Provider endpoint must be an absolute URI.");
        }

        var apiKey = payload.TryGetProperty("apiKey", out var apiKeyElement) &&
            apiKeyElement.ValueKind != JsonValueKind.Null
            ? apiKeyElement.GetString()
            : null;
        bool? enabled = payload.TryGetProperty("enabled", out var enabledElement) &&
            enabledElement.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? enabledElement.GetBoolean()
            : null;
        return new SaveProviderCommand(
            id,
            catalogId,
            name,
            endpoint,
            apiKey,
            ReadJsonDictionary(payload, "connectionOptions"),
            ReadOptionalEnum<AiProviderKind>(payload, "providerKind"),
            ReadOptionalEnum<AiProviderApiFormat>(payload, "apiFormat"),
            enabled);
    }

    private static UpsertModelCommand ReadUpsertModelCommand(JsonElement payload)
    {
        var sampling = payload.TryGetProperty("sampling", out var samplingElement) &&
            samplingElement.ValueKind == JsonValueKind.Object
            ? samplingElement.Deserialize<AiSamplingOptions>(JsonOptions)
            : null;
        bool? enabled = payload.TryGetProperty("enabled", out var enabledElement) &&
            enabledElement.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? enabledElement.GetBoolean()
            : null;

        return new UpsertModelCommand(
            ReadOptionalGuid(payload, "id", "modelProfileId"),
            ReadRequiredGuid(payload, "providerConnectionId", "providerId", "connectionId"),
            ReadRequiredString(payload, "name"),
            ReadRequiredEnum<AiProviderApiFormat>(payload, "apiFormat"),
            ReadRequiredString(payload, "model"),
            sampling,
            ReadJsonDictionary(payload, "modelOptions"),
            enabled);
    }

    private static IReadOnlyDictionary<string, JsonElement>? ReadJsonDictionary(
        JsonElement payload,
        string propertyName)
    {
        if (!payload.TryGetProperty(propertyName, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException($"Property '{propertyName}' must be a JSON object.");
        }

        return element.Deserialize<Dictionary<string, JsonElement>>(JsonOptions);
    }

    private static TEnum ReadRequiredEnum<TEnum>(JsonElement payload, string propertyName)
        where TEnum : struct, Enum
    {
        if (!payload.TryGetProperty(propertyName, out var element))
        {
            throw new ArgumentException($"Property '{propertyName}' is required.");
        }

        var result = element.Deserialize<TEnum>(JsonOptions);
        if (!Enum.IsDefined(result))
        {
            throw new ArgumentException($"Property '{propertyName}' has an unsupported value.");
        }

        return result;
    }

    private static TEnum? ReadOptionalEnum<TEnum>(JsonElement payload, string propertyName)
        where TEnum : struct, Enum
    {
        if (!payload.TryGetProperty(propertyName, out var element) ||
            element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        var result = element.Deserialize<TEnum>(JsonOptions);
        return Enum.IsDefined(result)
            ? result
            : throw new ArgumentException($"Property '{propertyName}' has an unsupported value.");
    }

    private static bool ReadRequiredBoolean(JsonElement payload, string propertyName)
    {
        if (!payload.TryGetProperty(propertyName, out var element) ||
            element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new ArgumentException($"Boolean property '{propertyName}' is required.");
        }

        return element.GetBoolean();
    }

    private static Guid ReadRequiredGuid(JsonElement payload, params string[] propertyNames)
        => ReadOptionalGuid(payload, propertyNames)
            ?? throw new ArgumentException($"GUID property '{propertyNames[0]}' is required.");

    private static Guid? ReadOptionalGuid(JsonElement payload, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (payload.TryGetProperty(propertyName, out var element) &&
                element.ValueKind == JsonValueKind.String &&
                Guid.TryParse(element.GetString(), out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static string ReadRequiredString(JsonElement payload, string propertyName)
        => ReadOptionalString(payload, propertyName)
            ?? throw new ArgumentException($"String property '{propertyName}' is required.");

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
