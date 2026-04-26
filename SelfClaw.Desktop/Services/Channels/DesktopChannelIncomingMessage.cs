namespace SelfClaw.Desktop.Services;

public sealed record DesktopChannelIncomingMessage(
    string ChannelId,
    string ConversationId,
    string MessageId,
    string SenderId,
    string SenderName,
    string Content,
    string? ConversationName = null,
    string? ConversationType = null,
    IReadOnlyList<DesktopChannelAttachment>? Attachments = null);
