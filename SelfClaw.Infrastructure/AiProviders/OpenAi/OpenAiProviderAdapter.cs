using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI.Responses;
using SelfClaw.Infrastructure.AiProviders.Abstractions;
using System.ClientModel;
using System.Text.Json;
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
    internal const string ApiKeySecretName = "api_key";

    private readonly AiProviderKind _providerKind;
    private readonly ILogger<OpenAiProviderAdapter> _logger;

    /// <param name="providerKind">
    /// The OpenAI-family kind this instance serves. Must be
    /// <see cref="AiProviderKind.OpenAI"/> or <see cref="AiProviderKind.OpenAICompatible"/>.
    /// </param>
    /// <param name="logger">Optional logger; a null logger is used when omitted.</param>
    public OpenAiProviderAdapter(
        AiProviderKind providerKind = AiProviderKind.OpenAI,
        ILogger<OpenAiProviderAdapter>? logger = null)
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
    }

    public AiProviderKind ProviderKind => _providerKind;

    public bool SupportsApiFormat(AiProviderApiFormat apiFormat) =>
        apiFormat is AiProviderApiFormat.OpenAIChatCompletions or AiProviderApiFormat.OpenAIResponses;

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
    private static ResponsesClientOptions CreateResponsesClientOptions(AiProviderConnection connection) =>
        new() { Endpoint = connection.Endpoint };
#pragma warning restore OPENAI001

    private static OpenAIClientOptions CreateClientOptions(AiProviderConnection connection) =>
       new() { Endpoint = connection.Endpoint };
    private static ApiKeyCredential CreateCredential(AiProviderClientRequest request) =>
        new(ResolveApiKey(request));

    private static string ResolveApiKey(AiProviderClientRequest request)
    {
        if (!request.Secrets.TryGetValue(ApiKeySecretName, out var apiKey) || string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                $"AI provider connection '{request.Connection.Name}' is missing the required '{ApiKeySecretName}' secret.");
        }

        return apiKey;
    }

    private static NotSupportedException UnsupportedFormat(AiProviderClientRequest request) =>
        new($"OpenAI provider '{request.Connection.ProviderKind}' does not support API format " +
            $"'{request.Profile.ApiFormat}' for profile '{request.Profile.Name}'.");

    /// <summary>
    /// Builds the format-independent <see cref="ChatOptions"/> (sampling, tool
    /// mode, tools) and wires the provider-specific raw option factory.
    /// </summary>
    private static ChatOptions CreateSharedChatOptions(
        AiProviderClientRequest request,
        Func<IChatClient, object?> rawRepresentationFactory)
    {
        var sampling = request.Profile.Sampling;
        return new ChatOptions
        {
            Temperature = sampling.TemperatureEnabled ? (float)sampling.Temperature : null,
            TopP = sampling.TopPEnabled ? (float)sampling.TopP : null,
            ToolMode = request.Tools.Count > 0 ? ChatToolMode.Auto : ChatToolMode.None,
            Tools = request.Tools.Count > 0 ? request.Tools.ToList() : null,
            RawRepresentationFactory = rawRepresentationFactory
        };
    }

    /// <summary>
    /// Reads a string model option. Returns false (and logs a warning) when the
    /// key is present but is not a JSON string; returns false silently when absent.
    /// </summary>
    private bool TryReadString(
        IReadOnlyDictionary<string, JsonElement> options,
        string key,
        string profileName,
        out string value)
    {
        value = string.Empty;
        if (!options.TryGetValue(key, out var element))
        {
            return false;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            _logger.LogWarning(
                "Ignoring model option '{Key}' for profile '{Profile}': expected a string but got {Kind}.",
                key,
                profileName,
                element.ValueKind);
            return false;
        }

        value = element.GetString() ?? string.Empty;
        return true;
    }

    /// <summary>
    /// Reads a boolean model option. Returns false (and logs a warning) when the
    /// key is present but is not a JSON boolean; returns false silently when absent.
    /// </summary>
    private bool TryReadBool(
        IReadOnlyDictionary<string, JsonElement> options,
        string key,
        string profileName,
        out bool value)
    {
        value = false;
        if (!options.TryGetValue(key, out var element))
        {
            return false;
        }

        if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            _logger.LogWarning(
                "Ignoring model option '{Key}' for profile '{Profile}': expected a boolean but got {Kind}.",
                key,
                profileName,
                element.ValueKind);
            return false;
        }

        value = element.GetBoolean();
        return true;
    }

    /// <summary>
    /// Reads a 32-bit integer model option. Returns false (and logs a warning)
    /// when the key is present but is not a JSON integer; returns false silently
    /// when absent.
    /// </summary>
    private bool TryReadInt(
        IReadOnlyDictionary<string, JsonElement> options,
        string key,
        string profileName,
        out int value)
    {
        value = 0;
        if (!options.TryGetValue(key, out var element))
        {
            return false;
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out value))
        {
            _logger.LogWarning(
                "Ignoring model option '{Key}' for profile '{Profile}': expected an integer but got {Kind}.",
                key,
                profileName,
                element.ValueKind);
            value = 0;
            return false;
        }

        return true;
    }

    /// <summary>Emits a debug log for every option key the format does not recognize.</summary>
    private void LogUnknownOptions(
        IReadOnlyDictionary<string, JsonElement> options,
        IReadOnlySet<string> recognizedKeys,
        string profileName)
    {
        foreach (var key in options.Keys)
        {
            if (!recognizedKeys.Contains(key))
            {
                _logger.LogDebug(
                    "Ignoring unknown model option '{Key}' for profile '{Profile}'.",
                    key,
                    profileName);
            }
        }
    }
}
