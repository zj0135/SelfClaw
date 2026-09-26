using System.Text.Json;

namespace SelfClaw.Infrastructure.Agents.Direct.Hooks;

/// <summary>
/// The lightweight rewrite validation of §9.3: an object within 64 KiB, every schema-required
/// property present, declared property types respected, and no extra property when the schema
/// forbids them. Anything deeper is left to the tool binding itself.
/// </summary>
internal static class HookArgumentsValidator
{
    internal static bool TryValidate(JsonElement arguments, JsonElement schema, out string? error)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            error = "updatedArguments must be a JSON object.";
            return false;
        }

        if (JsonSerializer.SerializeToUtf8Bytes(arguments, HookProtocol.Options).Length > HookProtocol.MaximumJsonValueBytes)
        {
            error = "updatedArguments exceeds 64 KiB.";
            return false;
        }

        if (!HasRequiredProperties(arguments, schema, out error))
        {
            return false;
        }

        if (!HasValidPropertyTypes(arguments, schema, out error))
        {
            return false;
        }

        if (!AllowsAdditionalProperties(arguments, schema, out error))
        {
            return false;
        }

        error = null;
        return true;
    }

    private static bool HasRequiredProperties(JsonElement arguments, JsonElement schema, out string? error)
    {
        if (schema.ValueKind == JsonValueKind.Object &&
            schema.TryGetProperty("required", out var required) &&
            required.ValueKind == JsonValueKind.Array)
        {
            foreach (var name in required.EnumerateArray())
            {
                if (name.ValueKind == JsonValueKind.String && !arguments.TryGetProperty(name.GetString()!, out _))
                {
                    error = $"updatedArguments is missing required property '{name.GetString()}'.";
                    return false;
                }
            }
        }

        error = null;
        return true;
    }

    private static bool HasValidPropertyTypes(JsonElement arguments, JsonElement schema, out string? error)
    {
        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("properties", out var properties) ||
            properties.ValueKind != JsonValueKind.Object)
        {
            error = null;
            return true;
        }

        foreach (var property in properties.EnumerateObject())
        {
            if (!arguments.TryGetProperty(property.Name, out var value) ||
                property.Value.ValueKind != JsonValueKind.Object ||
                !property.Value.TryGetProperty("type", out var type))
            {
                continue;
            }

            if (!MatchesDeclaredType(value, type))
            {
                error = $"updatedArguments property '{property.Name}' does not match the declared type.";
                return false;
            }
        }

        error = null;
        return true;
    }

    private static bool AllowsAdditionalProperties(JsonElement arguments, JsonElement schema, out string? error)
    {
        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("additionalProperties", out var additional) ||
            additional.ValueKind != JsonValueKind.False ||
            !schema.TryGetProperty("properties", out var properties) ||
            properties.ValueKind != JsonValueKind.Object)
        {
            error = null;
            return true;
        }

        var declared = properties.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var property in arguments.EnumerateObject())
        {
            if (!declared.Contains(property.Name))
            {
                error = $"updatedArguments contains undeclared property '{property.Name}'.";
                return false;
            }
        }

        error = null;
        return true;
    }

    private static bool MatchesDeclaredType(JsonElement value, JsonElement type)
    {
        if (type.ValueKind == JsonValueKind.String)
        {
            return MatchesTypeName(value, type.GetString()!);
        }

        if (type.ValueKind != JsonValueKind.Array)
        {
            return true;
        }

        return type.EnumerateArray().Any(item =>
            item.ValueKind == JsonValueKind.String && MatchesTypeName(value, item.GetString()!));
    }

    private static bool MatchesTypeName(JsonElement value, string type)
        => type switch
        {
            "string" => value.ValueKind == JsonValueKind.String,
            "number" => value.ValueKind == JsonValueKind.Number,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "null" => value.ValueKind == JsonValueKind.Null,
            _ => true
        };
}
