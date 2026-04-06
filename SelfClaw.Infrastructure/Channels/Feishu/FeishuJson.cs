using System.Net.Mime;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SelfClaw.Infrastructure.Channels.Feishu;

internal static class FeishuJson
{
    public static StringContent CreateJsonContent(JsonNode node)
    {
        return new StringContent(node.ToJsonString(), Encoding.UTF8, MediaTypeNames.Application.Json);
    }

    public static JsonElement Clone(JsonElement element) => element.Clone();

    public static JsonElement GetPropertyOrThrow(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            throw new InvalidOperationException($"Missing JSON property: {propertyName}");

        return value;
    }

    public static JsonElement? GetPropertyOrNull(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) ? value : null;
    }

    public static string GetString(JsonElement element, string propertyName, string fallback = "")
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return fallback;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? fallback,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => fallback
        };
    }

    public static string GetNestedString(JsonElement element, string fallback = "", params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (!current.TryGetProperty(segment, out current))
                return fallback;
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString() ?? fallback,
            JsonValueKind.Number => current.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => fallback
        };
    }

    public static int? GetInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return null;

        return TryGetInt32(value);
    }

    public static int? GetNestedInt32(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (!current.TryGetProperty(segment, out current))
                return null;
        }

        return TryGetInt32(current);
    }

    public static long? GetNestedInt64(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (!current.TryGetProperty(segment, out current))
                return null;
        }

        return TryGetInt64(current);
    }

    public static JsonElement? GetNestedProperty(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (!current.TryGetProperty(segment, out current))
                return null;
        }

        return current;
    }

    public static bool TryParseDocument(string json, out JsonDocument? document)
    {
        try
        {
            document = JsonDocument.Parse(json);
            return true;
        }
        catch
        {
            document = null;
            return false;
        }
    }

    public static async Task<JsonDocument> ReadJsonDocumentAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            return JsonDocument.Parse(content);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Expected JSON response but received: {content[..Math.Min(content.Length, 512)]}",
                ex);
        }
    }

    public static void EnsureFeishuSuccess(JsonElement root, string operationName)
    {
        var code = GetInt32(root, "code") ?? -1;
        if (code == 0)
            return;

        var message = GetString(root, "msg", "Unknown error");
        throw new InvalidOperationException($"Feishu {operationName} failed: {message} (code={code})");
    }

    public static long ParseTimestampMilliseconds(string? rawValue, long fallback)
    {
        if (!long.TryParse(rawValue, out var numeric))
            return fallback;

        return rawValue?.Length switch
        {
            <= 10 => numeric * 1000L,
            _ => numeric
        };
    }

    public static string NormalizeContentDispositionFileName(ContentDispositionHeaderValue? contentDisposition)
    {
        var raw = contentDisposition?.FileNameStar ?? contentDisposition?.FileName;
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        return raw.Trim().Trim('"');
    }

    private static int? TryGetInt32(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var numeric))
            return numeric;

        if (element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out numeric))
            return numeric;

        return null;
    }

    private static long? TryGetInt64(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var numeric))
            return numeric;

        if (element.ValueKind == JsonValueKind.String && long.TryParse(element.GetString(), out numeric))
            return numeric;

        return null;
    }
}


