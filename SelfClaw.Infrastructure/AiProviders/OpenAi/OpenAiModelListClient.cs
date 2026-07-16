using System.Net.Http.Headers;
using System.Globalization;
using System.Text.Json;
using SelfClaw.Infrastructure.AiProviders.Http;
using SelfClaw.Infrastructure.AiProviders.Models;

namespace SelfClaw.Infrastructure.AiProviders.OpenAi;

internal sealed class OpenAiModelListClient
{
    private readonly AiProviderHttpClientProvider? _httpClientProvider;
    private readonly HttpClient? _httpClientOverride;

    public OpenAiModelListClient(AiProviderHttpClientProvider httpClientProvider)
    {
        _httpClientProvider = httpClientProvider;
    }

    internal OpenAiModelListClient(HttpClient httpClient)
    {
        _httpClientOverride = httpClient;
    }

    public async Task<IReadOnlyList<AiModelDescriptor>> ListModelsAsync(
        AiProviderConnection connection,
        IReadOnlyDictionary<string, string> secrets,
        CancellationToken cancellationToken = default)
    {
        var apiKey = AiProviderSecrets.RequireApiKey(connection.Name, secrets);
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildModelsUri(connection.Endpoint));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var httpClient = _httpClientOverride ?? _httpClientProvider!.GetNonStreamingClient(connection);
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await AiProviderHttpResponses.EnsureSuccessAsync(response, connection.Name, cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await ParseResponseAsync(stream, connection, cancellationToken);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            throw InvalidModelList(connection);
        }

        var models = new List<AiModelDescriptor>();
        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var modelId = idElement.GetString();
            if (string.IsNullOrWhiteSpace(modelId))
            {
                continue;
            }

            var displayName = ReadOptionalString(item, "name");
            var contextLength = ReadOptionalInt64(item, "context_length");
            var pricing = item.TryGetProperty("pricing", out var pricingElement) &&
                          pricingElement.ValueKind == JsonValueKind.Object
                ? pricingElement
                : default;

            models.Add(new AiModelDescriptor(
                modelId,
                displayName,
                contextLength,
                null,
                ReadPricePerMTok(pricing, "prompt"),
                ReadPricePerMTok(pricing, "completion"),
                ReadPricePerMTok(pricing, "input_cache_write"),
                ReadPricePerMTok(pricing, "input_cache_read")));
        }

        return models;
    }

    private static Uri BuildModelsUri(Uri endpoint)
        => new($"{endpoint.AbsoluteUri.TrimEnd('/')}/models", UriKind.Absolute);

    private static async Task<JsonDocument> ParseResponseAsync(
        Stream stream,
        AiProviderConnection connection,
        CancellationToken cancellationToken)
    {
        try
        {
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (JsonException exception)
        {
            throw InvalidModelList(connection, exception);
        }
    }

    private static InvalidDataException InvalidModelList(
        AiProviderConnection connection,
        Exception? innerException = null)
    {
        var message = string.Equals(connection.CatalogId, "custom", StringComparison.OrdinalIgnoreCase)
            ? $"Custom gateway '{connection.Name}' does not implement '/models' or returned a non-OpenAI model list."
            : $"Provider '{connection.Name}' returned a model list without an OpenAI-compatible 'data' array.";
        return new InvalidDataException(message, innerException);
    }

    private static string? ReadOptionalString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) &&
           property.ValueKind == JsonValueKind.String &&
           !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()
            : null;

    private static long? ReadOptionalInt64(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) &&
           property.ValueKind == JsonValueKind.Number &&
           property.TryGetInt64(out var value)
            ? value
            : null;

    private static decimal? ReadPricePerMTok(JsonElement pricing, string propertyName)
    {
        if (pricing.ValueKind != JsonValueKind.Object ||
            !pricing.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        decimal pricePerToken;
        if (property.ValueKind == JsonValueKind.String)
        {
            if (!decimal.TryParse(
                    property.GetString(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out pricePerToken))
            {
                return null;
            }
        }
        else if (property.ValueKind != JsonValueKind.Number || !property.TryGetDecimal(out pricePerToken))
        {
            return null;
        }

        return pricePerToken <= decimal.MaxValue / 1_000_000m
            ? pricePerToken * 1_000_000m
            : null;
    }
}
