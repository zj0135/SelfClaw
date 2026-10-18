using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services.Attachments.Abstractions;
using SelfClaw.Desktop.Services.WebView;
using SelfClaw.Infrastructure.Attachments;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Desktop.Services.Attachments;

/// <summary>
/// Maps <c>attachments/*</c> onto the host attachment store. The renderer never handles a filesystem
/// path: it picks through the host dialog or sends bytes, and gets back a reference plus a preview URL
/// that follows the transcript's own URL rule.
/// </summary>
internal sealed class AttachmentBridge
{
    private const string ResultType = "attachments/result";
    private const string OperationPrefix = "attachments/";
    // Base64 spends four characters per three bytes; the ceiling is checked before decoding so an
    // oversized paste is refused before its decoded buffer is allocated.
    private const int MaximumBase64Length = (AttachmentPathResolver.MaximumByteLength + 2) / 3 * 4;

    private readonly IAttachmentStore _attachments;
    private readonly IImageFilePicker _picker;
    private readonly WebViewHostChannel _channel;
    private readonly string _attachmentsRoot;
    private readonly Dispatcher _dispatcher;

    public AttachmentBridge(
        IAttachmentStore attachments,
        IImageFilePicker picker,
        WebViewHostChannel channel,
        StoragePaths storagePaths,
        Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(attachments);
        ArgumentNullException.ThrowIfNull(picker);
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(storagePaths);
        ArgumentNullException.ThrowIfNull(dispatcher);
        _attachments = attachments;
        _picker = picker;
        _channel = channel;
        _attachmentsRoot = AttachmentPathResolver.ResolveRoot(storagePaths.AppDataDirectory);
        _dispatcher = dispatcher;
    }

    /// <summary>
    /// The root the store writes under, already resolved once: the router validates queued references
    /// against it instead of resolving a second copy of the layout.
    /// </summary>
    internal string AttachmentsRoot => _attachmentsRoot;

    internal async Task HandleAsync(
        string type,
        JsonElement payload,
        nint ownerHandle,
        CancellationToken cancellationToken)
    {
        var requestId = ReadOptionalString(payload, "requestId");
        var operation = type.StartsWith(OperationPrefix, StringComparison.Ordinal)
            ? type[OperationPrefix.Length..]
            : type;
        try
        {
            IReadOnlyList<object> attachments;
            switch (operation)
            {
                case "pick":
                    attachments = await PickAsync(payload, ownerHandle, cancellationToken).ConfigureAwait(false);
                    break;
                case "ingest-bytes":
                    attachments = [await IngestBytesAsync(payload, cancellationToken).ConfigureAwait(false)];
                    break;
                case "discard":
                    await DiscardAsync(payload, cancellationToken).ConfigureAwait(false);
                    attachments = [];
                    break;
                default:
                    throw new ArgumentException($"不支持的附件操作「{type}」。");
            }

            await ReplyAsync(new { type = ResultType, requestId, operation, ok = true, attachments }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            await ReplyAsync(new { type = ResultType, requestId, operation, ok = false, error = exception.Message })
                .ConfigureAwait(false);
        }
    }

    private async Task<IReadOnlyList<object>> PickAsync(
        JsonElement payload,
        nint ownerHandle,
        CancellationToken cancellationToken)
    {
        var conversationId = ReadGuid(payload, "conversationId");
        var paths = _picker.Pick(ownerHandle, ResolvePickerDirectory());
        if (!ReadBoolean(payload, "multiple", fallback: true) && paths.Count > 1)
        {
            paths = [paths[0]];
        }

        var attachments = new List<object>(paths.Count);
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attachments.Add(await IngestFileAsync(conversationId, path, cancellationToken).ConfigureAwait(false));
        }

        return attachments;
    }

    private async Task<object> IngestFileAsync(Guid conversationId, string path, CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(path);
        var mediaType = ResolveMediaType(fileName);
        try
        {
            await using var content = File.OpenRead(path);
            var record = await _attachments
                .IngestAsync(conversationId, content, fileName, mediaType, cancellationToken)
                .ConfigureAwait(false);
            return ToResponse(record);
        }
        catch (ArgumentException exception)
        {
            // Store-side rejections (whitelist, magic bytes, size) share one readable line; the user only
            // needs to know the file was not added.
            throw new ArgumentException($"无法添加图片「{fileName}」：格式不受支持或超过 10 MB 上限。", exception);
        }
    }

    private async Task<object> IngestBytesAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var conversationId = ReadGuid(payload, "conversationId");
        var fileName = ReadRequiredString(payload, "fileName", 260);
        var mediaType = ReadRequiredString(payload, "mediaType", 64);
        var dataBase64 = ReadOptionalString(payload, "dataBase64")
            ?? throw new ArgumentException("缺少图片数据，请重新添加。");
        if (dataBase64.Length > MaximumBase64Length)
        {
            throw new ArgumentException($"单张图片不能超过 {AttachmentPathResolver.MaximumByteLength / (1024 * 1024)} MB。");
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(dataBase64);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("图片数据无效，请重新添加。", exception);
        }

        try
        {
            using var content = new MemoryStream(bytes);
            var record = await _attachments
                .IngestAsync(conversationId, content, fileName, mediaType, cancellationToken)
                .ConfigureAwait(false);
            return ToResponse(record);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException($"无法添加图片「{fileName}」：格式不受支持或超过 10 MB 上限。", exception);
        }
    }

    private Task DiscardAsync(JsonElement payload, CancellationToken cancellationToken)
        => _attachments.DeleteAsync(
            ReadGuid(payload, "conversationId"),
            ReadGuid(payload, "attachmentId"),
            cancellationToken);

    private object ToResponse(MessageAttachmentRecord record)
    {
        AttachmentPathResolver.TryCreateSourceUrl(_attachmentsRoot, record.StoragePath, out var previewUrl);
        return new
        {
            attachmentId = record.Id.ToString("D"),
            fileName = record.FileName,
            mediaType = record.MediaType,
            byteLength = record.ByteLength,
            previewUrl
        };
    }

    private Task ReplyAsync(object response)
    {
        if (_dispatcher.CheckAccess())
        {
            _channel.PostResponse(response);
            return Task.CompletedTask;
        }

        return _dispatcher.InvokeAsync(() => _channel.PostResponse(response)).Task;
    }

    private static string ResolveMediaType(string fileName)
        => AttachmentPathResolver.TryResolveMediaType(fileName, out var mediaType)
            ? mediaType
            : throw new ArgumentException("图片格式不受支持：只能添加 PNG、JPEG、WebP 或 GIF 图片。");

    private static string ResolvePickerDirectory()
    {
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        return !string.IsNullOrWhiteSpace(pictures) && Directory.Exists(pictures)
            ? pictures
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private static string? ReadOptionalString(JsonElement payload, string propertyName)
    {
        if (!payload.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = element.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string ReadRequiredString(JsonElement payload, string propertyName, int maximumLength)
    {
        var value = ReadOptionalString(payload, propertyName)
            ?? throw new ArgumentException($"缺少 {propertyName}。");
        return value.Length <= maximumLength
            ? value
            : throw new ArgumentException($"{propertyName} 过长。");
    }

    private static Guid ReadGuid(JsonElement payload, string propertyName)
        => Guid.TryParse(ReadOptionalString(payload, propertyName), out var value) && value != Guid.Empty
            ? value
            : throw new ArgumentException($"{propertyName} 必须是有效的标识。");

    private static bool ReadBoolean(JsonElement payload, string propertyName, bool fallback)
        => payload.TryGetProperty(propertyName, out var element) && element.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? element.GetBoolean()
            : fallback;
}
