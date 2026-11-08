using System;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI.Images;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.AiProviders;
using SelfClaw.Infrastructure.AiProviders.Abstractions;
using SelfClaw.Infrastructure.AiProviders.Http;
using SelfClaw.Infrastructure.AiProviders.Models;
using OpenAIClientOptions = OpenAI.OpenAIClientOptions;

namespace SelfClaw.Infrastructure.ImageGeneration;

internal sealed class ImageGenerationService : IImageGenerationService
{
    private readonly IAiProviderRepository _repository;
    private readonly AiProviderRegistry _registry;
    private readonly ISecretProtector _secretProtector;
    private readonly AiProviderHttpClientProvider _httpClientProvider;
    private readonly ILogger<ImageGenerationService> _logger;

    public ImageGenerationService(
        IAiProviderRepository repository,
        AiProviderRegistry registry,
        ISecretProtector secretProtector,
        AiProviderHttpClientProvider httpClientProvider,
        ILogger<ImageGenerationService>? logger = null)
    {
        _repository = repository;
        _registry = registry;
        _secretProtector = secretProtector;
        _httpClientProvider = httpClientProvider;
        _logger = logger ?? NullLogger<ImageGenerationService>.Instance;
    }

    public async Task<ImageGenerationResult> GenerateImagesAsync(
        Guid? modelProfileId,
        ImageGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (client, httpClient) = await ResolveClientAsync(
            modelProfileId,
            request.Model,
            cancellationToken).ConfigureAwait(false);

        using (httpClient)
        {
            return await client.GenerateImagesAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<ImageGenerationResult> GenerateImageEditsAsync(
        Guid? modelProfileId,
        ImageEditRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (client, httpClient) = await ResolveClientAsync(
            modelProfileId,
            request.Model,
            cancellationToken).ConfigureAwait(false);

        using (httpClient)
        {
            return await client.GenerateImageEditsAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<(IImageGenerationClient Client, HttpClient HttpClient)> ResolveClientAsync(
        Guid? modelProfileId,
        string? requestedModel,
        CancellationToken cancellationToken)
    {
        var profile = await ResolveProfileAsync(modelProfileId, cancellationToken).ConfigureAwait(false);
        if (!profile.IsEnabled)
        {
            throw new InvalidOperationException($"AI model profile '{profile.Name}' is disabled.");
        }

        if (profile.ApiFormat != AiProviderApiFormat.OpenAIImages)
        {
            throw new NotSupportedException(
                $"AI model profile '{profile.Name}' uses API format '{profile.ApiFormat}', " +
                $"which is not supported for image generation. Expected '{nameof(AiProviderApiFormat.OpenAIImages)}'.");
        }

        var connection = await _repository.GetProviderConnectionAsync(
                profile.ProviderConnectionId,
                cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException(
                $"AI provider connection '{profile.ProviderConnectionId}' for model '{profile.Name}' was not found.");

        if (!connection.IsEnabled)
        {
            throw new InvalidOperationException($"AI provider connection '{connection.Name}' is disabled.");
        }

        var adapter = _registry.GetRequiredAdapter(connection.ProviderKind);
        if (!adapter.SupportsApiFormat(profile.ApiFormat))
        {
            throw new NotSupportedException(
                $"AI provider '{connection.Name}' does not support API format '{profile.ApiFormat}'.");
        }

        var secrets = await AiProviderSecrets.ResolveAsync(_secretProtector, connection, cancellationToken).ConfigureAwait(false);
        var apiKey = AiProviderSecrets.RequireApiKey(connection.Name, secrets);

        var httpClient = _httpClientProvider.CreateTurnClient(connection);
        try
        {
            var clientOptions = new OpenAIClientOptions
            {
                Endpoint = connection.Endpoint,
                Transport = new HttpClientPipelineTransport(httpClient)
            };

            var targetModel = !string.IsNullOrWhiteSpace(requestedModel) ? requestedModel : profile.Model;
            var nativeClient = new ImageClient(targetModel, new ApiKeyCredential(apiKey), clientOptions);
            var client = new OpenAiImageGenerationClient(nativeClient, profile.Id);

            return (client, httpClient);
        }
        catch
        {
            httpClient.Dispose();
            throw;
        }
    }

    private async Task<AiModelProfile> ResolveProfileAsync(
        Guid? modelProfileId,
        CancellationToken cancellationToken)
    {
        if (modelProfileId.HasValue)
        {
            return await _repository.GetModelProfileAsync(modelProfileId.Value, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"AI model profile '{modelProfileId.Value}' was not found.");
        }

        var profiles = await _repository.ListEnabledModelProfilesAsync(cancellationToken).ConfigureAwait(false);
        var matched = profiles.FirstOrDefault(p => p.ApiFormat == AiProviderApiFormat.OpenAIImages);
        if (matched is not null)
        {
            return matched;
        }

        throw new InvalidOperationException(
            "No enabled image generation model profile found. Please configure a model with the OpenAIImages API format.");
    }
}
