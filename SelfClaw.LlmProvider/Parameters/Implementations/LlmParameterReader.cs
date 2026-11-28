using System.Text.Json;

namespace SelfClaw.LlmProvider.Parameters.Implementations;

/// <summary>从已解析的参数表里按 JSON 路径取值。</summary>
internal static class LlmParameterReader
{
    internal static bool TryGetString(
        IReadOnlyDictionary<string, JsonElement> parameters,
        string path,
        out string value)
    {
        value = string.Empty;
        if (!parameters.TryGetValue(path, out var element))
        {
            return false;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString() ?? string.Empty;
        return value.Length > 0;
    }

    internal static bool TryGetBool(
        IReadOnlyDictionary<string, JsonElement> parameters,
        string path,
        out bool value)
    {
        value = false;
        if (!parameters.TryGetValue(path, out var element))
        {
            return false;
        }

        if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = element.GetBoolean();
        return true;
    }

    internal static bool TryGetInt(
        IReadOnlyDictionary<string, JsonElement> parameters,
        string path,
        out int value)
    {
        value = 0;
        return parameters.TryGetValue(path, out var element) &&
               element.ValueKind == JsonValueKind.Number &&
               element.TryGetInt32(out value);
    }
}
