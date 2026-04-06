using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SelfClaw.Infrastructure.Channl.Feishu;

namespace SelfClaw.Infrastructure.Channl.Feishu;

/// <summary>
/// Thin REST client that mirrors the Feishu API surface used by the Electron implementation.
/// </summary>
public sealed class FeishuApiClient
{
    private static readonly HttpClient SharedDownloadClient = CreateDefaultHttpClient();

    private readonly HttpClient _httpClient;
    private readonly string _appId;
    private readonly string _appSecret;
    private readonly Uri _baseUri;
    private readonly Action<string>? _log;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    private string _accessToken = string.Empty;
    private DateTimeOffset _tokenExpiresAtUtc = DateTimeOffset.MinValue;

    public const string DefaultBaseUrl = "https://open.feishu.cn";

    public FeishuApiClient(
        string appId,
        string appSecret,
        HttpClient httpClient,
        string baseUrl = DefaultBaseUrl,
        Action<string>? log = null)
    {
        _appId = appId;
        _appSecret = appSecret;
        _httpClient = httpClient;
        _baseUri = new Uri(baseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        _log = log;
    }

    public static HttpClient CreateDefaultHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 10,
            UseCookies = false
        };

        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(100)
        };
    }

    public async Task<string> EnsureTokenAsync(CancellationToken cancellationToken = default)
    {
        if (HasUsableToken())
            return _accessToken;

        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            if (HasUsableToken())
                return _accessToken;

            var body = new JsonObject
            {
                ["app_id"] = _appId,
                ["app_secret"] = _appSecret
            };

            using var document = await SendJsonAsync(
                HttpMethod.Post,
                "/open-apis/auth/v3/tenant_access_token/internal",
                FeishuJson.CreateJsonContent(body),
                includeAuth: false,
                cancellationToken: cancellationToken);

            var root = document.RootElement;
            FeishuJson.EnsureFeishuSuccess(root, "auth");

            _accessToken = FeishuJson.GetString(root, "tenant_access_token");
            var expireSeconds = FeishuJson.GetInt32(root, "expire") ?? 7200;
            _tokenExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expireSeconds - 60));
            return _accessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    public async Task<FeishuBotInfo> GetBotInfoAsync(CancellationToken cancellationToken = default)
    {
        using var document = await SendJsonAsync(
            HttpMethod.Get,
            "/open-apis/bot/v3/info",
            cancellationToken: cancellationToken);

        var root = document.RootElement;
        FeishuJson.EnsureFeishuSuccess(root, "getBotInfo");
        return new FeishuBotInfo(
            FeishuJson.GetNestedString(root, string.Empty, "bot", "open_id"),
            FeishuJson.GetNestedString(root, string.Empty, "bot", "app_name"));
    }

    public async Task<FeishuMessageResult> SendMessageAsync(
        string chatId,
        string content,
        string messageType = "text",
        CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["receive_id"] = chatId,
            ["msg_type"] = messageType,
            ["content"] = messageType == "text"
                ? new JsonObject { ["text"] = content }.ToJsonString()
                : content
        };

        using var document = await SendJsonAsync(
            HttpMethod.Post,
            "/open-apis/im/v1/messages?receive_id_type=chat_id",
            FeishuJson.CreateJsonContent(body),
            cancellationToken: cancellationToken);

        var root = document.RootElement;
        FeishuJson.EnsureFeishuSuccess(root, "sendMessage");
        return new FeishuMessageResult(FeishuJson.GetNestedString(root, string.Empty, "data", "message_id"));
    }

    public async Task<FeishuMessageResult> ReplyMessageAsync(
        string messageId,
        string content,
        CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["msg_type"] = "text",
            ["content"] = new JsonObject { ["text"] = content }.ToJsonString()
        };

        using var document = await SendJsonAsync(
            HttpMethod.Post,
            $"/open-apis/im/v1/messages/{Uri.EscapeDataString(messageId)}/reply",
            FeishuJson.CreateJsonContent(body),
            cancellationToken: cancellationToken);

        var root = document.RootElement;
        FeishuJson.EnsureFeishuSuccess(root, "replyMessage");
        return new FeishuMessageResult(FeishuJson.GetNestedString(root, string.Empty, "data", "message_id"));
    }

    public async Task<FeishuChatInfo?> GetChatInfoAsync(string chatId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var document = await SendJsonAsync(
                HttpMethod.Get,
                $"/open-apis/im/v1/chats/{Uri.EscapeDataString(chatId)}",
                cancellationToken: cancellationToken);

            var root = document.RootElement;
            FeishuJson.EnsureFeishuSuccess(root, "getChatInfo");

            return new FeishuChatInfo(
                FeishuJson.GetNestedString(root, string.Empty, "data", "name"),
                FeishuJson.GetNestedString(root, string.Empty, "data", "chat_type"));
        }
        catch
        {
            return null;
        }
    }

    public async Task<FeishuUserProfile?> GetUserProfileAsync(
        string userId,
        string idType = "open_id",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return null;

        try
        {
            using var document = await SendJsonAsync(
                HttpMethod.Get,
                $"/open-apis/contact/v3/users/{Uri.EscapeDataString(userId)}?user_id_type={Uri.EscapeDataString(idType)}",
                cancellationToken: cancellationToken);

            var root = document.RootElement;
            FeishuJson.EnsureFeishuSuccess(root, "getUserProfile");
            return new FeishuUserProfile(
                FeishuJson.GetNestedString(root, string.Empty, "data", "user", "name"));
        }
        catch
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<FeishuChatSummary>> ListChatsAsync(CancellationToken cancellationToken = default)
    {
        using var document = await SendJsonAsync(
            HttpMethod.Get,
            "/open-apis/im/v1/chats?page_size=50",
            cancellationToken: cancellationToken);

        var root = document.RootElement;
        FeishuJson.EnsureFeishuSuccess(root, "listChats");

        var items = FeishuJson.GetNestedProperty(root, "data", "items");
        if (items is not { ValueKind: JsonValueKind.Array })
            return [];

        var result = new List<FeishuChatSummary>();
        foreach (var item in items.Value.EnumerateArray())
        {
            result.Add(new FeishuChatSummary(
                FeishuJson.GetString(item, "chat_id"),
                FeishuJson.GetString(item, "name"),
                FeishuJson.GetInt32(item, "member_count"),
                item.Clone()));
        }

        return result;
    }

    public async Task<FeishuMemberPage> ListChatMembersAsync(
        string chatId,
        string? pageToken = null,
        int pageSize = 50,
        string memberIdType = "open_id",
        CancellationToken cancellationToken = default)
    {
        pageSize = Math.Clamp(pageSize, 1, 50);
        var query = new StringBuilder(
            $"/open-apis/im/v1/chats/{Uri.EscapeDataString(chatId)}/members" +
            $"?member_id_type={Uri.EscapeDataString(memberIdType)}" +
            $"&page_size={pageSize}");

        if (!string.IsNullOrWhiteSpace(pageToken))
            query.Append("&page_token=").Append(Uri.EscapeDataString(pageToken));

        using var document = await SendJsonAsync(
            HttpMethod.Get,
            query.ToString(),
            cancellationToken: cancellationToken);

        var root = document.RootElement;
        FeishuJson.EnsureFeishuSuccess(root, "listChatMembers");

        var items = new List<FeishuChatMember>();
        var members = FeishuJson.GetNestedProperty(root, "data", "items");
        if (members is { ValueKind: JsonValueKind.Array })
        {
            foreach (var item in members.Value.EnumerateArray())
            {
                items.Add(new FeishuChatMember(
                    Name: FeishuJson.GetString(item, "name"),
                    OpenId: FirstNonEmpty(
                        FeishuJson.GetNestedString(item, string.Empty, "member_id", "open_id"),
                        FeishuJson.GetString(item, "open_id")),
                    UserId: FirstNonEmpty(
                        FeishuJson.GetNestedString(item, string.Empty, "member_id", "user_id"),
                        FeishuJson.GetString(item, "user_id")),
                    UnionId: FirstNonEmpty(
                        FeishuJson.GetNestedString(item, string.Empty, "member_id", "union_id"),
                        FeishuJson.GetString(item, "union_id")),
                    Raw: item.Clone()));
            }
        }

        return new FeishuMemberPage(
            items,
            FeishuJson.GetNestedString(root, string.Empty, "data", "page_token"),
            string.Equals(
                FeishuJson.GetNestedString(root, "false", "data", "has_more"),
                "true",
                StringComparison.OrdinalIgnoreCase));
    }

    public async Task<FeishuMessageResult> CreateCardAsync(
        string initialContent,
        string title = "AI Assistant",
        CancellationToken cancellationToken = default)
    {
        var cardData = BuildStreamingCard(title, initialContent);
        var body = new JsonObject
        {
            ["type"] = "card_json",
            ["data"] = cardData.ToJsonString()
        };

        using var document = await SendJsonAsync(
            HttpMethod.Post,
            "/open-apis/cardkit/v1/cards",
            FeishuJson.CreateJsonContent(body),
            cancellationToken: cancellationToken);

        var root = document.RootElement;
        FeishuJson.EnsureFeishuSuccess(root, "createCard");
        return new FeishuMessageResult(FeishuJson.GetNestedString(root, string.Empty, "data", "card_id"));
    }

    public async Task<bool> UpdateCardAsync(
        string cardId,
        string content,
        int sequence,
        string title = "AI Assistant",
        CancellationToken cancellationToken = default)
    {
        var cardData = BuildStreamingCard(title, content);
        var body = new JsonObject
        {
            ["card"] = new JsonObject
            {
                ["type"] = "card_json",
                ["data"] = cardData.ToJsonString()
            },
            ["sequence"] = sequence
        };

        using var document = await SendJsonAsync(
            HttpMethod.Put,
            $"/open-apis/cardkit/v1/cards/{Uri.EscapeDataString(cardId)}",
            FeishuJson.CreateJsonContent(body),
            cancellationToken: cancellationToken);

        var root = document.RootElement;
        var code = FeishuJson.GetInt32(root, "code") ?? -1;
        if (code == 0)
            return true;

        _log?.Invoke($"[Feishu] updateCard failed (seq={sequence}): {FeishuJson.GetString(root, "msg", "Unknown error")}");
        return false;
    }

    public async Task<FeishuMessageResult> SendCardMessageAsync(
        string chatId,
        string cardId,
        CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["receive_id"] = chatId,
            ["msg_type"] = "interactive",
            ["content"] = new JsonObject
            {
                ["type"] = "card",
                ["data"] = new JsonObject { ["card_id"] = cardId }
            }.ToJsonString()
        };

        using var document = await SendJsonAsync(
            HttpMethod.Post,
            "/open-apis/im/v1/messages?receive_id_type=chat_id",
            FeishuJson.CreateJsonContent(body),
            cancellationToken: cancellationToken);

        var root = document.RootElement;
        FeishuJson.EnsureFeishuSuccess(root, "sendCardMessage");
        return new FeishuMessageResult(FeishuJson.GetNestedString(root, string.Empty, "data", "message_id"));
    }

    public async Task<FeishuMessageResult> ReplyCardMessageAsync(
        string replyMessageId,
        string cardId,
        CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["msg_type"] = "interactive",
            ["content"] = new JsonObject
            {
                ["type"] = "card",
                ["data"] = new JsonObject { ["card_id"] = cardId }
            }.ToJsonString()
        };

        using var document = await SendJsonAsync(
            HttpMethod.Post,
            $"/open-apis/im/v1/messages/{Uri.EscapeDataString(replyMessageId)}/reply",
            FeishuJson.CreateJsonContent(body),
            cancellationToken: cancellationToken);

        var root = document.RootElement;
        FeishuJson.EnsureFeishuSuccess(root, "replyCardMessage");
        return new FeishuMessageResult(FeishuJson.GetNestedString(root, string.Empty, "data", "message_id"));
    }

    public async Task<FeishuBinaryResource> DownloadMessageResourceWithMetadataAsync(
        string messageId,
        string fileKey,
        FeishuMessageResourceType resourceType = FeishuMessageResourceType.Image,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            BuildUri(
                $"/open-apis/im/v1/messages/{Uri.EscapeDataString(messageId)}/resources/{Uri.EscapeDataString(fileKey)}" +
                $"?type={resourceType.ToApiValue()}"));

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await EnsureTokenAsync(cancellationToken));

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Feishu downloadMessageResource failed: HTTP {(int)response.StatusCode}");

        var content = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return new FeishuBinaryResource(
            content,
            response.Content.Headers.ContentType?.MediaType,
            FeishuJson.NormalizeContentDispositionFileName(response.Content.Headers.ContentDisposition));
    }

    public async Task<byte[]> DownloadMessageResourceAsync(
        string messageId,
        string fileKey,
        FeishuMessageResourceType resourceType = FeishuMessageResourceType.Image,
        CancellationToken cancellationToken = default)
    {
        var result = await DownloadMessageResourceWithMetadataAsync(
            messageId,
            fileKey,
            resourceType,
            cancellationToken);

        return result.Content;
    }

    public async Task<string> UploadImageAsync(
        byte[] imageBuffer,
        string fileName = "image.png",
        CancellationToken cancellationToken = default)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("message"), "image_type");
        var imageContent = new ByteArrayContent(imageBuffer);
        imageContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(imageContent, "image", fileName);

        using var document = await SendJsonAsync(
            HttpMethod.Post,
            "/open-apis/im/v1/images",
            form,
            cancellationToken: cancellationToken);

        var root = document.RootElement;
        FeishuJson.EnsureFeishuSuccess(root, "uploadImage");
        return FeishuJson.GetNestedString(root, string.Empty, "data", "image_key");
    }

    public async Task<string> UploadFileAsync(
        byte[] fileBuffer,
        string fileName,
        FeishuFileType fileType = FeishuFileType.Stream,
        CancellationToken cancellationToken = default)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(fileType.ToApiValue()), "file_type");
        form.Add(new StringContent(fileName), "file_name");
        var fileContent = new ByteArrayContent(fileBuffer);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "file", fileName);

        using var document = await SendJsonAsync(
            HttpMethod.Post,
            "/open-apis/im/v1/files",
            form,
            cancellationToken: cancellationToken);

        var root = document.RootElement;
        FeishuJson.EnsureFeishuSuccess(root, "uploadFile");
        return FeishuJson.GetNestedString(root, string.Empty, "data", "file_key");
    }

    public async Task<FeishuMessageResult> SendImageMessageAsync(
        string chatId,
        string imageKey,
        CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["receive_id"] = chatId,
            ["msg_type"] = "image",
            ["content"] = new JsonObject { ["image_key"] = imageKey }.ToJsonString()
        };

        using var document = await SendJsonAsync(
            HttpMethod.Post,
            "/open-apis/im/v1/messages?receive_id_type=chat_id",
            FeishuJson.CreateJsonContent(body),
            cancellationToken: cancellationToken);

        var root = document.RootElement;
        FeishuJson.EnsureFeishuSuccess(root, "sendImageMessage");
        return new FeishuMessageResult(FeishuJson.GetNestedString(root, string.Empty, "data", "message_id"));
    }

    public async Task<FeishuMessageResult> SendFileMessageAsync(
        string chatId,
        string fileKey,
        CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["receive_id"] = chatId,
            ["msg_type"] = "file",
            ["content"] = new JsonObject { ["file_key"] = fileKey }.ToJsonString()
        };

        using var document = await SendJsonAsync(
            HttpMethod.Post,
            "/open-apis/im/v1/messages?receive_id_type=chat_id",
            FeishuJson.CreateJsonContent(body),
            cancellationToken: cancellationToken);

        var root = document.RootElement;
        FeishuJson.EnsureFeishuSuccess(root, "sendFileMessage");
        return new FeishuMessageResult(FeishuJson.GetNestedString(root, string.Empty, "data", "message_id"));
    }

    public async Task SendUrgentAsync(
        string messageId,
        IReadOnlyList<string> userIds,
        FeishuUrgentType urgentType,
        string userIdType = "user_id",
        CancellationToken cancellationToken = default)
    {
        var userIdArray = new JsonArray();
        foreach (var userId in userIds)
        {
            userIdArray.Add(userId);
        }

        var body = new JsonObject
        {
            ["user_id_list"] = userIdArray,
            ["urgent_type"] = urgentType.ToApiValue()
        };

        using var document = await SendJsonAsync(
            HttpMethod.Post,
            $"/open-apis/im/v1/messages/{Uri.EscapeDataString(messageId)}/urgent?user_id_type={Uri.EscapeDataString(userIdType)}",
            FeishuJson.CreateJsonContent(body),
            cancellationToken: cancellationToken);

        FeishuJson.EnsureFeishuSuccess(document.RootElement, "sendUrgent");
    }

    public async Task<JsonElement> ListBitableAppsAsync(
        int pageSize = 50,
        string? pageToken = null,
        CancellationToken cancellationToken = default)
    {
        var query = new StringBuilder($"/open-apis/bitable/v1/apps?page_size={pageSize}");
        if (!string.IsNullOrWhiteSpace(pageToken))
            query.Append("&page_token=").Append(Uri.EscapeDataString(pageToken));

        using var document = await SendJsonAsync(
            HttpMethod.Get,
            query.ToString(),
            cancellationToken: cancellationToken);

        var root = document.RootElement;
        FeishuJson.EnsureFeishuSuccess(root, "listBitableApps");
        return FeishuJson.GetPropertyOrThrow(root, "data").Clone();
    }

    public async Task<JsonElement> ListBitableTablesAsync(
        string appToken,
        int pageSize = 100,
        string? pageToken = null,
        CancellationToken cancellationToken = default)
    {
        var query = new StringBuilder(
            $"/open-apis/bitable/v1/apps/{Uri.EscapeDataString(appToken)}/tables?page_size={pageSize}");
        if (!string.IsNullOrWhiteSpace(pageToken))
            query.Append("&page_token=").Append(Uri.EscapeDataString(pageToken));

        using var document = await SendJsonAsync(
            HttpMethod.Get,
            query.ToString(),
            cancellationToken: cancellationToken);

        var root = document.RootElement;
        FeishuJson.EnsureFeishuSuccess(root, "listBitableTables");
        return FeishuJson.GetPropertyOrThrow(root, "data").Clone();
    }

    public async Task<JsonElement> ListBitableFieldsAsync(
        string appToken,
        string tableId,
        int pageSize = 200,
        string? pageToken = null,
        CancellationToken cancellationToken = default)
    {
        var query = new StringBuilder(
            $"/open-apis/bitable/v1/apps/{Uri.EscapeDataString(appToken)}/tables/{Uri.EscapeDataString(tableId)}/fields?page_size={pageSize}");
        if (!string.IsNullOrWhiteSpace(pageToken))
            query.Append("&page_token=").Append(Uri.EscapeDataString(pageToken));

        using var document = await SendJsonAsync(
            HttpMethod.Get,
            query.ToString(),
            cancellationToken: cancellationToken);

        var root = document.RootElement;
        FeishuJson.EnsureFeishuSuccess(root, "listBitableFields");
        return FeishuJson.GetPropertyOrThrow(root, "data").Clone();
    }

    public async Task<JsonElement> GetBitableRecordsAsync(
        string appToken,
        string tableId,
        int pageSize = 50,
        string? pageToken = null,
        string? filter = null,
        CancellationToken cancellationToken = default)
    {
        var query = new StringBuilder(
            $"/open-apis/bitable/v1/apps/{Uri.EscapeDataString(appToken)}/tables/{Uri.EscapeDataString(tableId)}/records?page_size={pageSize}");
        if (!string.IsNullOrWhiteSpace(pageToken))
            query.Append("&page_token=").Append(Uri.EscapeDataString(pageToken));
        if (!string.IsNullOrWhiteSpace(filter))
            query.Append("&filter=").Append(Uri.EscapeDataString(filter));

        using var document = await SendJsonAsync(
            HttpMethod.Get,
            query.ToString(),
            cancellationToken: cancellationToken);

        var root = document.RootElement;
        FeishuJson.EnsureFeishuSuccess(root, "getBitableRecords");
        return FeishuJson.GetPropertyOrThrow(root, "data").Clone();
    }

    public async Task<JsonElement> CreateBitableRecordsAsync(
        string appToken,
        string tableId,
        JsonElement records,
        CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["records"] = JsonNode.Parse(records.GetRawText())
        };

        using var document = await SendJsonAsync(
            HttpMethod.Post,
            $"/open-apis/bitable/v1/apps/{Uri.EscapeDataString(appToken)}/tables/{Uri.EscapeDataString(tableId)}/records",
            FeishuJson.CreateJsonContent(body),
            cancellationToken: cancellationToken);

        var root = document.RootElement;
        FeishuJson.EnsureFeishuSuccess(root, "createBitableRecords");
        return FeishuJson.GetPropertyOrThrow(root, "data").Clone();
    }

    public async Task<JsonElement> UpdateBitableRecordsAsync(
        string appToken,
        string tableId,
        JsonElement records,
        CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["records"] = JsonNode.Parse(records.GetRawText())
        };

        using var document = await SendJsonAsync(
            HttpMethod.Put,
            $"/open-apis/bitable/v1/apps/{Uri.EscapeDataString(appToken)}/tables/{Uri.EscapeDataString(tableId)}/records",
            FeishuJson.CreateJsonContent(body),
            cancellationToken: cancellationToken);

        var root = document.RootElement;
        FeishuJson.EnsureFeishuSuccess(root, "updateBitableRecords");
        return FeishuJson.GetPropertyOrThrow(root, "data").Clone();
    }

    public async Task<JsonElement> DeleteBitableRecordsAsync(
        string appToken,
        string tableId,
        IReadOnlyList<string> recordIds,
        CancellationToken cancellationToken = default)
    {
        var recordIdArray = new JsonArray();
        foreach (var recordId in recordIds)
        {
            recordIdArray.Add(recordId);
        }

        var body = new JsonObject
        {
            ["record_ids"] = recordIdArray
        };

        using var document = await SendJsonAsync(
            HttpMethod.Post,
            $"/open-apis/bitable/v1/apps/{Uri.EscapeDataString(appToken)}/tables/{Uri.EscapeDataString(tableId)}/records/batch_delete",
            FeishuJson.CreateJsonContent(body),
            cancellationToken: cancellationToken);

        var root = document.RootElement;
        FeishuJson.EnsureFeishuSuccess(root, "deleteBitableRecords");
        return FeishuJson.GetPropertyOrThrow(root, "data").Clone();
    }

    public async Task<IReadOnlyList<FeishuChatMessage>> GetMessagesAsync(
        string chatId,
        int count = 20,
        CancellationToken cancellationToken = default)
    {
        using var document = await SendJsonAsync(
            HttpMethod.Get,
            $"/open-apis/im/v1/messages?container_id_type=chat&container_id={Uri.EscapeDataString(chatId)}&page_size={count}",
            cancellationToken: cancellationToken);

        var root = document.RootElement;
        FeishuJson.EnsureFeishuSuccess(root, "getMessages");

        var items = FeishuJson.GetNestedProperty(root, "data", "items");
        if (items is not { ValueKind: JsonValueKind.Array })
            return [];

        var messages = new List<FeishuChatMessage>();
        foreach (var item in items.Value.EnumerateArray())
        {
            var rawContent = FeishuJson.GetNestedString(item, string.Empty, "body", "content");
            var content = rawContent;
            if (FeishuJson.TryParseDocument(rawContent, out var parsedContent) && parsedContent is not null)
            {
                using (parsedContent)
                {
                    content = FeishuJson.GetString(parsedContent.RootElement, "text", rawContent);
                }
            }

            var createTimeRaw = FeishuJson.GetString(item, "create_time");
            messages.Add(new FeishuChatMessage(
                MessageId: FeishuJson.GetString(item, "message_id"),
                SenderId: FirstNonEmpty(
                    FeishuJson.GetNestedString(item, string.Empty, "sender", "sender_id"),
                    FeishuJson.GetNestedString(item, string.Empty, "sender", "id"),
                    FeishuJson.GetNestedString(item, string.Empty, "sender", "sender_id", "open_id"),
                    FeishuJson.GetNestedString(item, string.Empty, "sender", "sender_id", "user_id")),
                SenderName: string.Empty,
                Content: content,
                Timestamp: FeishuJson.ParseTimestampMilliseconds(createTimeRaw, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                Raw: item.Clone()));
        }

        return messages;
    }

    public static async Task<byte[]> DownloadUrlAsync(string url, CancellationToken cancellationToken = default)
    {
        return await SharedDownloadClient.GetByteArrayAsync(url, cancellationToken);
    }

    private bool HasUsableToken()
    {
        return !string.IsNullOrWhiteSpace(_accessToken) && DateTimeOffset.UtcNow < _tokenExpiresAtUtc;
    }

    private Uri BuildUri(string path)
    {
        return new Uri(_baseUri, path.TrimStart('/'));
    }

    private async Task<JsonDocument> SendJsonAsync(
        HttpMethod method,
        string path,
        HttpContent? content = null,
        bool includeAuth = true,
        CancellationToken cancellationToken = default,
        Action<HttpRequestMessage>? configureRequest = null)
    {
        using var request = new HttpRequestMessage(method, BuildUri(path))
        {
            Content = content
        };

        if (includeAuth)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await EnsureTokenAsync(cancellationToken));

        configureRequest?.Invoke(request);

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        return await FeishuJson.ReadJsonDocumentAsync(response, cancellationToken);
    }

    private static JsonObject BuildStreamingCard(string title, string content)
    {
        return new JsonObject
        {
            ["schema"] = "2.0",
            ["config"] = new JsonObject
            {
                ["update_multi"] = true,
                ["streaming_mode"] = true
            },
            ["header"] = new JsonObject
            {
                ["title"] = new JsonObject
                {
                    ["tag"] = "plain_text",
                    ["content"] = title
                }
            },
            ["body"] = new JsonObject
            {
                ["elements"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["tag"] = "markdown",
                        ["content"] = content
                    }
                }
            }
        };
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
}


