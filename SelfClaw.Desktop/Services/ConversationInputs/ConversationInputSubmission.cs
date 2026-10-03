using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;

namespace SelfClaw.Desktop.Services.ConversationInputs;

internal sealed record ConversationInputSubmission(
    Guid ConversationId,
    string ClientRequestId,
    string Prompt,
    AgentRuntimeDefinition Agent,
    Guid? ModelProfileId,
    WorkspaceRoot? WorkspaceRoot,
    ToolPermissionMode ToolPermissionMode,
    ConversationRecord? Conversation,
    IReadOnlyList<MessageAttachmentRecord>? Attachments = null);
