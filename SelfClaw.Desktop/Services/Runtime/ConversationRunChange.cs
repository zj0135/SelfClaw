namespace SelfClaw.Desktop.Services.Runtime;

internal sealed record ConversationRunChange(
    ConversationRunHandle Handle,
    bool IsCompleted,
    bool PublishPersistedContent = true);
