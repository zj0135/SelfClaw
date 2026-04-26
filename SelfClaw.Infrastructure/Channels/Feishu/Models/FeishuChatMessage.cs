using System.Text.Json;

namespace SelfClaw.Infrastructure.Channels.Feishu;

public sealed record FeishuChatMessage(
    string MessageId,
    string SenderId,
    string SenderName,
    string Content,
    long Timestamp,
    JsonElement? Raw);
