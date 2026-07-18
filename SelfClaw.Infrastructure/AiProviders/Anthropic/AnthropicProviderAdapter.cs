using Anthropic;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Infrastructure.AiProviders.Abstractions;
using SelfClaw.Infrastructure.AiProviders.Models;

namespace SelfClaw.Infrastructure.AiProviders.Anthropic;

/// <summary>
/// Anthropic provider adapter backed by the official Microsoft Agent Framework
/// Anthropic integration. The integration exposes Anthropic as a ChatClientAgent;
/// its client factory hook is used here to capture the underlying IChatClient so
/// the rest of SelfClaw can keep using the shared provider abstraction.
/// </summary>
internal sealed class AnthropicProviderAdapter : IAiProviderAdapter
{
    internal const string ApiKeySecretName = AiProviderSecrets.ApiKeySecretName;
    private const string MaxTokensKey = "max_tokens";

    private static readonly IReadOnlySet<string> RecognizedModelOptionKeys =
        new HashSet<string>(StringComparer.Ordinal)
        {
            MaxTokensKey
        };

    private readonly ILogger<AnthropicProviderAdapter> _logger;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly IServiceProvider? _serviceProvider;
    private readonly AnthropicModelListClient _modelListClient;

    public AnthropicProviderAdapter(
        ILogger<AnthropicProviderAdapter>? logger = null,
        ILoggerFactory? loggerFactory = null,
        IServiceProvider? serviceProvider = null,
        AnthropicModelListClient? modelListClient = null)
    {
        _logger = logger ?? NullLogger<AnthropicProviderAdapter>.Instance;
        _loggerFactory = loggerFactory;
        _serviceProvider = serviceProvider;
        _modelListClient = modelListClient
            ?? new AnthropicModelListClient(new Http.AiProviderHttpClientProvider());
    }

    public AiProviderKind ProviderKind => AiProviderKind.Anthropic;

    public bool SupportsApiFormat(AiProviderApiFormat apiFormat) =>
        apiFormat == AiProviderApiFormat.AnthropicMessages;

    public bool SupportsModelListing => true;

    public Task<IReadOnlyList<AiModelDescriptor>> ListModelsAsync(
        AiProviderConnection connection,
        IReadOnlyDictionary<string, string> secrets,
        CancellationToken cancellationToken = default)
        => _modelListClient.ListModelsAsync(connection, secrets, cancellationToken);

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
            ModelOptionReader.ForProfile(_logger, request.Profile).TryReadInt(MaxTokensKey, out var maxTokens)
                ? maxTokens
                : null,
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

        var options = AiChatOptions.CreateBase(request);
        var reader = ModelOptionReader.ForProfile(_logger, request.Profile);
        if (reader.TryReadInt(MaxTokensKey, out var maxTokens))
        {
            options.MaxOutputTokens = maxTokens;
        }

        reader.LogUnknown(RecognizedModelOptionKeys);
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
        => AiProviderSecrets.RequireApiKey(request.Connection.Name, request.Secrets);

    private static NotSupportedException UnsupportedFormat(AiProviderClientRequest request) =>
        new($"Anthropic provider '{request.Connection.ProviderKind}' does not support API format " +
            $"'{request.Profile.ApiFormat}' for profile '{request.Profile.Name}'.");
}
