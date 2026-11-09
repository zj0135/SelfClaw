using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OpenAI.Images;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;

namespace SelfClaw.Infrastructure.ImageGeneration;

#pragma warning disable OPENAI001

internal sealed class OpenAiImageGenerationClient : IImageGenerationClient
{
    private readonly ImageClient _imageClient;
    private readonly string _model;
    private readonly ImageGenerationModelCapabilities _capabilities;
    private readonly Guid? _modelProfileId;

    public OpenAiImageGenerationClient(
        ImageClient imageClient,
        string model,
        ImageGenerationModelCapabilities capabilities,
        Guid? modelProfileId = null)
    {
        ArgumentNullException.ThrowIfNull(imageClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(capabilities);
        _imageClient = imageClient;
        _model = model;
        _capabilities = capabilities;
        _modelProfileId = modelProfileId;
    }

    public async Task<ImageGenerationResult> GenerateImagesAsync(
        ImageGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsurePrompt(request.Prompt);
        EnsureCount(request.Count);

        var options = new ImageGenerationOptions
        {
            Size = ResolveSize(request.Size),
            Quality = ResolveQuality(request.Quality)
        };
        // Both fields are omitted for families that do not accept them (gpt-image-1 answers 400 to
        // response_format and style), which is why neither is set unconditionally.
        if (_capabilities.Styles.Count > 0)
        {
            options.Style = ResolveStyle(request.Style);
        }

        var stopwatch = Stopwatch.StartNew();
        var response = await _imageClient.GenerateImagesAsync(
            request.Prompt,
            request.Count,
            options,
            cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();

        return Map(response.Value, stopwatch.Elapsed);
    }

    public async Task<ImageGenerationResult> GenerateImageEditsAsync(
        ImageEditRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsurePrompt(request.Prompt);
        EnsureCount(request.Count);
        if (request.Image.IsEmpty)
        {
            throw new ArgumentException("An image edit requires the source image bytes.", nameof(request));
        }

        var options = new ImageEditOptions { Size = ResolveSize(request.Size) };
        await using var imageStream = new MemoryStream(request.Image.ToArray());

        var stopwatch = Stopwatch.StartNew();
        var response = await _imageClient.GenerateImageEditsAsync(
            imageStream,
            "image.png",
            request.Prompt,
            request.Count,
            options,
            cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();

        return Map(response.Value, stopwatch.Elapsed);
    }

    private ImageGenerationResult Map(GeneratedImageCollection images, TimeSpan duration)
    {
        var items = new List<GeneratedImageItem>(images.Count);
        foreach (var image in images)
        {
            items.Add(new GeneratedImageItem(
                image.ImageBytes?.ToMemory(),
                image.ImageUri,
                image.RevisedPrompt));
        }

        return new ImageGenerationResult(items, duration, _modelProfileId);
    }

    private static void EnsurePrompt(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new ArgumentException("An image request requires a prompt.", nameof(prompt));
        }
    }

    private void EnsureCount(int count)
    {
        if (count < 1 || count > _capabilities.MaxCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(count),
                count,
                $"Model '{_model}' can generate between 1 and {_capabilities.MaxCount} image(s) per request.");
        }
    }

    private GeneratedImageSize ResolveSize(string? size)
    {
        var value = string.IsNullOrWhiteSpace(size) ? _capabilities.Sizes[0] : size.Trim().ToLowerInvariant();
        if (!_capabilities.Sizes.Contains(value))
        {
            throw new ArgumentException(
                $"Model '{_model}' does not support size '{value}'. Supported sizes: {string.Join(", ", _capabilities.Sizes)}.",
                nameof(size));
        }

        return value switch
        {
            "256x256" => GeneratedImageSize.W256xH256,
            "512x512" => GeneratedImageSize.W512xH512,
            "1024x1024" => GeneratedImageSize.W1024xH1024,
            "1024x1536" => GeneratedImageSize.W1024xH1536,
            "1536x1024" => GeneratedImageSize.W1536xH1024,
            "1024x1792" => GeneratedImageSize.W1024xH1792,
            "1792x1024" => GeneratedImageSize.W1792xH1024,
            _ => throw new ArgumentException($"Size '{value}' has no OpenAI Images equivalent.", nameof(size))
        };
    }

    private GeneratedImageQuality ResolveQuality(ImageGenerationQuality? quality)
    {
        var value = quality ?? _capabilities.Qualities[0];
        if (!_capabilities.Qualities.Contains(value))
        {
            throw new ArgumentException(
                $"Model '{_model}' does not support quality '{value}'. Supported qualities: {string.Join(", ", _capabilities.Qualities)}.",
                nameof(quality));
        }

        return value switch
        {
            ImageGenerationQuality.Auto => GeneratedImageQuality.Auto,
            ImageGenerationQuality.Low => GeneratedImageQuality.LowQuality,
            ImageGenerationQuality.Medium => GeneratedImageQuality.MediumQuality,
            ImageGenerationQuality.High => GeneratedImageQuality.HighQuality,
            ImageGenerationQuality.Hd => GeneratedImageQuality.High,
            _ => GeneratedImageQuality.Standard
        };
    }

    private static GeneratedImageStyle ResolveStyle(ImageGenerationStyle? style) =>
        style is ImageGenerationStyle.Natural ? GeneratedImageStyle.Natural : GeneratedImageStyle.Vivid;
}

#pragma warning restore OPENAI001
