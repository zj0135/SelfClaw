namespace SelfClaw.Infrastructure.Channels.Feishu;

public sealed record FeishuBinaryResource(
    byte[] Content,
    string? MediaType,
    string? FileName);
