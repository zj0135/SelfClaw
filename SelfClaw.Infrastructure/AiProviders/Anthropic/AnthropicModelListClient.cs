using System.Net.Http.Headers;
using System.Text.Json;
using SelfClaw.Infrastructure.AiProviders.Http;
using SelfClaw.Infrastructure.AiProviders.Models;

namespace SelfClaw.Infrastructure.AiProviders.Anthropic;

internal sealed class AnthropicModelListClient
{
    private const string AnthropicVersion = "2023-06-01";
    private readonly AiProviderHttpClientProvider? _httpClientProvider;
    private readonly HttpClient? _httpClientOverride;

    public AnthropicModelListClient(AiProviderHttpClientProvider httpClientProvider)
    {
        _httpClientProvider = httpClientProvider;
    }

    internal AnthropicModelListClient(HttpClient httpClient)
    {
        _httpClientOverride = httpClient;
    }

    public async Task<IReadOnlyList<AiModelDescriptor>> ListModelsAsync(
        AiProviderConnection connection,
        IReadOnlyDictionary<string, string> secrets,
        CancellationToken cancellationToken = default)
    {
        var apiKey = AiProviderSecrets.RequireApiKey(connection.Name, secrets);
        var models = new List<AiModelDescriptor>();
        string? afterId = null;
        var visitedPageIds = new HashSet<string>(StringComparer.Ordinal);

        do
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildModelsUri(connection.Endpoint, afterId));
            request.Headers.Add("x-api-key", apiKey);
            request.Headers.Add("anthropic-version", AnthropicVersion);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var httpClient = _httpClientOverride ?? _httpClientProvider!.GetNonStreamingClient(connection);
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            await AiProviderHttpResponses.EnsureSuccessAsync(response, connection.Name, cancellationToken);

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException(
                    $"Provider '{connection.Name}' returned an Anthropic model list without a 'data' array.");
            }

            foreach (var item in data.EnumerateArray())
            {
                if (!TryReadString(item, "id", out var modelId))
                {
                    continue;
                }

                TryReadString(item, "display_name", out var displayName);
                models.Add(new AiModelDescriptor(
                    modelId,
                    displayName,
                    ReadOptionalInt64(item, "max_input_tokens", "context_length", "context_window"),
                    ReadOptionalInt64(item, "max_tokens", "max_output_tokens"),
                    null,
                    null,
                    null,
                    null));
            }

            var hasMore = root.TryGetProperty("has_more", out var hasMoreElement) &&
                hasMoreElement.ValueKind == JsonValueKind.True;
            if (!hasMore)
            {
                afterId = null;
                continue;
            }

            if (!TryReadString(root, "last_id", out var lastId) || !visitedPageIds.Add(lastId))
            {
                throw new InvalidDataException(
                    $"Provider '{connection.Name}' returned an invalid Anthropic model-list pagination cursor.");
            }

            afterId = lastId;
        }
        while (afterId is not null);

        return models;
    }

    private static Uri BuildModelsUri(Uri endpoint, string? afterId)
    {
        var builder = new UriBuilder(endpoint);
        var basePath = builder.Path.TrimEnd('/');
        builder.Path = basePath.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? $"{basePath}/models"
            : $"{basePath}/v1/models";
        builder.Query = afterId is null
            ? "limit=100"
            : $"limit=100&after_id={Uri.EscapeDataString(afterId)}";
        return builder.Uri;
    }

    private static bool TryReadString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

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

    private static string ResolveApiKey(
        AiProviderConnection connection,
        IReadOnlyDictionary<string, string> secrets)
        => AiProviderSecrets.RequireApiKey(connection.Name, secrets);
}
