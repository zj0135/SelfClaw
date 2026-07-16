using Microsoft.Extensions.AI;
using OllamaSharp;
using SelfClaw.Infrastructure.AiProviders.Abstractions;
using SelfClaw.Infrastructure.AiProviders.Http;
using SelfClaw.Infrastructure.AiProviders.Models;
using SelfClaw.Infrastructure.AiProviders.OpenAi;

namespace SelfClaw.Infrastructure.AiProviders.Ollama;

internal sealed class OllamaProviderAdapter : IAiProviderAdapter
{
    private readonly AiProviderHttpClientProvider _httpClientProvider;
    private readonly OpenAiProviderAdapter _openAiCompatibilityAdapter;

    public OllamaProviderAdapter(AiProviderHttpClientProvider httpClientProvider)
    {
        _httpClientProvider = httpClientProvider;
        _openAiCompatibilityAdapter = new OpenAiProviderAdapter(
            AiProviderKind.OpenAI,
            httpClientProvider: httpClientProvider);
    }

    public AiProviderKind ProviderKind => AiProviderKind.Ollama;

    public bool SupportsModelListing => true;

    public bool SupportsApiFormat(AiProviderApiFormat apiFormat)
        => apiFormat is AiProviderApiFormat.OllamaNative or AiProviderApiFormat.OpenAIChatCompletions;

    public IChatClient CreateChatClient(AiProviderClientRequest request)
        => request.Profile.ApiFormat switch
        {
            AiProviderApiFormat.OllamaNative => new OllamaApiClient(
                _httpClientProvider.GetStreamingClient(request.Connection),
                request.Profile.Model),
            AiProviderApiFormat.OpenAIChatCompletions =>
                _openAiCompatibilityAdapter.CreateChatClient(CreateOpenAiCompatibilityRequest(request)),
            _ => throw UnsupportedFormat(request)
        };

    public ChatOptions CreateChatOptions(AiProviderClientRequest request)
    {
        if (request.Profile.ApiFormat == AiProviderApiFormat.OpenAIChatCompletions)
        {
            return _openAiCompatibilityAdapter.CreateChatOptions(CreateOpenAiCompatibilityRequest(request));
        }

        if (request.Profile.ApiFormat != AiProviderApiFormat.OllamaNative)
        {
            throw UnsupportedFormat(request);
        }

        var sampling = request.Profile.Sampling;
        return new ChatOptions
        {
            ModelId = request.Profile.Model,
            Temperature = sampling.TemperatureEnabled ? (float)sampling.Temperature : null,
            TopP = sampling.TopPEnabled ? (float)sampling.TopP : null,
            ToolMode = request.Tools.Count > 0 ? ChatToolMode.Auto : ChatToolMode.None,
            Tools = request.Tools.Count > 0 ? request.Tools.ToList() : null
        };
    }

    public async Task<IReadOnlyList<AiModelDescriptor>> ListModelsAsync(
        AiProviderConnection connection,
        IReadOnlyDictionary<string, string> secrets,
        CancellationToken cancellationToken = default)
    {
        using var client = new OllamaApiClient(
            _httpClientProvider.GetNonStreamingClient(connection),
            defaultModel: string.Empty);
        var models = await client.ListLocalModelsAsync(cancellationToken);
        return models
            .Where(model => !string.IsNullOrWhiteSpace(model.Name))
            .Select(model => new AiModelDescriptor(model.Name, model.Name, null, null, null, null, null, null))
            .ToArray();
    }

    private static AiProviderClientRequest CreateOpenAiCompatibilityRequest(AiProviderClientRequest request)
    {
        var secrets = request.Secrets.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        secrets[OpenAiProviderAdapter.ApiKeySecretName] = "ollama-local";
        return request with
        {
            Connection = request.Connection with
            {
                Endpoint = NormalizeOpenAiEndpoint(request.Connection.Endpoint),
                AuthKind = AiProviderAuthKind.ApiKey
            },
            Secrets = secrets
        };
    }

    private static Uri NormalizeOpenAiEndpoint(Uri endpoint)
    {
        var absolute = endpoint.AbsoluteUri.TrimEnd('/');
        return absolute.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? new Uri($"{absolute}/", UriKind.Absolute)
            : new Uri($"{absolute}/v1/", UriKind.Absolute);
    }

    private static NotSupportedException UnsupportedFormat(AiProviderClientRequest request)
        => new($"Ollama provider does not support API format '{request.Profile.ApiFormat}' " +
            $"for profile '{request.Profile.Name}'.");
}
