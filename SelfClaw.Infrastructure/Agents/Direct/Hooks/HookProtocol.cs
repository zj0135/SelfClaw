using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SelfClaw.Infrastructure.Agents.Direct.Hooks.Models;

namespace SelfClaw.Infrastructure.Agents.Direct.Hooks;

internal static class HookProtocol
{
    internal const int MaximumOutputBytes = 64 * 1024;
    internal const int MaximumJsonValueBytes = 64 * 1024;

    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    internal static byte[] Serialize<TPayload>(TPayload payload)
        => JsonSerializer.SerializeToUtf8Bytes(payload, Options);

    /// <summary>
    /// Truncates on a UTF-8 boundary so a multi-byte character is never split, which would otherwise
    /// make the payload contain an invalid sequence.
    /// </summary>
    internal static (string Text, bool Truncated) Truncate(string value, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (Encoding.UTF8.GetByteCount(value) <= maxBytes)
        {
            return (value, false);
        }

        var builder = new StringBuilder(value.Length);
        var written = 0;
        for (var index = 0; index < value.Length; index++)
        {
            var length = char.IsHighSurrogate(value[index]) &&
                         index + 1 < value.Length &&
                         char.IsLowSurrogate(value[index + 1])
                ? 2
                : 1;
            var span = value.AsSpan(index, length);
            var bytes = Encoding.UTF8.GetByteCount(span);
            if (written + bytes > maxBytes)
            {
                break;
            }

            builder.Append(span);
            written += bytes;
            index += length - 1;
        }

        return (builder.ToString(), true);
    }

    /// <summary>
    /// Keeps a JSON value verbatim while it fits, and otherwise replaces it with the truncated UTF-8
    /// text as a JSON string so the model can still see the beginning of the arguments.
    /// </summary>
    internal static (JsonElement Value, bool Truncated) TruncateJson(JsonElement value)
    {
        var raw = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        if (raw.Length <= MaximumJsonValueBytes)
        {
            return (value, false);
        }

        var text = Encoding.UTF8.GetString(raw);
        var (truncated, _) = Truncate(text, MaximumJsonValueBytes);
        return (JsonSerializer.SerializeToElement(truncated, Options), true);
    }

    /// <summary>
    /// Parses a hook's stdout into an event decision. An empty stdout means "no decision", which every
    /// event treats as its default (continue, or no feedback).
    /// </summary>
    internal static HookDecisionParse<TDecision> Parse<TDecision>(
        string stdout,
        IReadOnlyCollection<string> allowedDecisions)
        where TDecision : class
    {
        ArgumentNullException.ThrowIfNull(stdout);
        var text = stdout.TrimStart('\uFEFF');
        if (string.IsNullOrWhiteSpace(text))
        {
            return new HookDecisionParse<TDecision>(null, null, null);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException exception)
        {
            return new HookDecisionParse<TDecision>(null, "invalidOutput", exception.Message);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new HookDecisionParse<TDecision>(null, "invalidOutput", "Hook stdout must be a JSON object.");
            }

            if (allowedDecisions.Count > 0 &&
                document.RootElement.TryGetProperty("decision", out var decision) &&
                decision.ValueKind != JsonValueKind.Null)
            {
                if (decision.ValueKind != JsonValueKind.String ||
                    !allowedDecisions.Contains(decision.GetString()!))
                {
                    return new HookDecisionParse<TDecision>(
                        null, "invalidDecision", "Hook stdout declared an unsupported decision.");
                }
            }
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<TDecision>(text, Options);
            return new HookDecisionParse<TDecision>(parsed, null, null);
        }
        catch (JsonException exception)
        {
            return new HookDecisionParse<TDecision>(null, "invalidDecision", exception.Message);
        }
    }
}
