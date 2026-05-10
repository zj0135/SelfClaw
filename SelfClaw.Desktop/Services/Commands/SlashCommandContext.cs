using SelfClaw.Core.Models;

namespace SelfClaw.Desktop.Services;

public sealed record SlashCommandContext(
    string Arguments,
    bool Confirmed,
    ConversationRecord? Conversation,
    ProviderProfile? Profile,
    string? ApiKey,
    WorkspaceRoot? WorkspaceRoot,
    IReadOnlyList<MessageRecord> Messages,
    DesktopSettings Settings,
    CancellationToken CancellationToken);
