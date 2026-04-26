namespace SelfClaw.Infrastructure.Channels.Feishu;

public sealed record FeishuMemberPage(
    IReadOnlyList<FeishuChatMember> Items,
    string? PageToken,
    bool HasMore);
