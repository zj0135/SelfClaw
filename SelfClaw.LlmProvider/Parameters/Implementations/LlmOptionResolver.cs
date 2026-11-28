using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;


using SelfClaw.LlmProvider.Chat.Implementations;
using SelfClaw.LlmProvider.Connections.Dtos;
using SelfClaw.LlmProvider.Parameters.Dtos;
using SelfClaw.LlmProvider.Profiles.Dtos;

namespace SelfClaw.LlmProvider.Parameters.Implementations;

/// <summary>
/// 按厂商参数表求值。来源链顺序即优先级；产物是"参数路径 → JSON 值"。
/// </summary>
internal sealed class LlmOptionResolver
{
    private const string ReasoningEffortSource = "reasoning.effort";
    private const string EnableReasoningSource = "request.enableReasoning";
    private const string MultimodalSource = "limits.multimodal";
    private const string OptionPrefix = "option:";
    private const string ConnectionPrefix = "connection:";
    private const string ValueSource = "value";
    private const string AnyKey = "*";

    private readonly LlmConnection _connection;
    private readonly LlmModel _model;
    private readonly ILogger _logger;
    private readonly ModelOptionReader _options;

    internal LlmOptionResolver(LlmConnection connection, LlmModel model, ILogger? logger = null)
    {
        _connection = connection;
        _model = model;
        _logger = logger ?? NullLogger.Instance;
        _options = new ModelOptionReader(_logger, model.Options, model.ModelId);
    }

    internal IReadOnlyDictionary<string, JsonElement> Resolve(IReadOnlyList<LlmVendorParameter>? parameters)
    {
        var resolved = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (parameters is not { Count: > 0 })
        {
            return resolved;
        }

        var recognized = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in parameters)
        {
            CollectOptionKeys(parameter, recognized);
            if (TryResolveParameter(parameter, out var value))
            {
                resolved[parameter.Path] = value;
            }
        }

        recognized.Add(LlmChatOptions.MaxOutputTokensKey);
        _options.LogUnknown(recognized);
        return resolved;
    }

    private static void CollectOptionKeys(LlmVendorParameter parameter, HashSet<string> recognized)
    {
        foreach (var source in parameter.Sources ?? [])
        {
            if (source.StartsWith(OptionPrefix, StringComparison.Ordinal))
            {
                recognized.Add(source[OptionPrefix.Length..]);
            }
        }
    }

    private bool TryResolveParameter(LlmVendorParameter parameter, out JsonElement value)
    {
        value = default;
        if (parameter.Value is { } constant)
        {
            value = ApplyMap(constant, parameter);
            return true;
        }

        foreach (var source in parameter.Sources ?? [])
        {
            if (IsLegacySource(source) && parameter.GateLegacySources && _model.HasTypedConfiguration)
            {
                continue;
            }

            if (!TryReadSource(source, out var raw))
            {
                continue;
            }

            value = ApplyMap(raw, parameter);
            return true;
        }

        return false;
    }

    // 手写线参数与按配置派生的开关属于"旧来源"：界面上设过类型化配置后，它们不许再参与。
    private static bool IsLegacySource(string source) =>
        source.StartsWith(OptionPrefix, StringComparison.Ordinal) || source == EnableReasoningSource;

    private bool TryReadSource(string source, out JsonElement value)
    {
        value = default;
        if (source == ReasoningEffortSource)
        {
            if (string.IsNullOrWhiteSpace(_model.ReasoningEffort))
            {
                return false;
            }

            value = JsonSerializer.SerializeToElement(_model.ReasoningEffort);
            return true;
        }

        if (source == EnableReasoningSource)
        {
            value = JsonSerializer.SerializeToElement(
                _model.ReasoningEffort is { Length: > 0 } effort &&
                !string.Equals(effort, "none", StringComparison.OrdinalIgnoreCase));
            return true;
        }

        if (source == MultimodalSource)
        {
            value = JsonSerializer.SerializeToElement(_model.Limits.IsMultimodal);
            return true;
        }

        if (source.StartsWith(OptionPrefix, StringComparison.Ordinal))
        {
            var key = source[OptionPrefix.Length..];
            if (_model.Options is not { } modelOptions || !modelOptions.TryGetValue(key, out var element))
            {
                return false;
            }

            value = element.Clone();
            return true;
        }

        if (source.StartsWith(ConnectionPrefix, StringComparison.Ordinal))
        {
            var key = source[ConnectionPrefix.Length..];
            if (_connection.Options is not { } options || !options.TryGetValue(key, out var element))
            {
                return false;
            }

            value = element.Clone();
            return true;
        }

        if (source == ValueSource)
        {
            return false;
        }

        _logger.LogDebug(
            "Ignoring unknown parameter source '{Source}' for model '{Model}'.", source, _model.ModelId);
        return false;
    }

    private static JsonElement ApplyMap(JsonElement raw, LlmVendorParameter parameter)
    {
        var mapped = MapValue(raw, parameter);
        return ConvertTo(mapped, parameter.Type);
    }

    private static JsonElement? MapValue(JsonElement raw, LlmVendorParameter parameter)
    {
        if (parameter.Map is not { Count: > 0 } map)
        {
            return raw;
        }

        var key = KeyOf(raw);
        if (map.TryGetValue(key, out var exact))
        {
            return JsonSerializer.SerializeToElement(exact);
        }

        if (map.TryGetValue(AnyKey, out var any) && key.Length > 0)
        {
            return JsonSerializer.SerializeToElement(any);
        }

        return null;
    }

    private static string KeyOf(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => value.GetRawText(),
        _ => value.GetRawText()
    };

    private static JsonElement ConvertTo(JsonElement? value, LlmVendorValueType type)
    {
        if (value is not { } element)
        {
            return JsonSerializer.SerializeToElement<object?>(null);
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            var text = element.GetString() ?? string.Empty;
            return text.Length > 0 && type != LlmVendorValueType.String
                ? ConvertText(text, type)
                : element;
        }

        return element;
    }

    private static JsonElement ConvertText(string text, LlmVendorValueType type) => type switch
    {
        LlmVendorValueType.Boolean when bool.TryParse(text, out var flag) => JsonSerializer.SerializeToElement(flag),
        LlmVendorValueType.Integer when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            => JsonSerializer.SerializeToElement(number),
        LlmVendorValueType.Number when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var real)
            => JsonSerializer.SerializeToElement(real),
        _ => JsonSerializer.SerializeToElement(text)
    };
}
