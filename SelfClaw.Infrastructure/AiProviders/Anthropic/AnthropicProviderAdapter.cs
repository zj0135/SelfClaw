using Anthropic;
using Anthropic.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Infrastructure.AiProviders.Abstractions;
using SelfClaw.Infrastructure.AiProviders.Http;
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
            MaxTokensKey,
            AiChatOptions.MaxOutputTokensKey
        };

    private readonly ILogger<AnthropicProviderAdapter> _logger;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly IServiceProvider? _serviceProvider;
    private readonly AnthropicModelListClient _modelListClient;
    private readonly AiProviderHttpClientProvider _httpClientProvider;

    public AnthropicProviderAdapter(
        ILogger<AnthropicProviderAdapter>? logger = null,
        ILoggerFactory? loggerFactory = null,
        IServiceProvider? serviceProvider = null,
        AnthropicModelListClient? modelListClient = null,
        AiProviderHttpClientProvider? httpClientProvider = null)
    {
        _logger = logger ?? NullLogger<AnthropicProviderAdapter>.Instance;
        _loggerFactory = loggerFactory;
        _serviceProvider = serviceProvider;
        _httpClientProvider = httpClientProvider ?? new AiProviderHttpClientProvider();
        _modelListClient = modelListClient
            ?? new AnthropicModelListClient(_httpClientProvider);
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
            ResolveMaxOutputTokens(request),
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
        options.MaxOutputTokens = ResolveMaxOutputTokens(request);
        ModelOptionReader.ForProfile(_logger, request.Profile).LogUnknown(RecognizedModelOptionKeys);
        return options;
    }

    /// <summary>
    /// Resolves the turn's output ceiling from model options, falling back to the model's
    /// catalog maximum. Without this the Agent Framework integration sends its own 4096
    /// default, which truncates ordinary answers.
    /// </summary>
    private int? ResolveMaxOutputTokens(AiProviderClientRequest request)
    {
        var reader = ModelOptionReader.ForProfile(_logger, request.Profile);
        int? configured = reader.TryReadInt(MaxTokensKey, out var maxTokens)
            ? maxTokens
            : reader.TryReadInt(AiChatOptions.MaxOutputTokensKey, out var aliased)
                ? aliased
                : null;
        return AiChatOptions.ResolveMaxOutputTokens(request, configured);
    }

    /// <summary>
    /// Builds the per-turn Anthropic client on top of the shared pooled handler. The SDK's
    /// <c>AnthropicClient.Dispose</c> disposes whatever HttpClient it was handed, so the client gets a
    /// short-lived wrapper over the shared handler (<c>disposeHandler: false</c>): disposing a turn's
    /// client never tears down the pooled connections the next turn reuses.
    /// </summary>
    internal AnthropicClient CreateAnthropicClient(AiProviderClientRequest request)
    {
        var options = default(ClientOptions);
        options.ApiKey = ResolveApiKey(request);
        options.BaseUrl = request.Connection.Endpoint.AbsoluteUri;
        options.HttpClient = new HttpClient(
            _httpClientProvider.GetSharedStreamingHandler(request.Connection),
            disposeHandler: false)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        return new AnthropicClient(options);
    }

    private static string ResolveApiKey(AiProviderClientRequest request)
        => AiProviderSecrets.RequireApiKey(request.Connection.Name, request.Secrets);

    private static NotSupportedException UnsupportedFormat(AiProviderClientRequest request) =>
        new($"Anthropic provider '{request.Connection.ProviderKind}' does not support API format " +
            $"'{request.Profile.ApiFormat}' for profile '{request.Profile.Name}'.");
}
