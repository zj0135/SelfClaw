using SelfClaw.Core.Models;
using Anthropic;
using Anthropic.Core;
using Anthropic.Models.Messages;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Infrastructure.AiProviders.Abstractions;
using SelfClaw.Infrastructure.AiProviders.Http;
using SelfClaw.Infrastructure.AiProviders.Models;

namespace SelfClaw.Infrastructure.AiProviders.Anthropic;

/// <summary>
/// Anthropic provider adapter backed by the official SDK's IChatClient integration.
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
    private readonly AnthropicModelListClient _modelListClient;
    private readonly AiProviderHttpClientProvider _httpClientProvider;

    public AnthropicProviderAdapter(
        ILogger<AnthropicProviderAdapter>? logger = null,
        AnthropicModelListClient? modelListClient = null,
        AiProviderHttpClientProvider? httpClientProvider = null)
    {
        _logger = logger ?? NullLogger<AnthropicProviderAdapter>.Instance;
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

    public IChatClient CreateChatClient(AiProviderClientRequest request, HttpClient httpClient)
    {
        if (request.Profile.ApiFormat != AiProviderApiFormat.AnthropicMessages)
        {
            throw UnsupportedFormat(request);
        }

        return CreateAnthropicClient(request, httpClient)
            .AsIChatClient(request.Profile.Model, ResolveMaxOutputTokens(request));
    }

    public ChatOptions CreateChatOptions(AiProviderClientRequest request)
    {
        if (request.Profile.ApiFormat != AiProviderApiFormat.AnthropicMessages)
        {
            throw UnsupportedFormat(request);
        }

        var options = AiChatOptions.CreateBase(request, _ => CreateReasoningOptions(request));
        options.MaxOutputTokens = ResolveMaxOutputTokens(request);
        ModelOptionReader.ForProfile(_logger, request.Profile).LogUnknown(RecognizedModelOptionKeys);
        return options;
    }

    private MessageCreateParams? CreateReasoningOptions(AiProviderClientRequest request)
    {
        if (request.Profile.Configuration?.ReasoningEffort is not string effort)
        {
            return null;
        }

        return new MessageCreateParams
        {
            Model = request.Profile.Model,
            Messages = [],
            MaxTokens = ResolveMaxOutputTokens(request) ?? 4096,
            Thinking = effort == "none"
                ? new ThinkingConfigDisabled()
                : new ThinkingConfigAdaptive(),
            OutputConfig = effort == "none" ? null : new OutputConfig
            {
                Effort = effort switch
                {
                    "minimal" or "low" => Effort.Low,
                    "medium" => Effort.Medium,
                    "high" => Effort.High,
                    _ => Effort.Max
                }
            }
        };
    }

    /// <summary>
    /// Resolves the turn's output ceiling from model options, falling back to the model's
    /// catalog maximum. Without this the SDK integration sends its own 4096
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
    /// Builds the per-turn Anthropic client on the caller-owned turn client. The SDK's
    /// <c>AnthropicClient.Dispose</c> disposes whatever HttpClient it was handed, so the caller must keep
    /// that wrapper alive for the turn's duration; disposing it never tears down the shared pooled handler
    /// behind it (<c>disposeHandler: false</c>).
    /// </summary>
    internal AnthropicClient CreateAnthropicClient(AiProviderClientRequest request, HttpClient httpClient)
    {
        var options = new ClientOptions
        {
            ApiKey = ResolveApiKey(request),
            AuthToken = null,
            BaseUrl = request.Connection.Endpoint.AbsoluteUri,
            HttpClient = httpClient
        };

        return new AnthropicClient(options);
    }

    private static string ResolveApiKey(AiProviderClientRequest request)
        => AiProviderSecrets.RequireApiKey(request.Connection.Name, request.Secrets);

    private static NotSupportedException UnsupportedFormat(AiProviderClientRequest request) =>
        new($"Anthropic provider '{request.Connection.ProviderKind}' does not support API format " +
            $"'{request.Profile.ApiFormat}' for profile '{request.Profile.Name}'.");
}
