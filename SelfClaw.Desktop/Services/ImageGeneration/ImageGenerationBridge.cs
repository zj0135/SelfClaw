using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Desktop.Services.ImageGeneration;

internal sealed class ImageGenerationBridge
{
    private const string MessagePrefix = "image-generation/";

    private readonly IImageGenerationService _imageService;
    private readonly IAiProviderSettingsService _providerSettingsService;
    private readonly StoragePaths _storagePaths;
    private readonly ILogger<ImageGenerationBridge> _logger;

    public ImageGenerationBridge(
        IImageGenerationService imageService,
        IAiProviderSettingsService providerSettingsService,
        StoragePaths storagePaths,
        ILogger<ImageGenerationBridge>? logger = null)
    {
        _imageService = imageService;
        _providerSettingsService = providerSettingsService;
        _storagePaths = storagePaths;
        _logger = logger ?? NullLogger<ImageGenerationBridge>.Instance;
    }

    public async Task<object?> TryHandleAsync(
        string type,
        JsonElement payload,
        CancellationToken cancellationToken = default)
    {
        if (!type.StartsWith(MessagePrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var requestId = ReadOptionalString(payload, "requestId");
        try
        {
            return type switch
            {
                "image-generation/list-models" => await HandleListModelsAsync(type, requestId, cancellationToken),
                "image-generation/generate" => await HandleGenerateAsync(type, payload, requestId, cancellationToken),
                "image-generation/edit" => await HandleEditAsync(type, payload, requestId, cancellationToken),
                _ => new { type, requestId, error = $"Unknown image generation message type '{type}'." }
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling image generation request '{Type}'", type);
            return new { type, requestId, error = ex.Message };
        }
    }

    private async Task<object> HandleListModelsAsync(string type, string? requestId, CancellationToken cancellationToken)
    {
        var state = await _providerSettingsService.GetStateAsync(cancellationToken);
        var imageProfiles = new List<object>();

        foreach (var provider in state.Providers.Where(p => p.Enabled))
        {
            foreach (var model in provider.Models.Where(m => m.Enabled && m.ApiFormat == AiProviderApiFormat.OpenAIImages))
            {
                imageProfiles.Add(new
                {
                    profileId = model.ModelProfileId,
                    providerConnectionId = provider.ConnectionId,
                    providerName = provider.Name,
                    name = model.Name,
                    model = model.Model,
                    apiFormat = (int)model.ApiFormat,
                    description = model.Configuration?.Description ?? $"{provider.Name} 图像生成模型"
                });
            }
        }

        return new { type, requestId, ok = true, models = imageProfiles };
    }

    private async Task<object> HandleGenerateAsync(string type, JsonElement payload, string? requestId, CancellationToken cancellationToken)
    {
        var prompt = ReadRequiredString(payload, "prompt");
        var modelProfileId = ReadOptionalGuid(payload, "modelProfileId", "profileId");
        var model = ReadOptionalString(payload, "model");
        var count = ReadOptionalInt(payload, "count") ?? 1;
        var size = ReadOptionalString(payload, "size") ?? "1024x1024";
        var qualityStr = ReadOptionalString(payload, "quality");
        var styleStr = ReadOptionalString(payload, "style");

        var quality = string.Equals(qualityStr, "hd", StringComparison.OrdinalIgnoreCase)
            ? ImageGenerationQuality.Hd
            : ImageGenerationQuality.Standard;

        var style = string.Equals(styleStr, "natural", StringComparison.OrdinalIgnoreCase)
            ? ImageGenerationStyle.Natural
            : ImageGenerationStyle.Vivid;

        var request = new ImageGenerationRequest(
            prompt,
            model,
            count,
            size,
            quality,
            style,
            ImageGenerationResponseFormat.Bytes);

        var result = await _imageService.GenerateImagesAsync(modelProfileId, request, cancellationToken);
        var images = new List<object>(result.Images.Count);

        var outputDir = Path.Combine(_storagePaths.AppDataDirectory, "generated-images", DateTime.UtcNow.ToString("yyyyMMdd"));
        Directory.CreateDirectory(outputDir);

        for (var i = 0; i < result.Images.Count; i++)
        {
            var item = result.Images[i];
            string? localPath = null;
            string? dataUri = null;

            if (item.ImageBytes is { } bytes && !bytes.IsEmpty)
            {
                var fileName = $"{Guid.NewGuid():N}.png";
                localPath = Path.Combine(outputDir, fileName);
                await File.WriteAllBytesAsync(localPath, bytes.ToArray(), cancellationToken);
                dataUri = $"data:image/png;base64,{Convert.ToBase64String(bytes.Span)}";
            }

            images.Add(new
            {
                id = Guid.NewGuid().ToString("N"),
                url = dataUri ?? item.ImageUri?.ToString() ?? string.Empty,
                localPath,
                revisedPrompt = item.RevisedPrompt
            });
        }

        return new
        {
            type,
            requestId,
            ok = true,
            images,
            durationMs = result.Duration.TotalMilliseconds,
            modelProfileId = result.ModelProfileId
        };
    }

    private async Task<object> HandleEditAsync(string type, JsonElement payload, string? requestId, CancellationToken cancellationToken)
    {
        var prompt = ReadRequiredString(payload, "prompt");
        var imageBase64 = ReadRequiredString(payload, "imageBase64");
        var maskBase64 = ReadOptionalString(payload, "maskBase64");
        var modelProfileId = ReadOptionalGuid(payload, "modelProfileId", "profileId");
        var model = ReadOptionalString(payload, "model");
        var count = ReadOptionalInt(payload, "count") ?? 1;
        var size = ReadOptionalString(payload, "size") ?? "1024x1024";

        var imageBytes = Convert.FromBase64String(StripDataUriPrefix(imageBase64));
        ReadOnlyMemory<byte>? maskBytes = null;
        if (!string.IsNullOrWhiteSpace(maskBase64))
        {
            maskBytes = Convert.FromBase64String(StripDataUriPrefix(maskBase64));
        }

        var request = new ImageEditRequest(
            imageBytes,
            prompt,
            maskBytes,
            Model: model,
            Count: count,
            Size: size,
            ResponseFormat: ImageGenerationResponseFormat.Bytes);

        var result = await _imageService.GenerateImageEditsAsync(modelProfileId, request, cancellationToken);
        var images = new List<object>(result.Images.Count);

        var outputDir = Path.Combine(_storagePaths.AppDataDirectory, "generated-images", DateTime.UtcNow.ToString("yyyyMMdd"));
        Directory.CreateDirectory(outputDir);

        foreach (var item in result.Images)
        {
            string? localPath = null;
            string? dataUri = null;

            if (item.ImageBytes is { } bytes && !bytes.IsEmpty)
            {
                var fileName = $"{Guid.NewGuid():N}.png";
                localPath = Path.Combine(outputDir, fileName);
                await File.WriteAllBytesAsync(localPath, bytes.ToArray(), cancellationToken);
                dataUri = $"data:image/png;base64,{Convert.ToBase64String(bytes.Span)}";
            }

            images.Add(new
            {
                id = Guid.NewGuid().ToString("N"),
                url = dataUri ?? item.ImageUri?.ToString() ?? string.Empty,
                localPath,
                revisedPrompt = item.RevisedPrompt
            });
        }

        return new
        {
            type,
            requestId,
            ok = true,
            images,
            durationMs = result.Duration.TotalMilliseconds
        };
    }

    private static string StripDataUriPrefix(string base64OrDataUri)
    {
        var commaIndex = base64OrDataUri.IndexOf(',');
        if (base64OrDataUri.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && commaIndex >= 0)
        {
            return base64OrDataUri[(commaIndex + 1)..];
        }
        return base64OrDataUri;
    }

    private static string ReadRequiredString(JsonElement payload, string name)
    {
        if (payload.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString()!;
        }
        throw new ArgumentException($"Missing required string property '{name}'.");
    }

    private static string? ReadOptionalString(JsonElement payload, string name)
    {
        return payload.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }

    private static Guid? ReadOptionalGuid(JsonElement payload, params string[] names)
    {
        foreach (var name in names)
        {
            if (payload.TryGetProperty(name, out var prop) &&
                prop.ValueKind == JsonValueKind.String &&
                Guid.TryParse(prop.GetString(), out var guid))
            {
                return guid;
            }
        }
        return null;
    }

    private static int? ReadOptionalInt(JsonElement payload, string name)
    {
        return payload.TryGetProperty(name, out var prop) && prop.TryGetInt32(out var value)
            ? value
            : null;
    }
}
