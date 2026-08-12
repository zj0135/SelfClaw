using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;

namespace SelfClaw.Desktop.ViewModels;

internal sealed record PromptSubmissionSnapshot(
    string Prompt,
    ConversationRecord? Conversation,
    WorkspaceRoot? WorkspaceRoot,
    ToolPermissionMode ToolPermissionMode,
    Guid? ModelProfileId,
    string AgentId,
    AgentExecutionMode? ExecutionModeOverride,
    GitWorkspaceMode WorkspaceMode,
    int SelectionVersion);
