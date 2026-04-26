using System.Text.Json;

namespace SelfClaw.Infrastructure.Channels.Feishu;

public sealed record FeishuChatSummary(
    string ChatId,
    string Name,
    int? MemberCount,
    JsonElement? Raw);
