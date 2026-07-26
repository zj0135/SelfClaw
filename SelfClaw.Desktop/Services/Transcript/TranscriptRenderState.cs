namespace SelfClaw.Desktop.Services;

public sealed record TranscriptRenderState(
    IReadOnlyList<TranscriptRenderItem> Items,
    bool AutoScroll,
    IReadOnlyList<TranscriptConversationItem> Conversations,
    string? SelectedConversationId,
    bool IsBusy,
    string? ActivityText = null,
    string AgentMode = "cli",
    string? SelectedAgentId = null,
    string? SelectedAgentName = null,
    long CapabilityRevision = 0);
