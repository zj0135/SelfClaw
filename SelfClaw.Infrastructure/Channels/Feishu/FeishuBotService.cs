using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SelfClaw.Infrastructure.Channels.Feishu;

/// <summary>
/// High-level Feishu service that reproduces the TypeScript provider behavior.
/// It parses incoming events, resolves names, filters group mentions and exposes helper APIs used by the app.
/// </summary>
public sealed class FeishuBotService : IAsyncDisposable
{
    private const int StreamThrottleMs = 500;
    private const int StreamCardMaxDurationMs = 45_000;
    private const int StreamCardMaxChars = 3_500;
    private const int StreamCardMinRotateChars = 800;

    private readonly FeishuChannelOptions _options;
    private readonly Func<FeishuIncomingMessage, CancellationToken, Task> _incomingMessageHandler;
    private readonly Action<bool>? _runningStateChanged;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly FeishuLongConnectionClient _longConnectionClient;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly ConcurrentDictionary<string, string> _chatNameCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _userNameCache = new(StringComparer.Ordinal);
    private readonly HashSet<string> _processedMessageIds = new(StringComparer.Ordinal);
    private readonly Queue<string> _processedMessageOrder = new();
    private readonly object _processedLock = new();

    private string _botOpenId = string.Empty;

    public FeishuBotService(
        FeishuChannelOptions options,
        Func<FeishuIncomingMessage, CancellationToken, Task> incomingMessageHandler,
        Action<bool>? runningStateChanged = null)
    {
        _options = options;
        _incomingMessageHandler = incomingMessageHandler;
        _runningStateChanged = runningStateChanged;
        _httpClient = options.HttpClient ?? FeishuApiClient.CreateDefaultHttpClient();
        _ownsHttpClient = options.HttpClient is null;
        Api = new FeishuApiClient(options.AppId, options.AppSecret, _httpClient, options.BaseUrl, options.Log);
        _longConnectionClient = new FeishuLongConnectionClient(
            options.AppId,
            options.AppSecret,
            _httpClient,
            HandleIncomingPayloadAsync,
            options.BaseUrl,
            options.Log);
    }

    public FeishuApiClient Api { get; }

    public bool IsRunning => _longConnectionClient.IsRunning;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ValidateOptions();

        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            if (IsRunning)
                return;

            await Api.EnsureTokenAsync(cancellationToken);
            try
            {
                var botInfo = await Api.GetBotInfoAsync(cancellationToken);
                _botOpenId = botInfo.OpenId;
                _options.Log?.Invoke($"[Feishu] Bot identity: {botInfo.AppName} ({botInfo.OpenId})");
            }
            catch (Exception ex)
            {
                _options.Log?.Invoke($"[Feishu] Failed to resolve bot identity: {ex.Message}");
            }

            await _longConnectionClient.StartAsync(cancellationToken);
            _runningStateChanged?.Invoke(true);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            await _longConnectionClient.StopAsync(cancellationToken);
            _runningStateChanged?.Invoke(false);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await _longConnectionClient.DisposeAsync();
        _lifecycleLock.Dispose();
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }

    public async Task<FeishuMessageResult> SendMessageAsync(
        string chatId,
        string content,
        CancellationToken cancellationToken = default)
    {
        var trimmed = content?.Trim();
        if (!string.IsNullOrWhiteSpace(trimmed) && trimmed.StartsWith('{') && FeishuJson.TryParseDocument(trimmed, out var document) && document is not null)
        {
            using (document)
            {
                var root = document.RootElement;
                var msgType = FeishuJson.GetString(root, "msg_type");
                var payload = FeishuJson.GetPropertyOrNull(root, "content");
                if (!string.IsNullOrWhiteSpace(msgType) && payload is not null)
                {
                    var serializedContent = msgType == "text"
                        ? payload.Value.ValueKind == JsonValueKind.String
                            ? payload.Value.GetString() ?? string.Empty
                            : payload.Value.GetRawText()
                        : payload.Value.GetRawText();

                    return await Api.SendMessageAsync(chatId, serializedContent, msgType, cancellationToken);
                }
            }
        }

        return await Api.SendMessageAsync(chatId, content ?? string.Empty, cancellationToken: cancellationToken);
    }

    public Task<FeishuMessageResult> ReplyMessageAsync(
        string messageId,
        string content,
        CancellationToken cancellationToken = default)
    {
        return Api.ReplyMessageAsync(messageId, content, cancellationToken);
    }

    public Task<IReadOnlyList<FeishuChatMessage>> GetGroupMessagesAsync(
        string chatId,
        int count = 20,
        CancellationToken cancellationToken = default)
    {
        return Api.GetMessagesAsync(chatId, count, cancellationToken);
    }

    public Task<IReadOnlyList<FeishuChatSummary>> ListGroupsAsync(CancellationToken cancellationToken = default)
    {
        return Api.ListChatsAsync(cancellationToken);
    }

    public async Task<IFeishuStreamingHandle> SendStreamingMessageAsync(
        string chatId,
        string initialContent,
        string? replyToMessageId = null,
        CancellationToken cancellationToken = default)
    {
        var handle = new FeishuStreamingMessageHandle(
            Api,
            _options.BotDisplayName ?? "AI Assistant",
            chatId,
            replyToMessageId,
            StreamThrottleMs,
            StreamCardMaxDurationMs,
            StreamCardMaxChars,
            StreamCardMinRotateChars,
            cancellationToken);
        await handle.InitializeAsync(initialContent, cancellationToken);
        return handle;
    }

    public async Task<FeishuMessageResult> SendImageAsync(
        string chatId,
        string source,
        CancellationToken cancellationToken = default)
    {
        var resource = await ReadBinarySourceAsync(source, "image.png", cancellationToken);
        var imageKey = await Api.UploadImageAsync(resource.Content, resource.FileName ?? "image.png", cancellationToken);
        return await Api.SendImageMessageAsync(chatId, imageKey, cancellationToken);
    }

    public async Task<FeishuMessageResult> SendFileAsync(
        string chatId,
        string source,
        FeishuFileType? fileType = null,
        CancellationToken cancellationToken = default)
    {
        var resource = await ReadBinarySourceAsync(source, "file", cancellationToken);
        var resolvedFileName = resource.FileName ?? "file";
        var resolvedFileType = fileType ?? FeishuValueConverters.DetectFileType(resolvedFileName);
        var fileKey = await Api.UploadFileAsync(resource.Content, resolvedFileName, resolvedFileType, cancellationToken);
        return await Api.SendFileMessageAsync(chatId, fileKey, cancellationToken);
    }

    public async Task<FeishuMessageResult> SendMentionAsync(
        string chatId,
        IReadOnlyList<string>? userIds,
        bool atAll,
        string text,
        CancellationToken cancellationToken = default)
    {
        var chatInfo = await Api.GetChatInfoAsync(chatId, cancellationToken);
        if (!string.Equals(chatInfo?.ChatType, "group", StringComparison.Ordinal))
            throw new InvalidOperationException("Feishu mention is only available in group chats.");

        var contentRow = new JsonArray();
        if (atAll)
        {
            contentRow.Add(new JsonObject
            {
                ["tag"] = "at",
                ["user_id"] = "all"
            });
        }

        foreach (var userId in userIds ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(userId))
                continue;

            contentRow.Add(new JsonObject
            {
                ["tag"] = "at",
                ["user_id"] = userId
            });
        }

        if (!string.IsNullOrWhiteSpace(text))
        {
            contentRow.Add(new JsonObject
            {
                ["tag"] = "text",
                ["text"] = contentRow.Count > 0 ? $" {text.Trim()}" : text.Trim()
            });
        }

        if (contentRow.Count == 0)
            throw new InvalidOperationException("Mention message content is empty.");

        var postContent = new JsonObject
        {
            ["zh_cn"] = new JsonObject
            {
                ["content"] = new JsonArray { contentRow }
            }
        };

        return await Api.SendMessageAsync(chatId, postContent.ToJsonString(), "post", cancellationToken);
    }

    public Task<FeishuMemberPage> ListMembersAsync(
        string chatId,
        string? pageToken = null,
        int pageSize = 50,
        string memberIdType = "open_id",
        CancellationToken cancellationToken = default)
    {
        return Api.ListChatMembersAsync(chatId, pageToken, pageSize, memberIdType, cancellationToken);
    }

    public async Task SendUrgentAsync(
        string messageId,
        IReadOnlyList<string> userIds,
        IReadOnlyList<FeishuUrgentType> urgentTypes,
        CancellationToken cancellationToken = default)
    {
        foreach (var urgentType in urgentTypes)
        {
            await Api.SendUrgentAsync(messageId, userIds, urgentType, cancellationToken: cancellationToken);
        }
    }

    public Task<FeishuBinaryResource> DownloadResourceAsync(
        string messageId,
        string fileKey,
        FeishuMessageResourceType resourceType = FeishuMessageResourceType.File,
        CancellationToken cancellationToken = default)
    {
        return Api.DownloadMessageResourceWithMetadataAsync(messageId, fileKey, resourceType, cancellationToken);
    }

    public Task<JsonElement> ListBitableAppsAsync(int pageSize = 50, string? pageToken = null, CancellationToken cancellationToken = default)
        => Api.ListBitableAppsAsync(pageSize, pageToken, cancellationToken);

    public Task<JsonElement> ListBitableTablesAsync(string appToken, int pageSize = 100, string? pageToken = null, CancellationToken cancellationToken = default)
        => Api.ListBitableTablesAsync(appToken, pageSize, pageToken, cancellationToken);

    public Task<JsonElement> ListBitableFieldsAsync(string appToken, string tableId, int pageSize = 200, string? pageToken = null, CancellationToken cancellationToken = default)
        => Api.ListBitableFieldsAsync(appToken, tableId, pageSize, pageToken, cancellationToken);

    public Task<JsonElement> GetBitableRecordsAsync(string appToken, string tableId, int pageSize = 50, string? pageToken = null, string? filter = null, CancellationToken cancellationToken = default)
        => Api.GetBitableRecordsAsync(appToken, tableId, pageSize, pageToken, filter, cancellationToken);

    public Task<JsonElement> CreateBitableRecordsAsync(string appToken, string tableId, JsonElement records, CancellationToken cancellationToken = default)
        => Api.CreateBitableRecordsAsync(appToken, tableId, records, cancellationToken);

    public Task<JsonElement> UpdateBitableRecordsAsync(string appToken, string tableId, JsonElement records, CancellationToken cancellationToken = default)
        => Api.UpdateBitableRecordsAsync(appToken, tableId, records, cancellationToken);

    public Task<JsonElement> DeleteBitableRecordsAsync(string appToken, string tableId, IReadOnlyList<string> recordIds, CancellationToken cancellationToken = default)
        => Api.DeleteBitableRecordsAsync(appToken, tableId, recordIds, cancellationToken);

    private async Task HandleIncomingPayloadAsync(string rawPayload, CancellationToken cancellationToken)
    {
        if (!FeishuJson.TryParseDocument(rawPayload, out var document) || document is null)
            return;

        using (document)
        {
            var root = document.RootElement;
            if (IsOfficialMessageEvent(root))
            {
                await HandleOfficialMessageEventAsync(root, cancellationToken);
                return;
            }

            if (!string.IsNullOrWhiteSpace(FeishuJson.GetString(root, "chatId")) &&
                !string.IsNullOrWhiteSpace(FeishuJson.GetString(root, "content")))
            {
                await _incomingMessageHandler(new FeishuIncomingMessage
                {
                    ChatId = FeishuJson.GetString(root, "chatId"),
                    SenderId = FeishuJson.GetString(root, "senderId"),
                    SenderName = FeishuJson.GetString(root, "senderName"),
                    Content = FeishuJson.GetString(root, "content"),
                    MessageId = FeishuJson.GetString(root, "messageId"),
                    Timestamp = FeishuJson.GetNestedInt64(root, "timestamp") ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    RawEvent = root.Clone()
                }, cancellationToken);
            }
        }
    }

    private async Task HandleOfficialMessageEventAsync(JsonElement root, CancellationToken cancellationToken)
    {
        var eventElement = FeishuJson.GetPropertyOrThrow(root, "event");
        var message = FeishuJson.GetPropertyOrThrow(eventElement, "message");
        var sender = FeishuJson.GetPropertyOrThrow(eventElement, "sender");

        var chatId = FeishuJson.GetString(message, "chat_id");
        var messageId = FeishuJson.GetString(message, "message_id");
        var messageType = FeishuJson.GetString(message, "message_type", "text");
        var chatType = FeishuJson.GetString(message, "chat_type", "p2p");
        var senderOpenId = FeishuJson.GetNestedString(sender, string.Empty, "sender_id", "open_id");
        var senderUserId = FeishuJson.GetNestedString(sender, string.Empty, "sender_id", "user_id");
        var senderId = FirstNonEmpty(senderOpenId, senderUserId);
        var senderIdType = !string.IsNullOrWhiteSpace(senderOpenId) ? "open_id" : "user_id";

        if (string.Equals(chatType, "group", StringComparison.Ordinal) && !IsBotMentioned(message))
            return;

        if (!RegisterMessage(messageId))
        {
            _options.Log?.Invoke($"[Feishu] Skipping duplicate message {messageId}");
            return;
        }

        var content = string.Empty;
        List<FeishuImageAttachment>? images = null;
        FeishuAudioAttachment? audio = null;
        var mentions = ExtractMentions(message);
        var rawContent = FeishuJson.GetString(message, "content");

        if (FeishuJson.TryParseDocument(rawContent, out var contentDocument) && contentDocument is not null)
        {
            using (contentDocument)
            {
                var contentRoot = contentDocument.RootElement;
                if (string.Equals(messageType, "image", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(FeishuJson.GetString(contentRoot, "image_key")))
                {
                    content = "[User sent an image]";
                    try
                    {
                        var imageKey = FeishuJson.GetString(contentRoot, "image_key");
                        var image = await Api.DownloadMessageResourceWithMetadataAsync(
                            messageId,
                            imageKey,
                            FeishuMessageResourceType.Image,
                            cancellationToken);
                        images =
                        [
                            new FeishuImageAttachment(
                                Convert.ToBase64String(image.Content),
                                image.MediaType ?? "image/png")
                        ];
                    }
                    catch (Exception ex)
                    {
                        content = $"[User sent an image but download failed: {ex.Message}]";
                    }
                }
                else if (string.Equals(messageType, "audio", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(FeishuJson.GetString(contentRoot, "file_key")))
                {
                    audio = new FeishuAudioAttachment(
                        FeishuJson.GetString(contentRoot, "file_key"),
                        FeishuJson.GetString(contentRoot, "file_name"),
                        FeishuJson.GetString(contentRoot, "media_type"),
                        FeishuJson.GetInt32(contentRoot, "duration"));
                }
                else
                {
                    content = FeishuJson.GetString(contentRoot, "text", rawContent);
                }
            }
        }
        else
        {
            content = rawContent;
        }

        if (!string.IsNullOrWhiteSpace(content) && mentions.Count > 0)
        {
            foreach (var mention in mentions)
            {
                if (string.IsNullOrWhiteSpace(mention.Key))
                    continue;

                content = Regex.Replace(content, Regex.Escape(mention.Key), string.Empty).Trim();
            }
        }

        if (string.IsNullOrWhiteSpace(chatId) || (string.IsNullOrWhiteSpace(content) && images is null && audio is null))
            return;

        var senderName = await ResolveSenderNameAsync(senderId, senderIdType, cancellationToken);
        var chatName = await ResolveChatNameAsync(chatId, senderName, chatType, cancellationToken);
        var timestamp = FeishuJson.ParseTimestampMilliseconds(
            FeishuJson.GetString(message, "create_time"),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        await _incomingMessageHandler(new FeishuIncomingMessage
        {
            ChatId = chatId,
            SenderId = senderId,
            SenderName = string.IsNullOrWhiteSpace(senderName) ? senderId : senderName,
            Content = content,
            MessageId = messageId,
            Timestamp = timestamp,
            Images = images,
            Audio = audio,
            MessageType = messageType,
            ChatName = chatName,
            ChatType = chatType,
            RawEvent = root.Clone()
        }, cancellationToken);
    }

    private bool IsBotMentioned(JsonElement message)
    {
        foreach (var mention in ExtractMentions(message))
        {
            if (string.Equals(mention.Key, "@_all", StringComparison.Ordinal))
                return true;
            if (!string.IsNullOrWhiteSpace(_botOpenId) && string.Equals(mention.OpenId, _botOpenId, StringComparison.Ordinal))
                return true;
            if (string.IsNullOrWhiteSpace(_botOpenId) &&
                !string.IsNullOrWhiteSpace(_options.BotDisplayName) &&
                string.Equals(mention.Name, _options.BotDisplayName, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private List<FeishuMention> ExtractMentions(JsonElement message)
    {
        var mentions = new List<FeishuMention>();
        var mentionArray = FeishuJson.GetPropertyOrNull(message, "mentions");
        if (mentionArray is not { ValueKind: JsonValueKind.Array })
            return mentions;

        foreach (var mention in mentionArray.Value.EnumerateArray())
        {
            mentions.Add(new FeishuMention(
                FeishuJson.GetString(mention, "key"),
                FeishuJson.GetNestedString(mention, string.Empty, "id", "open_id"),
                FeishuJson.GetString(mention, "name")));
        }

        return mentions;
    }

    private async Task<string> ResolveSenderNameAsync(string senderId, string senderIdType, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(senderId))
            return string.Empty;

        if (_userNameCache.TryGetValue(senderId, out var cachedName))
            return cachedName;

        var profile = await Api.GetUserProfileAsync(senderId, senderIdType, cancellationToken);
        var name = profile?.Name?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(name))
            _userNameCache[senderId] = name;

        return name;
    }

    private async Task<string> ResolveChatNameAsync(string chatId, string senderName, string chatType, CancellationToken cancellationToken)
    {
        if (_chatNameCache.TryGetValue(chatId, out var cachedName))
            return cachedName;

        var chatInfo = await Api.GetChatInfoAsync(chatId, cancellationToken);
        var chatName = chatInfo?.Name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(chatName) && string.Equals(chatType, "p2p", StringComparison.Ordinal))
            chatName = senderName;

        if (!string.IsNullOrWhiteSpace(chatName))
            _chatNameCache[chatId] = chatName;

        return chatName;
    }

    private bool RegisterMessage(string messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId))
            return true;

        lock (_processedLock)
        {
            if (_processedMessageIds.Contains(messageId))
                return false;

            _processedMessageIds.Add(messageId);
            _processedMessageOrder.Enqueue(messageId);
            while (_processedMessageOrder.Count > 500)
            {
                var expired = _processedMessageOrder.Dequeue();
                _processedMessageIds.Remove(expired);
            }

            return true;
        }
    }

    private void ValidateOptions()
    {
        if (string.IsNullOrWhiteSpace(_options.AppId) || string.IsNullOrWhiteSpace(_options.AppSecret))
            throw new InvalidOperationException("Feishu AppId and AppSecret are required.");
    }

    private static bool IsOfficialMessageEvent(JsonElement root)
    {
        return string.Equals(
                   FeishuJson.GetNestedString(root, string.Empty, "header", "event_type"),
                   "im.message.receive_v1",
                   StringComparison.Ordinal) &&
               FeishuJson.GetPropertyOrNull(root, "event") is { ValueKind: JsonValueKind.Object };
    }

    private static async Task<FeishuBinaryResource> ReadBinarySourceAsync(
        string source,
        string fallbackName,
        CancellationToken cancellationToken)
    {
        var value = source.Trim();
        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var content = await FeishuApiClient.DownloadUrlAsync(value, cancellationToken);
            return new FeishuBinaryResource(content, null, ResolveSourceFileName(value, fallbackName));
        }

        if (!File.Exists(value))
            throw new FileNotFoundException($"File not found: {value}", value);

        return new FeishuBinaryResource(
            await File.ReadAllBytesAsync(value, cancellationToken),
            null,
            Path.GetFileName(value));
    }

    private static string ResolveSourceFileName(string source, string fallback)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            var fileName = Path.GetFileName(uri.AbsolutePath);
            return string.IsNullOrWhiteSpace(fileName) ? fallback : Uri.UnescapeDataString(fileName);
        }

        var sanitized = source.Split('?')[0];
        return Path.GetFileName(sanitized);
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return string.Empty;
    }

    private sealed record FeishuMention(string Key, string OpenId, string Name);

    private sealed class FeishuStreamingMessageHandle : IFeishuStreamingHandle
    {
        private readonly FeishuApiClient _api;
        private readonly string _title;
        private readonly string _chatId;
        private readonly string? _replyToMessageId;
        private readonly int _throttleMs;
        private readonly int _maxDurationMs;
        private readonly int _maxChars;
        private readonly int _minRotateChars;
        private readonly SemaphoreSlim _mutex = new(1, 1);

        private string _currentCardId = string.Empty;
        private int _currentCardIndex = 1;
        private int _currentSequence;
        private long _lastUpdateAtMs;
        private long _currentCardStartedAtMs;
        private int _segmentStartOffset;
        private bool _rotationPending;
        private int _rotationOffset;
        private readonly CancellationToken _defaultCancellationToken;

        public FeishuStreamingMessageHandle(
            FeishuApiClient api,
            string title,
            string chatId,
            string? replyToMessageId,
            int throttleMs,
            int maxDurationMs,
            int maxChars,
            int minRotateChars,
            CancellationToken defaultCancellationToken)
        {
            _api = api;
            _title = title;
            _chatId = chatId;
            _replyToMessageId = replyToMessageId;
            _throttleMs = throttleMs;
            _maxDurationMs = maxDurationMs;
            _maxChars = maxChars;
            _minRotateChars = minRotateChars;
            _defaultCancellationToken = defaultCancellationToken;
        }

        public async Task InitializeAsync(string initialContent, CancellationToken cancellationToken)
        {
            _currentCardId = await CreateCardAsync(initialContent, isFirstCard: true, cancellationToken);
            _currentCardStartedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        public async Task UpdateAsync(string content, CancellationToken cancellationToken = default)
        {
            cancellationToken = MergeCancellationToken(cancellationToken);
            await _mutex.WaitAsync(cancellationToken);
            try
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (now - _lastUpdateAtMs < _throttleMs)
                    return;

                _lastUpdateAtMs = now;
                await EnsureNextCardIfNeededAsync(content, cancellationToken);
                var segmentContent = content.Length > _segmentStartOffset
                    ? content[_segmentStartOffset..]
                    : "Thinking...";
                await FlushCurrentCardAsync(segmentContent, cancellationToken);
                MarkRotationIfNeeded(content, now);
            }
            finally
            {
                _mutex.Release();
            }
        }

        public async Task FinishAsync(string finalContent, CancellationToken cancellationToken = default)
        {
            cancellationToken = MergeCancellationToken(cancellationToken);
            await _mutex.WaitAsync(cancellationToken);
            try
            {
                await EnsureNextCardIfNeededAsync(finalContent, cancellationToken);
                var segmentContent = finalContent.Length > _segmentStartOffset
                    ? finalContent[_segmentStartOffset..]
                    : "Done.";
                await FlushCurrentCardAsync(segmentContent, cancellationToken);
            }
            finally
            {
                _mutex.Release();
            }
        }

        private async Task<string> CreateCardAsync(string content, bool isFirstCard, CancellationToken cancellationToken)
        {
            var title = BuildCardTitle(_currentCardIndex);
            var card = await _api.CreateCardAsync(string.IsNullOrWhiteSpace(content) ? "Thinking..." : content, title, cancellationToken);
            if (isFirstCard && !string.IsNullOrWhiteSpace(_replyToMessageId))
                await _api.ReplyCardMessageAsync(_replyToMessageId, card.MessageId, cancellationToken);
            else
                await _api.SendCardMessageAsync(_chatId, card.MessageId, cancellationToken);

            _currentSequence = 0;
            return card.MessageId;
        }

        private async Task FlushCurrentCardAsync(string content, CancellationToken cancellationToken)
        {
            _currentSequence += 1;
            await _api.UpdateCardAsync(
                _currentCardId,
                string.IsNullOrWhiteSpace(content) ? "Thinking..." : content,
                _currentSequence,
                BuildCardTitle(_currentCardIndex),
                cancellationToken);
        }

        private void MarkRotationIfNeeded(string fullContent, long nowMs)
        {
            var currentSegmentLength = Math.Max(0, fullContent.Length - _segmentStartOffset);
            var shouldRotate = currentSegmentLength >= _minRotateChars &&
                               (nowMs - _currentCardStartedAtMs >= _maxDurationMs ||
                                currentSegmentLength >= _maxChars);
            if (!shouldRotate)
                return;

            _rotationPending = true;
            _rotationOffset = fullContent.Length;
        }

        private async Task EnsureNextCardIfNeededAsync(string fullContent, CancellationToken cancellationToken)
        {
            if (!_rotationPending || fullContent.Length <= _rotationOffset)
                return;

            _currentCardIndex += 1;
            _currentCardId = await CreateCardAsync("Continuing...", isFirstCard: false, cancellationToken);
            _segmentStartOffset = _rotationOffset;
            _currentCardStartedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _rotationPending = false;
            _rotationOffset = 0;
        }

        private string BuildCardTitle(int index)
        {
            return index <= 1 ? _title : $"{_title} (Part {index})";
        }

        private CancellationToken MergeCancellationToken(CancellationToken cancellationToken)
        {
            if (!cancellationToken.CanBeCanceled)
                return _defaultCancellationToken;
            if (!_defaultCancellationToken.CanBeCanceled)
                return cancellationToken;
            return CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _defaultCancellationToken).Token;
        }
    }
}



