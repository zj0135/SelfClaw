namespace SelfClaw.Desktop.Services;

public sealed record TranscriptRenderState(
    IReadOnlyList<TranscriptRenderItem> Items,
    bool AutoScroll,
    IReadOnlyList<TranscriptConversationItem> Conversations,
    string? SelectedConversationId,
    string Theme,
    IReadOnlyList<ShellSelectOption> Profiles,
    string? SelectedProfileId,
    string? SelectedProfileModel,
    IReadOnlyList<ShellSelectOption> WorkspaceRoots,
    string? SelectedWorkspaceRootId,
    IReadOnlyList<ShellSelectOption> ToolPermissionModes,
    string? SelectedToolPermissionModeId,
    IReadOnlyList<ShellSelectOption> ThemeOptions,
    string? SelectedThemeId,
    IReadOnlyList<AgentActivityNode> AgentActivities,
    string StatusText,
    bool IsBusy);
