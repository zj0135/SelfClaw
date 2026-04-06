using System.Text.Json;

namespace SelfClaw.Infrastructure.Channels.Feishu;

/// <summary>
/// Configuration for the C# Feishu channel runtime.
/// </summary>
public sealed class FeishuChannelOptions
{
    public required string AppId { get; init; }
    public required string AppSecret { get; init; }
    public string BaseUrl { get; init; } = FeishuApiClient.DefaultBaseUrl;
    public string? BotDisplayName { get; init; }
    public HttpClient? HttpClient { get; init; }
    public Action<string>? Log { get; init; }
}

public sealed class FeishuIncomingMessage
{
    public required string ChatId { get; init; }
    public required string SenderId { get; init; }
    public required string SenderName { get; init; }
    public string Content { get; init; } = string.Empty;
    public string MessageId { get; init; } = string.Empty;
    public long Timestamp { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public IReadOnlyList<FeishuImageAttachment>? Images { get; init; }
    public FeishuAudioAttachment? Audio { get; init; }
    public string MessageType { get; init; } = "text";
    public string ChatName { get; init; } = string.Empty;
    public string ChatType { get; init; } = "p2p";
    public JsonElement? RawEvent { get; init; }
}

public sealed record FeishuImageAttachment(string Base64, string MediaType);

public sealed record FeishuAudioAttachment(
    string FileKey,
    string? FileName,
    string? MediaType,
    int? DurationMs);

public sealed record FeishuBinaryResource(
    byte[] Content,
    string? MediaType,
    string? FileName);

public sealed record FeishuMessageResult(string MessageId);

public sealed record FeishuBotInfo(string OpenId, string AppName);

public sealed record FeishuChatInfo(string Name, string ChatType);

public sealed record FeishuUserProfile(string Name);

public sealed record FeishuChatSummary(
    string ChatId,
    string Name,
    int? MemberCount,
    JsonElement? Raw);

public sealed record FeishuChatMessage(
    string MessageId,
    string SenderId,
    string SenderName,
    string Content,
    long Timestamp,
    JsonElement? Raw);

public sealed record FeishuChatMember(
    string Name,
    string OpenId,
    string UserId,
    string UnionId,
    JsonElement? Raw);

public sealed record FeishuMemberPage(
    IReadOnlyList<FeishuChatMember> Items,
    string? PageToken,
    bool HasMore);

public enum FeishuFileType
{
    Opus,
    Mp4,
    Pdf,
    Doc,
    Xls,
    Ppt,
    Stream
}

public enum FeishuUrgentType
{
    App,
    Sms
}

public enum FeishuMessageResourceType
{
    Image,
    File
}

public interface IFeishuStreamingHandle
{
    Task UpdateAsync(string content, CancellationToken cancellationToken = default);
    Task FinishAsync(string finalContent, CancellationToken cancellationToken = default);
}

public static class FeishuValueConverters
{
    public static string ToApiValue(this FeishuFileType fileType) => fileType switch
    {
        FeishuFileType.Opus => "opus",
        FeishuFileType.Mp4 => "mp4",
        FeishuFileType.Pdf => "pdf",
        FeishuFileType.Doc => "doc",
        FeishuFileType.Xls => "xls",
        FeishuFileType.Ppt => "ppt",
        _ => "stream"
    };

    public static FeishuFileType DetectFileType(string fileName, FeishuFileType fallback = FeishuFileType.Stream)
    {
        var extension = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        return extension switch
        {
            "opus" => FeishuFileType.Opus,
            "mp4" => FeishuFileType.Mp4,
            "pdf" => FeishuFileType.Pdf,
            "doc" or "docx" => FeishuFileType.Doc,
            "xls" or "xlsx" => FeishuFileType.Xls,
            "ppt" or "pptx" => FeishuFileType.Ppt,
            _ => fallback
        };
    }

    public static string ToApiValue(this FeishuUrgentType urgentType) => urgentType switch
    {
        FeishuUrgentType.Sms => "sms",
        _ => "app"
    };

    public static string ToApiValue(this FeishuMessageResourceType resourceType) => resourceType switch
    {
        FeishuMessageResourceType.File => "file",
        _ => "image"
    };
}

