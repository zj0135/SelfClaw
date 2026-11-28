using System.Text.Json;
using Microsoft.Extensions.Logging;


using SelfClaw.LlmProvider.Chat.Implementations;

namespace SelfClaw.LlmProvider.Parameters.Implementations;

/// <summary>
/// 以"告警并跳过"的统一策略读取自由选项袋：键不存在时静默跳过，存在但类型不符时记一条告警。
/// </summary>
internal sealed class ModelOptionReader
{
    private readonly ILogger _logger;
    private readonly IReadOnlyDictionary<string, JsonElement> _options;
    private readonly string _modelId;

    internal ModelOptionReader(
        ILogger logger,
        IReadOnlyDictionary<string, JsonElement>? options,
        string modelId)
    {
        _logger = logger;
        _options = options ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        _modelId = modelId;
    }

    internal bool TryReadString(string key, out string value)
    {
        value = string.Empty;
        if (!_options.TryGetValue(key, out var element))
        {
            return false;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            LogWrongKind(key, "a string", element.ValueKind);
            return false;
        }

        value = element.GetString() ?? string.Empty;
        return true;
    }

    internal bool TryReadBool(string key, out bool value)
    {
        value = false;
        if (!_options.TryGetValue(key, out var element))
        {
            return false;
        }

        if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            LogWrongKind(key, "a boolean", element.ValueKind);
            return false;
        }

        value = element.GetBoolean();
        return true;
    }

    internal bool TryReadInt(string key, out int value)
    {
        value = 0;
        if (!_options.TryGetValue(key, out var element))
        {
            return false;
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out value))
        {
            LogWrongKind(key, "an integer", element.ValueKind);
            value = 0;
            return false;
        }

        return true;
    }

    /// <summary>对每个未被任何参数表认领的键记一条 debug：它们永远不会上线。</summary>
    internal void LogUnknown(IReadOnlySet<string> recognizedKeys)
    {
        foreach (var key in _options.Keys)
        {
            if (recognizedKeys.Contains(key) || LlmChatOptions.IsHostOwnedOption(key))
            {
                continue;
            }

            _logger.LogDebug("Ignoring unknown model option '{Key}' for model '{Model}'.", key, _modelId);
        }
    }

    private void LogWrongKind(string key, string expected, JsonValueKind actual) =>
        _logger.LogWarning(
            "Ignoring model option '{Key}' for model '{Model}': expected {Expected} but got {Kind}.",
            key,
            _modelId,
            expected,
            actual);
}
