using System.Text.Json;

namespace SelfClaw.Infrastructure.Channels.Feishu;

public sealed record FeishuChatMember(
    string Name,
    string OpenId,
    string UserId,
    string UnionId,
    JsonElement? Raw);
