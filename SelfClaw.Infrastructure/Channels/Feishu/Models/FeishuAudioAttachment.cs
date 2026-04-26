namespace SelfClaw.Infrastructure.Channels.Feishu;

public sealed record FeishuAudioAttachment(
    string FileKey,
    string? FileName,
    string? MediaType,
    int? DurationMs);
