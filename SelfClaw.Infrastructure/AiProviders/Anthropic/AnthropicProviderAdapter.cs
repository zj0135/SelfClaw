using System.Text.Json;
using Anthropic;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Infrastructure.AiProviders.Abstractions;

namespace SelfClaw.Infrastructure.AiProviders.Anthropic;

/// <summary>
/// Anthropic provider adapter backed by the official Microsoft Agent Framework
/// Anthropic integration. The integration exposes Anthropic as a ChatClientAgent;
/// its client factory hook is used here to capture the underlying IChatClient so
/// the rest of SelfClaw can keep using the shared provider abstraction.
/// </summary>
internal sealed class AnthropicProviderAdapter : IAiProviderAdapter
{
    internal const string ApiKeySecretName = "api_key";
    private const string MaxTokensKey = "max_tokens";

    private static readonly IReadOnlySet<string> RecognizedModelOptionKeys =
        new HashSet<string>(StringComparer.Ordinal)
        {
            MaxTokensKey
        };

    private readonly ILogger<AnthropicProviderAdapter> _logger;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly IServiceProvider? _serviceProvider;

    public AnthropicProviderAdapter(
        ILogger<AnthropicProviderAdapter>? logger = null,
        ILoggerFactory? loggerFactory = null,
        IServiceProvider? serviceProvider = null)
    {
        _logger = logger ?? NullLogger<AnthropicProviderAdapter>.Instance;
        _loggerFactory = loggerFactory;
        _serviceProvider = serviceProvider;
    }

    public AiProviderKind ProviderKind => AiProviderKind.Anthropic;

    public bool SupportsApiFormat(AiProviderApiFormat apiFormat) =>
        apiFormat == AiProviderApiFormat.AnthropicMessages;

    public IChatClient CreateChatClient(AiProviderClientRequest request)
    {
        if (request.Profile.ApiFormat != AiProviderApiFormat.AnthropicMessages)
        {
            throw UnsupportedFormat(request);
        }

        var client = CreateAnthropicClient(request);
        IChatClient? capturedClient = null;

        client.AsAIAgent(
            request.Profile.Model,
            request.Profile.Name,
            string.Empty,
            string.Empty,
            request.Tools.ToList(),
            TryReadMaxTokens(request.Profile, out var maxTokens) ? maxTokens : null,
            chatClient =>
            {
                capturedClient = chatClient;
                return chatClient;
            },
            _loggerFactory,
            _serviceProvider);

        return capturedClient ?? throw new InvalidOperationException(
            $"Anthropic provider '{request.Connection.Name}' did not expose an IChatClient.");
    }

    public ChatOptions CreateChatOptions(AiProviderClientRequest request)
    {
        if (request.Profile.ApiFormat != AiProviderApiFormat.AnthropicMessages)
        {
            throw UnsupportedFormat(request);
        }

        var sampling = request.Profile.Sampling;
        var options = new ChatOptions
        {
            Temperature = sampling.TemperatureEnabled ? (float)sampling.Temperature : null,
            TopP = sampling.TopPEnabled ? (float)sampling.TopP : null,
            ToolMode = request.Tools.Count > 0 ? ChatToolMode.Auto : ChatToolMode.None,
            Tools = request.Tools.Count > 0 ? request.Tools.ToList() : null
        };

        if (TryReadMaxTokens(request.Profile, out var maxTokens))
        {
            options.MaxOutputTokens = maxTokens;
        }

        LogUnknownOptions(request.Profile.ModelOptions, request.Profile.Name);
        return options;
    }

    private static AnthropicClient CreateAnthropicClient(AiProviderClientRequest request)
    {
        var client = new AnthropicClient
        {
            ApiKey = ResolveApiKey(request),
            BaseUrl = request.Connection.Endpoint.AbsoluteUri
        };

        return client;
    }

    private static string ResolveApiKey(AiProviderClientRequest request)
    {
        if (!request.Secrets.TryGetValue(ApiKeySecretName, out var apiKey) || string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                $"AI provider connection '{request.Connection.Name}' is missing the required '{ApiKeySecretName}' secret.");
        }

        return apiKey;
    }

    private bool TryReadMaxTokens(AiModelProfile profile, out int value)
    {
        value = 0;
        if (!profile.ModelOptions.TryGetValue(MaxTokensKey, out var element))
        {
            return false;
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out value))
        {
            _logger.LogWarning(
                "Ignoring model option '{Key}' for profile '{Profile}': expected an integer but got {Kind}.",
                MaxTokensKey,
                profile.Name,
                element.ValueKind);
            value = 0;
            return false;
        }

        return true;
    }

    private void LogUnknownOptions(
        IReadOnlyDictionary<string, JsonElement> options,
        string profileName)
    {
        foreach (var key in options.Keys)
        {
            if (!RecognizedModelOptionKeys.Contains(key))
            {
                _logger.LogDebug(
                    "Ignoring unknown model option '{Key}' for profile '{Profile}'.",
                    key,
                    profileName);
            }
        }
    }

    private static NotSupportedException UnsupportedFormat(AiProviderClientRequest request) =>
        new($"Anthropic provider '{request.Connection.ProviderKind}' does not support API format " +
            $"'{request.Profile.ApiFormat}' for profile '{request.Profile.Name}'.");
}
