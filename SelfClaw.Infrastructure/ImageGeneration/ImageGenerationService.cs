using System;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
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
    /// <summary>
    /// Image rendering is one long request, not a token stream: a 1792x1024 dall-e-3 render routinely
    /// passes the 100s chat timeout and gpt-image-1 is slower still. The ceiling is enforced through the
    /// linked token so expiry aborts the socket read instead of merely abandoning the await.
    /// </summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(5);

    private readonly IAiProviderRepository _repository;
    private readonly AiProviderRegistry _registry;
    private readonly ISecretProtector _secretProtector;
    private readonly AiProviderHttpClientProvider _httpClientProvider;

    public ImageGenerationService(
        IAiProviderRepository repository,
        AiProviderRegistry registry,
        ISecretProtector secretProtector,
        AiProviderHttpClientProvider httpClientProvider)
    {
        _repository = repository;
        _registry = registry;
        _secretProtector = secretProtector;
        _httpClientProvider = httpClientProvider;
    }

    public Task<ImageGenerationResult> GenerateImagesAsync(
        Guid? modelProfileId,
        ImageGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteAsync(
            modelProfileId,
            (client, token) => client.GenerateImagesAsync(request, token),
            cancellationToken);
    }

    public Task<ImageGenerationResult> GenerateImageEditsAsync(
        Guid? modelProfileId,
        ImageEditRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteAsync(
            modelProfileId,
            (client, token) => client.GenerateImageEditsAsync(request, token),
            cancellationToken);
    }

    public ImageGenerationModelCapabilities GetModelCapabilities(string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        return OpenAiImageModelCapabilities.Resolve(model);
    }

    private async Task<ImageGenerationResult> ExecuteAsync(
        Guid? modelProfileId,
        Func<IImageGenerationClient, CancellationToken, Task<ImageGenerationResult>> operation,
        CancellationToken cancellationToken)
    {
        var (client, httpClient) = await ResolveClientAsync(modelProfileId, cancellationToken).ConfigureAwait(false);
        using (httpClient)
        using (var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeoutSource.CancelAfter(RequestTimeout);
            try
            {
                return await operation(client, timeoutSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Caller cancellation keeps propagating as cancellation; only the timeout becomes a
                // failure result the user can act on.
                throw new TimeoutException(
                    $"Image generation did not finish within {RequestTimeout.TotalMinutes:0} minutes and was cancelled.");
            }
        }
    }

    private async Task<(IImageGenerationClient Client, HttpClient HttpClient)> ResolveClientAsync(
        Guid? modelProfileId,
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

            // The profile owns the model id: the renderer picks a profile, never a model string, so a
            // request can not point a stored endpoint and key at a different model.
            var nativeClient = new ImageClient(profile.Model, new ApiKeyCredential(apiKey), clientOptions);
            var client = new OpenAiImageGenerationClient(
                nativeClient,
                profile.Model,
                OpenAiImageModelCapabilities.Resolve(profile.Model),
                profile.Id);

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
