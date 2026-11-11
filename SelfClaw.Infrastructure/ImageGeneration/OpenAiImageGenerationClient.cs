using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using OpenAI.Images;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;

namespace SelfClaw.Infrastructure.ImageGeneration;

#pragma warning disable OPENAI001

internal sealed partial class OpenAiImageGenerationClient : IImageGenerationClient
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
        // Both fields are omitted for families that do not accept them (gpt-image answers 400 to
        // response_format and style), which is why neither is set unconditionally.
        if (_capabilities.Styles.Count > 0)
        {
            options.Style = ResolveStyle(request.Style);
        }

        ApplyOutputKnobs(
            options,
            request.Background,
            request.OutputFormat,
            request.OutputCompression,
            request.Moderation);

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

        var options = new ImageEditOptions
        {
            Size = ResolveSize(request.Size),
            Quality = ResolveQuality(request.Quality)
        };
        ApplyOutputKnobs(
            options,
            request.Background,
            request.OutputFormat,
            request.OutputCompression);

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

    /// <summary>
    /// GPT Image-only knobs are applied only when the capability says so; leaving the SDK properties
    /// null keeps them out of the JSON entirely, which is what keeps arbitrary gateways working.
    /// </summary>
    private void ApplyOutputKnobs(
        ImageGenerationOptions options,
        ImageGenerationBackground? background,
        ImageGenerationOutputFormat? outputFormat,
        int? outputCompression,
        ImageGenerationModeration? moderation)
    {
        var knobs = ValidateOutputKnobs(background, outputFormat, outputCompression);
        if (knobs.Background is { } bg)
        {
            options.Background = ToSdkBackground(bg);
        }

        if (knobs.OutputFormat is { } format)
        {
            options.OutputFileFormat = ToSdkFormat(format);
        }

        if (knobs.OutputCompression is { } compression)
        {
            options.OutputCompressionFactor = compression;
        }

        // moderation is a generation-only field: ImageEditOptions has no equivalent.
        if (moderation is not null)
        {
            EnsureAllowed(moderation.Value, _capabilities.Moderations, nameof(moderation));
            options.ModerationLevel = moderation.Value == ImageGenerationModeration.Low
                ? GeneratedImageModerationLevel.Low
                : GeneratedImageModerationLevel.Auto;
        }
        EnsureTransparentFormat(background, outputFormat);
    }

    private void ApplyOutputKnobs(
        ImageEditOptions options,
        ImageGenerationBackground? background,
        ImageGenerationOutputFormat? outputFormat,
        int? outputCompression)
    {
        var knobs = ValidateOutputKnobs(background, outputFormat, outputCompression);
        if (knobs.Background is { } bg)
        {
            options.Background = ToSdkBackground(bg);
        }

        if (knobs.OutputFormat is { } format)
        {
            options.OutputFileFormat = ToSdkFormat(format);
        }

        if (knobs.OutputCompression is { } compression)
        {
            options.OutputCompressionFactor = compression;
        }
        EnsureTransparentFormat(background, outputFormat);
    }

    /// <summary>JPEG has no alpha channel, so a transparent background cannot be delivered as JPEG.</summary>
    private static void EnsureTransparentFormat(
        ImageGenerationBackground? background,
        ImageGenerationOutputFormat? outputFormat)
    {
        if (background == ImageGenerationBackground.Transparent
            && outputFormat == ImageGenerationOutputFormat.Jpeg)
        {
            throw new ArgumentException(
                "透明背景不支持 JPEG 输出，请改用 PNG 或 WebP。", nameof(outputFormat));
        }
    }

    private (
        ImageGenerationBackground? Background,
        ImageGenerationOutputFormat? OutputFormat,
        int? OutputCompression) ValidateOutputKnobs(
        ImageGenerationBackground? background,
        ImageGenerationOutputFormat? outputFormat,
        int? outputCompression)
    {
        if (background is not null)
        {
            EnsureAllowed(background.Value, _capabilities.Backgrounds, nameof(background));
        }

        if (outputFormat is not null)
        {
            EnsureAllowed(outputFormat.Value, _capabilities.OutputFormats, nameof(outputFormat));
        }

        if (outputCompression is int compression)
        {
            if (!_capabilities.SupportsOutputCompression)
            {
                throw new ArgumentException(
                    $"Model '{_model}' does not support output_compression.", nameof(outputCompression));
            }

            if (compression is < 0 or > 100)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(outputCompression), compression, "output_compression must be between 0 and 100.");
            }
        }

        return (background, outputFormat, outputCompression);
    }

    private void EnsureAllowed<T>(T value, IReadOnlyList<T>? allowed, string name) where T : struct, Enum
    {
        if (allowed is null || allowed.Count == 0 || !allowed.Contains(value))
        {
            throw new ArgumentException(
                $"Model '{_model}' does not support {name} '{ToWireName(value)}'.", name);
        }
    }

    private GeneratedImageSize ResolveSize(string? size)
    {
        var value = string.IsNullOrWhiteSpace(size) ? _capabilities.Sizes[0] : size.Trim().ToLowerInvariant();

        if (value == "auto")
        {
            return _capabilities.Sizes.Contains("auto")
                ? GeneratedImageSize.Auto
                : throw new ArgumentException($"Model '{_model}' does not support size 'auto'.", nameof(size));
        }

        if (_capabilities.Sizes.Contains(value))
        {
            // The SDK exposes well-known sizes as constants, but its GeneratedImageSize(int,int)
            // constructor serialises any WxH correctly (verified by capturing the request body).
            // Parsing presets generically means adding a new preset never requires touching this
            // client again.
            return ParseWidthHeight(value);
        }

        // A free-form WIDTHxHEIGHT is only legal when the model advertises custom-size rules.
        if (_capabilities.CustomSize is not { } rules)
        {
            throw new ArgumentException(
                $"Model '{_model}' does not support size '{value}'. Supported sizes: {string.Join(", ", _capabilities.Sizes)}.",
                nameof(size));
        }

        var (width, height) = ParseWidthHeightDimensions(value);
        EnsureCustomSize(width, height, rules, value);
        return new GeneratedImageSize(width, height);
    }

    private static GeneratedImageSize ParseWidthHeight(string value)
    {
        var (width, height) = ParseWidthHeightDimensions(value);
        return new GeneratedImageSize(width, height);
    }

    private static (int Width, int Height) ParseWidthHeightDimensions(string value)
    {
        var match = CustomSizePattern().Match(value);
        if (!match.Success)
        {
            throw new ArgumentException($"Size '{value}' is not a valid WIDTHxHEIGHT value.", nameof(value));
        }

        return (
            int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
    }

    private void EnsureCustomSize(int width, int height, ImageGenerationCustomSizeRules rules, string value)
    {
        if (width <= 0 || height <= 0
            || width % rules.MultipleOf != 0 || height % rules.MultipleOf != 0)
        {
            throw new ArgumentException(
                $"Custom size '{value}' must have both dimensions as multiples of {rules.MultipleOf}.", nameof(value));
        }

        if (width > rules.MaxWidth || height > rules.MaxHeight)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value), value,
                $"Custom size '{value}' exceeds the maximum {rules.MaxWidth}x{rules.MaxHeight}.");
        }

        // Ratio must lie between 1:3 and 3:1; compare cross-products to stay in integer arithmetic.
        var ratio = (double)width / height;
        var minRatio = 1.0 / rules.MaxAspectRatioDividend;
        var maxRatio = (double)rules.MaxAspectRatioDividend;
        if (ratio < minRatio || ratio > maxRatio)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value), value,
                $"Custom size '{value}' must have an aspect ratio between 1:{rules.MaxAspectRatioDividend} and {rules.MaxAspectRatioDividend}:1.");
        }
    }

    private GeneratedImageQuality ResolveQuality(ImageGenerationQuality? quality)
    {
        var value = quality ?? _capabilities.Qualities[0];
        if (!_capabilities.Qualities.Contains(value))
        {
            throw new ArgumentException(
                $"Model '{_model}' does not support quality '{ToWireName(value)}'. Supported qualities: {string.Join(", ", _capabilities.Qualities)}.",
                nameof(quality));
        }

        // The SDK models these as extensible string values, so xhigh/max pass through without a
        // special case while the legacy tiers keep their strongly typed constants.
        return value switch
        {
            ImageGenerationQuality.Auto => GeneratedImageQuality.Auto,
            ImageGenerationQuality.Low => GeneratedImageQuality.LowQuality,
            ImageGenerationQuality.Medium => GeneratedImageQuality.MediumQuality,
            ImageGenerationQuality.High => GeneratedImageQuality.HighQuality,
            ImageGenerationQuality.Hd => GeneratedImageQuality.High,
            ImageGenerationQuality.XHigh => new GeneratedImageQuality("xhigh"),
            ImageGenerationQuality.Max => new GeneratedImageQuality("max"),
            _ => GeneratedImageQuality.Standard
        };
    }

    private static GeneratedImageStyle ResolveStyle(ImageGenerationStyle? style) =>
        style is ImageGenerationStyle.Natural ? GeneratedImageStyle.Natural : GeneratedImageStyle.Vivid;

    private static GeneratedImageBackground ToSdkBackground(ImageGenerationBackground value) => value switch
    {
        ImageGenerationBackground.Transparent => GeneratedImageBackground.Transparent,
        ImageGenerationBackground.Opaque => GeneratedImageBackground.Opaque,
        _ => GeneratedImageBackground.Auto
    };

    private static GeneratedImageFileFormat ToSdkFormat(ImageGenerationOutputFormat value) => value switch
    {
        ImageGenerationOutputFormat.Jpeg => GeneratedImageFileFormat.Jpeg,
        ImageGenerationOutputFormat.Webp => GeneratedImageFileFormat.Webp,
        _ => GeneratedImageFileFormat.Png
    };

    private static string ToWireName<T>(T value) where T : struct, Enum =>
        value switch
        {
            ImageGenerationQuality.XHigh => "xhigh",
            _ => value.ToString().ToLowerInvariant()
        };

    [GeneratedRegex(@"^(\d+)x(\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex CustomSizePattern();
}

#pragma warning restore OPENAI001
