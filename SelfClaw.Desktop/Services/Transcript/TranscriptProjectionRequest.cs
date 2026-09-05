using SelfClaw.Core.Models;

namespace SelfClaw.Desktop.Services.Transcript;

internal sealed record TranscriptProjectionRequest(
    IReadOnlyList<MessageRecord> Messages,
    IReadOnlyList<ToolExecutionRecord> ToolRuns,
    IReadOnlyList<ConversationRecord> Conversations,
    IReadOnlyList<WorkspaceRoot> WorkspaceRoots,
    Guid? SelectedConversationId,
    bool AutoScroll,
    bool IsBusy,
    string? ActivityText,
    string AgentMode,
    string SelectedAgentId,
    string SelectedAgentName,
    long CapabilityRevision,
    string ToolPermissionMode);
