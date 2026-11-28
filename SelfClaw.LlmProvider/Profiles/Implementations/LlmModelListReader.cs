using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;


using SelfClaw.LlmProvider.Connections.Dtos;
using SelfClaw.LlmProvider.Profiles.Dtos;
using SelfClaw.LlmProvider.Transport.Implementations;
using SelfClaw.LlmProvider.Vendors.Dtos;

namespace SelfClaw.LlmProvider.Profiles.Implementations;

/// <summary>模型清单的两种线格式。厂商描述符只选择用哪一种，解析逻辑是代码。</summary>
internal static class LlmModelListReader
{
    private const string AnthropicVersion = "2023-06-01";
    private const int PageSize = 100;

    internal static Task<IReadOnlyList<LlmModelDescriptor>> ReadAsync(
        HttpClient httpClient,
        LlmConnection connection,
        LlmVendorDescriptor vendor,
        LlmVendorModelListing listing,
        CancellationToken cancellationToken) =>
        listing.Protocol == LlmVendorModelListing.Anthropic
            ? ReadAnthropicAsync(httpClient, connection, listing, cancellationToken)
            : ReadOpenAiAsync(httpClient, connection, listing, cancellationToken);

    private static async Task<IReadOnlyList<LlmModelDescriptor>> ReadOpenAiAsync(
        HttpClient httpClient,
        LlmConnection connection,
        LlmVendorModelListing listing,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(connection.Endpoint, listing.Path, null));
        ApplyOpenAiCredential(request, connection);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        await ProviderHttpResponses.EnsureSuccessAsync(response, connection.VendorId, cancellationToken)
            .ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await ParseAsync(stream, connection, "OpenAI", cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                $"Provider '{connection.VendorId}' returned a model list without an OpenAI-compatible 'data' array.");
        }

        var models = new List<LlmModelDescriptor>();
        foreach (var item in data.EnumerateArray())
        {
            if (!TryReadString(item, "id", out var modelId))
            {
                continue;
            }

            var pricing = item.TryGetProperty("pricing", out var pricingElement) &&
                          pricingElement.ValueKind == JsonValueKind.Object
                ? pricingElement
                : default;

            models.Add(new LlmModelDescriptor(
                modelId,
                ReadOptionalString(item, "name"),
                ReadOptionalInt64(item, "context_length"),
                ReadOptionalInt64(item, "max_output_tokens") ?? ReadOptionalInt64(item, "max_completion_tokens"),
                ReadPricePerMTok(pricing, "prompt"),
                ReadPricePerMTok(pricing, "completion"),
                ReadPricePerMTok(pricing, "input_cache_write"),
                ReadPricePerMTok(pricing, "input_cache_read")));
        }

        return models;
    }

    private static async Task<IReadOnlyList<LlmModelDescriptor>> ReadAnthropicAsync(
        HttpClient httpClient,
        LlmConnection connection,
        LlmVendorModelListing listing,
        CancellationToken cancellationToken)
    {
        var models = new List<LlmModelDescriptor>();
        string? afterId = null;
        var visited = new HashSet<string>(StringComparer.Ordinal);

        do
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, BuildUri(connection.Endpoint, listing.Path, afterId));
            request.Headers.TryAddWithoutValidation("x-api-key", connection.Credential.Value);
            request.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            await ProviderHttpResponses.EnsureSuccessAsync(response, connection.VendorId, cancellationToken)
                .ConfigureAwait(false);

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await ParseAsync(stream, connection, "Anthropic", cancellationToken)
                .ConfigureAwait(false);
            var root = document.RootElement;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException(
                    $"Provider '{connection.VendorId}' returned an Anthropic model list without a 'data' array.");
            }

            foreach (var item in data.EnumerateArray())
            {
                if (!TryReadString(item, "id", out var modelId))
                {
                    continue;
                }

                TryReadString(item, "display_name", out var displayName);
                models.Add(new LlmModelDescriptor(
                    modelId,
                    displayName,
                    ReadOptionalInt64(item, "max_input_tokens", "context_length", "context_window"),
                    ReadOptionalInt64(item, "max_tokens", "max_output_tokens")));
            }

            var hasMore = root.TryGetProperty("has_more", out var hasMoreElement) &&
                          hasMoreElement.ValueKind == JsonValueKind.True;
            if (!hasMore)
            {
                break;
            }

            if (!TryReadString(root, "last_id", out var lastId) || !visited.Add(lastId))
            {
                throw new InvalidDataException(
                    $"Provider '{connection.VendorId}' returned an invalid Anthropic model-list pagination cursor.");
            }

            afterId = lastId;
        }
        while (afterId is not null);

        return models;
    }

    private static void ApplyOpenAiCredential(HttpRequestMessage request, LlmConnection connection)
    {
        switch (connection.Credential.Kind)
        {
            case LlmCredentialKind.Bearer:
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.Credential.Value);
                break;
            case LlmCredentialKind.Header when connection.Credential.Name is { Length: > 0 } headerName:
                request.Headers.TryAddWithoutValidation(headerName, connection.Credential.Value);
                break;
            case LlmCredentialKind.Query when connection.Credential.Name is { Length: > 0 } queryName:
                request.RequestUri = BuildUri(
                    request.RequestUri!, connection.Credential.Name, connection.Credential.Value);
                break;
        }
    }

    private static Uri BuildUri(Uri endpoint, string path, string? afterId)
    {
        var builder = new UriBuilder(
            $"{endpoint.AbsoluteUri.TrimEnd('/')}/{path.TrimStart('/')}");
        builder.Query = afterId is null
            ? $"limit={PageSize}"
            : $"limit={PageSize}&after_id={Uri.EscapeDataString(afterId)}";
        return builder.Uri;
    }

    private static async Task<JsonDocument> ParseAsync(
        Stream stream,
        LlmConnection connection,
        string protocolName,
        CancellationToken cancellationToken)
    {
        try
        {
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Provider '{connection.VendorId}' returned a non-{protocolName} model list.", exception);
        }
    }

    private static bool TryReadString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return value.Length > 0;
    }

    private static string? ReadOptionalString(JsonElement element, string propertyName) =>
        TryReadString(element, propertyName, out var value) ? value : null;

    private static long? ReadOptionalInt64(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (element.TryGetProperty(propertyName, out var property) &&
                property.ValueKind == JsonValueKind.Number &&
                property.TryGetInt64(out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static decimal? ReadPricePerMTok(JsonElement pricing, string propertyName)
    {
        if (pricing.ValueKind != JsonValueKind.Object || !pricing.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        decimal pricePerToken;
        if (property.ValueKind == JsonValueKind.String)
        {
            if (!decimal.TryParse(
                    property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out pricePerToken))
            {
                return null;
            }
        }
        else if (property.ValueKind != JsonValueKind.Number || !property.TryGetDecimal(out pricePerToken))
        {
            return null;
        }

        return pricePerToken <= decimal.MaxValue / 1_000_000m ? pricePerToken * 1_000_000m : null;
    }
}
