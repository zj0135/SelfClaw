using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using SelfClaw.Infrastructure.AiProviders.Abstractions;
using SelfClaw.Infrastructure.AiProviders.Http;
using SelfClaw.Infrastructure.AiProviders.Models;

namespace SelfClaw.Infrastructure.AiProviders.Azure;

/// <summary>
/// Azure OpenAI adapter. Azure chat clients address deployments rather than
/// model ids, so <see cref="AiModelProfile.Model"/> is treated as the deployment name.
/// Azure's data-plane key cannot enumerate deployments; profiles are entered manually.
/// </summary>
internal sealed class AzureOpenAiProviderAdapter : IAiProviderAdapter
{
    internal const string ApiKeySecretName = AiProviderSecrets.ApiKeySecretName;
    internal const string ApiVersionOptionName = "api-version";

    private readonly AiProviderHttpClientProvider _httpClientProvider;

    public AzureOpenAiProviderAdapter(AiProviderHttpClientProvider httpClientProvider)
    {
        _httpClientProvider = httpClientProvider;
    }

    public AiProviderKind ProviderKind => AiProviderKind.AzureOpenAI;

    public bool SupportsModelListing => false;

    public bool SupportsApiFormat(AiProviderApiFormat apiFormat)
        => apiFormat == AiProviderApiFormat.OpenAIChatCompletions;

    public Task<IReadOnlyList<AiModelDescriptor>> ListModelsAsync(
        AiProviderConnection connection,
        IReadOnlyDictionary<string, string> secrets,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "Azure OpenAI deployments cannot be listed with a data-plane API key. Add the deployment as a model manually.");

    public IChatClient CreateChatClient(AiProviderClientRequest request)
    {
        EnsureSupported(request);
        var client = new AzureOpenAIClient(
            request.Connection.Endpoint,
            new ApiKeyCredential(ResolveApiKey(request)),
            CreateClientOptions(request.Connection));
        return client.GetChatClient(request.Profile.Model).AsIChatClient();
    }

    public ChatOptions CreateChatOptions(AiProviderClientRequest request)
    {
        EnsureSupported(request);
        return AiChatOptions.CreateBase(request);
    }

    private AzureOpenAIClientOptions CreateClientOptions(AiProviderConnection connection)
    {
        var options = TryReadServiceVersion(connection, out var serviceVersion)
            ? new AzureOpenAIClientOptions(serviceVersion)
            : new AzureOpenAIClientOptions();
        options.Transport = new HttpClientPipelineTransport(_httpClientProvider.GetStreamingClient(connection));
        return options;
    }

    private static bool TryReadServiceVersion(
        AiProviderConnection connection,
        out AzureOpenAIClientOptions.ServiceVersion serviceVersion)
    {
        serviceVersion = default;
        if (!connection.ConnectionOptions.TryGetValue(ApiVersionOptionName, out var value))
        {
            return false;
        }

        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidOperationException(
                $"Azure OpenAI connection option '{ApiVersionOptionName}' must be a non-empty string.");
        }

        var apiVersion = value.GetString()!;
        var enumName = $"V{apiVersion.Replace('-', '_')}";
        if (!Enum.TryParse(enumName, ignoreCase: true, out serviceVersion) ||
            !Enum.IsDefined(serviceVersion))
        {
            throw new InvalidOperationException(
                $"Azure OpenAI api-version '{apiVersion}' is not supported by the installed Azure SDK.");
        }

        return true;
    }

    private static string ResolveApiKey(AiProviderClientRequest request)
        => AiProviderSecrets.RequireApiKey(request.Connection.Name, request.Secrets);

    private void EnsureSupported(AiProviderClientRequest request)
    {
        if (!SupportsApiFormat(request.Profile.ApiFormat))
        {
            throw new NotSupportedException(
                $"Azure OpenAI provider does not support API format '{request.Profile.ApiFormat}' " +
                $"for profile '{request.Profile.Name}'.");
        }
    }
}
