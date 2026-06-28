namespace SelfClaw.Desktop.Services;

public sealed record TranscriptRenderState(
    IReadOnlyList<TranscriptRenderItem> Items,
    bool AutoScroll,
    IReadOnlyList<TranscriptConversationItem> Conversations,
    string? SelectedConversationId,
    bool IsBusy);
