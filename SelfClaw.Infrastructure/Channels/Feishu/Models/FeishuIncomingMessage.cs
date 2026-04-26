using System.Text.Json;

namespace SelfClaw.Infrastructure.Channels.Feishu;

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
