using SelfClaw.Core.Models;

namespace SelfClaw.Desktop.Services.Workspace.Models;

internal sealed record PreparedConversationWorkspace(WorkspaceRoot? WorkspaceRoot, Guid? ConversationId, bool Provisioned);
