using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.AI;
using SelfClaw.Infrastructure.AiProviders.Abstractions;
using SelfClaw.Infrastructure.AiProviders.Http;
using SelfClaw.Infrastructure.AiProviders.Models;
using SelfClaw.Infrastructure.AiProviders.OpenAi;

namespace SelfClaw.Infrastructure.AiProviders.Gemini;

internal sealed class GeminiProviderAdapter : IAiProviderAdapter
{
    private readonly AiProviderHttpClientProvider _httpClientProvider;
    private readonly OpenAiProviderAdapter _openAiCompatibilityAdapter;

    public GeminiProviderAdapter(AiProviderHttpClientProvider httpClientProvider)
    {
        _httpClientProvider = httpClientProvider;
        _openAiCompatibilityAdapter = new OpenAiProviderAdapter(
            AiProviderKind.OpenAI,
            httpClientProvider: httpClientProvider);
    }

    public AiProviderKind ProviderKind => AiProviderKind.GoogleGemini;

    public bool SupportsModelListing => true;

    public bool SupportsApiFormat(AiProviderApiFormat apiFormat)
        => apiFormat == AiProviderApiFormat.OpenAIChatCompletions;

    public IChatClient CreateChatClient(AiProviderClientRequest request)
    {
        EnsureSupported(request);
        return _openAiCompatibilityAdapter.CreateChatClient(CreateCompatibilityRequest(request));
    }

    public ChatOptions CreateChatOptions(AiProviderClientRequest request)
    {
        EnsureSupported(request);
        return _openAiCompatibilityAdapter.CreateChatOptions(CreateCompatibilityRequest(request));
    }

    public async Task<IReadOnlyList<AiModelDescriptor>> ListModelsAsync(
        AiProviderConnection connection,
        IReadOnlyDictionary<string, string> secrets,
        CancellationToken cancellationToken = default)
    {
        var apiKey = ResolveApiKey(connection, secrets);
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildNativeModelsUri(connection.Endpoint));
        request.Headers.Add("x-goog-api-key", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var httpClient = _httpClientProvider.GetNonStreamingClient(connection);
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await AiProviderHttpResponses.EnsureSuccessAsync(response, connection.Name, cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("models", out var modelsElement) ||
            modelsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                $"Provider '{connection.Name}' returned a Gemini model list without a 'models' array.");
        }

        var models = new List<AiModelDescriptor>();
        foreach (var item in modelsElement.EnumerateArray())
        {
            if (!SupportsGenerateContent(item) || !TryReadString(item, "name", out var name))
            {
                continue;
            }

            TryReadString(item, "displayName", out var displayName);
            models.Add(new AiModelDescriptor(
                NormalizeModelId(name),
                displayName,
                ReadOptionalInt64(item, "inputTokenLimit"),
                ReadOptionalInt64(item, "outputTokenLimit"),
                null,
                null,
                null,
                null));
        }

        return models;
    }

    private static AiProviderClientRequest CreateCompatibilityRequest(AiProviderClientRequest request)
        => request with
        {
            Connection = request.Connection with
            {
                Endpoint = BuildOpenAiEndpoint(request.Connection.Endpoint),
                AuthKind = AiProviderAuthKind.ApiKey
            }
        };

    private static Uri BuildOpenAiEndpoint(Uri endpoint)
    {
        var builder = new UriBuilder(endpoint);
        var path = builder.Path.TrimEnd('/');
        builder.Path = path.EndsWith("/v1beta/openai", StringComparison.OrdinalIgnoreCase)
            ? $"{path}/"
            : path.EndsWith("/v1beta", StringComparison.OrdinalIgnoreCase)
                ? $"{path}/openai/"
                : $"{path}/v1beta/openai/";
        builder.Query = string.Empty;
        return builder.Uri;
    }

    private static Uri BuildNativeModelsUri(Uri endpoint)
    {
        var builder = new UriBuilder(endpoint);
        var path = builder.Path.TrimEnd('/');
        if (path.EndsWith("/v1beta/openai", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^"/openai".Length];
        }

        builder.Path = path.EndsWith("/v1beta", StringComparison.OrdinalIgnoreCase)
            ? $"{path}/models"
            : $"{path}/v1beta/models";
        builder.Query = string.Empty;
        return builder.Uri;
    }

    private static bool SupportsGenerateContent(JsonElement item)
        => item.TryGetProperty("supportedGenerationMethods", out var methods) &&
           methods.ValueKind == JsonValueKind.Array &&
           methods.EnumerateArray().Any(method =>
               method.ValueKind == JsonValueKind.String &&
               string.Equals(method.GetString(), "generateContent", StringComparison.Ordinal));

    private static string NormalizeModelId(string name)
        => name.StartsWith("models/", StringComparison.Ordinal) ? name["models/".Length..] : name;

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

    private static long? ReadOptionalInt64(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) &&
           property.ValueKind == JsonValueKind.Number &&
           property.TryGetInt64(out var value)
            ? value
            : null;

    private static string ResolveApiKey(
        AiProviderConnection connection,
        IReadOnlyDictionary<string, string> secrets)
        => AiProviderSecrets.RequireApiKey(connection.Name, secrets);

    private void EnsureSupported(AiProviderClientRequest request)
    {
        if (!SupportsApiFormat(request.Profile.ApiFormat))
        {
            throw new NotSupportedException(
                $"Google Gemini provider does not support API format '{request.Profile.ApiFormat}' " +
                $"for profile '{request.Profile.Name}'.");
        }
    }
}
