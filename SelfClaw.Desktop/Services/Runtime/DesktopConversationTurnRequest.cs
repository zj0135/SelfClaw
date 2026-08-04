using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;

namespace SelfClaw.Desktop.Services.Runtime;

internal sealed record DesktopConversationTurnRequest(
    ConversationRecord? Conversation,
    AgentRuntimeDefinition Agent,
    string Prompt,
    Guid? ModelProfileId,
    WorkspaceRoot? WorkspaceRoot,
    ToolPermissionMode ToolPermissionMode);
