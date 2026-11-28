using System.Text.Json;


using SelfClaw.LlmProvider.Connections.Dtos;
using SelfClaw.LlmProvider.Errors;
using SelfClaw.LlmProvider.Parameters.Dtos;
using SelfClaw.LlmProvider.Quirks.Dtos;
using SelfClaw.LlmProvider.Vendors.Dtos;

namespace SelfClaw.LlmProvider.Vendors.Implementations;

/// <summary>把厂商描述符 JSON 映射成对象。格式见 README。</summary>
internal static class LlmVendorJson
{
    internal static LlmVendorDescriptor Parse(JsonDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new LlmVendorCatalogException("A vendor descriptor must be a JSON object.");
        }

        var vendorId = ReadRequiredString(root, "vendorId");
        var displayName = ReadRequiredString(root, "displayName");
        var endpointText = ReadRequiredString(root, "defaultEndpoint");
        if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint))
        {
            throw new LlmVendorCatalogException($"Vendor '{vendorId}' has an invalid defaultEndpoint '{endpointText}'.");
        }

        if (!root.TryGetProperty("protocols", out var protocolsElement) ||
            protocolsElement.ValueKind != JsonValueKind.Object ||
            !protocolsElement.EnumerateObject().Any())
        {
            throw new LlmVendorCatalogException($"Vendor '{vendorId}' must declare at least one protocol.");
        }

        var protocols = new Dictionary<string, LlmVendorProtocol>(StringComparer.Ordinal);
        foreach (var entry in protocolsElement.EnumerateObject())
        {
            protocols[entry.Name] = ParseProtocol(vendorId, entry.Name, entry.Value);
        }

        var authKind = LlmCredentialKind.Bearer;
        string? authName = null;
        if (root.TryGetProperty("auth", out var auth) && auth.ValueKind == JsonValueKind.Object)
        {
            authKind = ParseAuthKind(vendorId, ReadOptionalString(auth, "kind"));
            authName = ReadOptionalString(auth, "name");
        }

        return new LlmVendorDescriptor(
            vendorId,
            displayName,
            endpoint,
            protocols,
            authKind,
            authName,
            ParseModelListing(root),
            ParseQuirks(root, "quirks"),
            ReadOptionalString(root, "subtitle"),
            ReadOptionalString(root, "accentColor"));
    }

    private static LlmVendorProtocol ParseProtocol(string vendorId, string protocolId, JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new LlmVendorCatalogException(
                $"Vendor '{vendorId}' protocol '{protocolId}' must be a JSON object.");
        }

        List<LlmVendorParameter>? parameters = null;
        if (element.TryGetProperty("parameters", out var parameterArray) &&
            parameterArray.ValueKind == JsonValueKind.Array)
        {
            parameters = parameterArray.EnumerateArray().Select(ParseParameter).ToList();
        }

        return new LlmVendorProtocol(parameters, ParseQuirks(element, "quirks"));
    }

    private static LlmVendorParameter ParseParameter(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new LlmVendorCatalogException("A parameter entry must be a JSON object.");
        }

        var path = ReadRequiredString(element, "path");
        var type = ReadOptionalString(element, "type") switch
        {
            "boolean" => LlmVendorValueType.Boolean,
            "integer" => LlmVendorValueType.Integer,
            "number" => LlmVendorValueType.Number,
            _ => LlmVendorValueType.String
        };

        List<string>? sources = null;
        if (element.TryGetProperty("sources", out var sourceArray) &&
            sourceArray.ValueKind == JsonValueKind.Array)
        {
            sources = sourceArray.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!)
                .Where(item => item.Length > 0)
                .ToList();
        }

        Dictionary<string, string>? map = null;
        if (element.TryGetProperty("map", out var mapElement) && mapElement.ValueKind == JsonValueKind.Object)
        {
            map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in mapElement.EnumerateObject())
            {
                map[pair.Name] = pair.Value.ValueKind == JsonValueKind.String
                    ? pair.Value.GetString() ?? string.Empty
                    : pair.Value.GetRawText();
            }
        }

        JsonElement? value = element.TryGetProperty("value", out var valueElement)
            ? valueElement.Clone()
            : null;

        var gate = element.TryGetProperty("gateLegacySources", out var gateElement) &&
                   gateElement.ValueKind == JsonValueKind.True;

        return new LlmVendorParameter(path, type, sources, map, value, gate);
    }

    private static List<LlmQuirkRef>? ParseQuirks(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var quirks = new List<LlmQuirkRef>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                quirks.Add(new LlmQuirkRef(item.GetString()!));
                continue;
            }

            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            Dictionary<string, string>? parameters = null;
            if (item.TryGetProperty("parameters", out var mapElement) &&
                mapElement.ValueKind == JsonValueKind.Object)
            {
                parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in mapElement.EnumerateObject())
                {
                    parameters[pair.Name] = pair.Value.ValueKind == JsonValueKind.String
                        ? pair.Value.GetString() ?? string.Empty
                        : pair.Value.GetRawText();
                }
            }

            quirks.Add(new LlmQuirkRef(ReadRequiredString(item, "id"), parameters));
        }

        return quirks.Count > 0 ? quirks : null;
    }

    private static LlmVendorModelListing? ParseModelListing(JsonElement root)
    {
        if (!root.TryGetProperty("modelListing", out var element) || element.ValueKind == JsonValueKind.False)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return new LlmVendorModelListing();
        }

        return new LlmVendorModelListing(
            ReadOptionalString(element, "protocol") ?? LlmVendorModelListing.OpenAi,
            ReadOptionalString(element, "path") ?? "models");
    }

    private static LlmCredentialKind ParseAuthKind(string vendorId, string? kind) => kind switch
    {
        null or "bearer" => LlmCredentialKind.Bearer,
        "header" => LlmCredentialKind.Header,
        "query" => LlmCredentialKind.Query,
        _ => throw new LlmVendorCatalogException(
            $"Vendor '{vendorId}' declares unsupported auth kind '{kind}'. Use bearer, header or query.")
    };

    private static string ReadRequiredString(JsonElement element, string propertyName)
        => ReadOptionalString(element, propertyName)
           ?? throw new LlmVendorCatalogException($"A vendor descriptor requires a non-empty '{propertyName}'.");

    private static string? ReadOptionalString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) &&
           property.ValueKind == JsonValueKind.String &&
           !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()
            : null;
}
