using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OpenAI.Images;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;

#pragma warning disable OPENAI001

namespace SelfClaw.Infrastructure.ImageGeneration;

internal sealed class OpenAiImageGenerationClient : IImageGenerationClient
{
    private readonly ImageClient _imageClient;
    private readonly Guid? _modelProfileId;

    public OpenAiImageGenerationClient(ImageClient imageClient, Guid? modelProfileId = null)
    {
        ArgumentNullException.ThrowIfNull(imageClient);
        _imageClient = imageClient;
        _modelProfileId = modelProfileId;
    }

    public async Task<ImageGenerationResult> GenerateImagesAsync(
        ImageGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var options = new ImageGenerationOptions
        {
            Quality = ResolveQuality(request.Quality),
            Size = ResolveSize(request.Size),
            Style = ResolveStyle(request.Style),
            ResponseFormat = ResolveFormat(request.ResponseFormat)
        };

        if (!string.IsNullOrWhiteSpace(request.User))
        {
            options.EndUserId = request.User;
        }

        var stopwatch = Stopwatch.StartNew();
        var response = await _imageClient.GenerateImagesAsync(
            request.Prompt,
            request.Count,
            options,
            cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();

        var items = new List<GeneratedImageItem>(response.Value.Count);
        foreach (var image in response.Value)
        {
            items.Add(new GeneratedImageItem(
                image.ImageBytes?.ToMemory(),
                image.ImageUri,
                image.RevisedPrompt));
        }

        return new ImageGenerationResult(items, stopwatch.Elapsed, _modelProfileId);
    }

    public async Task<ImageGenerationResult> GenerateImageEditsAsync(
        ImageEditRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var options = new ImageEditOptions
        {
            Size = ResolveSize(request.Size),
            ResponseFormat = ResolveFormat(request.ResponseFormat)
        };

        if (!string.IsNullOrWhiteSpace(request.User))
        {
            options.EndUserId = request.User;
        }

        var imageFileName = string.IsNullOrWhiteSpace(request.ImageFileName) ? "image.png" : request.ImageFileName;
        await using var imageStream = new MemoryStream(request.Image.ToArray());

        var stopwatch = Stopwatch.StartNew();
        GeneratedImageCollection images;

        if (request.Mask is { } maskData && !maskData.IsEmpty)
        {
            var maskFileName = string.IsNullOrWhiteSpace(request.MaskFileName) ? "mask.png" : request.MaskFileName;
            await using var maskStream = new MemoryStream(maskData.ToArray());

            var response = await _imageClient.GenerateImageEditsAsync(
                imageStream,
                imageFileName,
                request.Prompt,
                maskStream,
                maskFileName,
                request.Count,
                options,
                cancellationToken).ConfigureAwait(false);
            images = response.Value;
        }
        else
        {
            var response = await _imageClient.GenerateImageEditsAsync(
                imageStream,
                imageFileName,
                request.Prompt,
                request.Count,
                options,
                cancellationToken).ConfigureAwait(false);
            images = response.Value;
        }

        stopwatch.Stop();

        var items = new List<GeneratedImageItem>(images.Count);
        foreach (var image in images)
        {
            items.Add(new GeneratedImageItem(
                image.ImageBytes?.ToMemory(),
                image.ImageUri,
                image.RevisedPrompt));
        }

        return new ImageGenerationResult(items, stopwatch.Elapsed, _modelProfileId);
    }

    private static GeneratedImageQuality ResolveQuality(ImageGenerationQuality quality) =>
        quality switch
        {
            ImageGenerationQuality.Hd => GeneratedImageQuality.High,
            _ => GeneratedImageQuality.Standard
        };

    private static GeneratedImageStyle ResolveStyle(ImageGenerationStyle style) =>
        style switch
        {
            ImageGenerationStyle.Natural => GeneratedImageStyle.Natural,
            _ => GeneratedImageStyle.Vivid
        };

    private static GeneratedImageFormat ResolveFormat(ImageGenerationResponseFormat format) =>
        format switch
        {
            ImageGenerationResponseFormat.Uri => GeneratedImageFormat.Uri,
            _ => GeneratedImageFormat.Bytes
        };

    private static GeneratedImageSize ResolveSize(string? size)
    {
        if (string.IsNullOrWhiteSpace(size))
        {
            return GeneratedImageSize.W1024xH1024;
        }

        return size.Trim().ToLowerInvariant() switch
        {
            "256x256" => GeneratedImageSize.W256xH256,
            "512x512" => GeneratedImageSize.W512xH512,
            "1024x1024" => GeneratedImageSize.W1024xH1024,
            "1024x1536" => GeneratedImageSize.W1024xH1536,
            "1536x1024" => GeneratedImageSize.W1536xH1024,
            "1024x1792" => GeneratedImageSize.W1024xH1792,
            "1792x1024" => GeneratedImageSize.W1792xH1024,
            _ => GeneratedImageSize.W1024xH1024
        };
    }
}
