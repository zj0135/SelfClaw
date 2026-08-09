using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI.Responses;
using SelfClaw.Infrastructure.AiProviders.Abstractions;
using System.ClientModel;
using System.ClientModel.Primitives;
using SelfClaw.Infrastructure.AiProviders.Http;
using SelfClaw.Infrastructure.AiProviders.Models;
using OpenAIClientOptions = OpenAI.OpenAIClientOptions;

namespace SelfClaw.Infrastructure.AiProviders.OpenAi;

/// <summary>
/// OpenAI provider adapter. Builds Microsoft.Extensions.AI chat clients and
/// options for both the OpenAI Chat Completions and OpenAI Responses wire
/// formats from a shared <see cref="AiProviderClientRequest"/>. Format-specific
/// creation lives in the matching partials
/// (<c>OpenAiProviderAdapter.ChatCompletions.cs</c>,
/// <c>OpenAiProviderAdapter.Responses.cs</c>); this file holds the dispatch and
/// the helpers shared across formats.
/// </summary>
/// <remarks>
/// A single instance serves one OpenAI-family kind. The same SDK backs both the
/// strict <see cref="AiProviderKind.OpenAI"/> endpoint and arbitrary
/// <see cref="AiProviderKind.OpenAICompatible"/> endpoints; the kind only changes
/// provider-specific defaults (notably the Chat Completions <c>thinking.type</c>
/// behavior). Register one instance per supported kind.
/// </remarks>
internal sealed partial class OpenAiProviderAdapter : IAiProviderAdapter
{
    /// <summary>Decrypted-secret key the adapter reads the API key from.</summary>
    internal const string ApiKeySecretName = AiProviderSecrets.ApiKeySecretName;

    private readonly AiProviderKind _providerKind;
    private readonly ILogger<OpenAiProviderAdapter> _logger;
    private readonly OpenAiModelListClient _modelListClient;
    private readonly AiProviderHttpClientProvider _httpClientProvider;

    /// <param name="providerKind">
    /// The OpenAI-family kind this instance serves. Must be
    /// <see cref="AiProviderKind.OpenAI"/> or <see cref="AiProviderKind.OpenAICompatible"/>.
    /// </param>
    /// <param name="logger">Optional logger; a null logger is used when omitted.</param>
    public OpenAiProviderAdapter(
        AiProviderKind providerKind = AiProviderKind.OpenAI,
        ILogger<OpenAiProviderAdapter>? logger = null,
        OpenAiModelListClient? modelListClient = null,
        AiProviderHttpClientProvider? httpClientProvider = null)
    {
        if (providerKind is not (AiProviderKind.OpenAI or AiProviderKind.OpenAICompatible))
        {
            throw new ArgumentOutOfRangeException(
                nameof(providerKind),
                providerKind,
                "OpenAiProviderAdapter only serves the OpenAI and OpenAICompatible provider kinds.");
        }

        _providerKind = providerKind;
        _logger = logger ?? NullLogger<OpenAiProviderAdapter>.Instance;
        _httpClientProvider = httpClientProvider ?? new AiProviderHttpClientProvider();
        _modelListClient = modelListClient ?? new OpenAiModelListClient(_httpClientProvider);
    }

    public AiProviderKind ProviderKind => _providerKind;

    public bool SupportsApiFormat(AiProviderApiFormat apiFormat) =>
        apiFormat is AiProviderApiFormat.OpenAIChatCompletions or AiProviderApiFormat.OpenAIResponses;

    public bool SupportsModelListing => true;

    public Task<IReadOnlyList<AiModelDescriptor>> ListModelsAsync(
        AiProviderConnection connection,
        IReadOnlyDictionary<string, string> secrets,
        CancellationToken cancellationToken = default)
        => _modelListClient.ListModelsAsync(connection, secrets, cancellationToken);

    public IChatClient CreateChatClient(AiProviderClientRequest request) =>
        request.Profile.ApiFormat switch
        {
            AiProviderApiFormat.OpenAIChatCompletions => CreateChatCompletionsClient(request),
            AiProviderApiFormat.OpenAIResponses => CreateResponsesClient(request),
            _ => throw UnsupportedFormat(request)
        };

    public ChatOptions CreateChatOptions(AiProviderClientRequest request) =>
        request.Profile.ApiFormat switch
        {
            AiProviderApiFormat.OpenAIChatCompletions => CreateChatCompletionsOptions(request),
            AiProviderApiFormat.OpenAIResponses => CreateResponsesOptions(request),
            _ => throw UnsupportedFormat(request)
        };

#pragma warning disable OPENAI001
    private ResponsesClientOptions CreateResponsesClientOptions(AiProviderConnection connection) =>
        new()
        {
            Endpoint = connection.Endpoint,
            Transport = new HttpClientPipelineTransport(_httpClientProvider.GetStreamingClient(connection))
        };
#pragma warning restore OPENAI001

    private OpenAIClientOptions CreateClientOptions(AiProviderConnection connection) =>
       new()
       {
           Endpoint = connection.Endpoint,
           Transport = new HttpClientPipelineTransport(_httpClientProvider.GetStreamingClient(connection))
       };
    private static ApiKeyCredential CreateCredential(AiProviderClientRequest request) =>
        new(ResolveApiKey(request));

    private static string ResolveApiKey(AiProviderClientRequest request)
        => AiProviderSecrets.RequireApiKey(request.Connection.Name, request.Secrets);

    private static NotSupportedException UnsupportedFormat(AiProviderClientRequest request) =>
        new($"OpenAI provider '{request.Connection.ProviderKind}' does not support API format " +
            $"'{request.Profile.ApiFormat}' for profile '{request.Profile.Name}'.");

    private ModelOptionReader OptionReader(AiProviderClientRequest request) =>
        new(_logger, request.Profile.ModelOptions, request.Profile.Name);
}
