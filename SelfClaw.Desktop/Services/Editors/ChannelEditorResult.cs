namespace SelfClaw.Desktop.Services;

public sealed record ChannelEditorResult(
    string ChannelId,
    string DisplayName,
    string AppId,
    string BotDisplayName,
    Guid? ProfileId,
    string AppSecret);
