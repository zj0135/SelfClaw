using SelfClaw.Core.Runtime;

namespace SelfClaw.Core.Models;

public sealed record SubagentTaskStartRequest(
    Guid ParentConversationId,
    Guid ParentTurnId,
    string SubagentId,
    string Task,
    AgentRuntimeDefinition ParentAgent,
    Guid ParentModelProfileId,
    WorkspaceRoot? WorkspaceRoot,
    ToolPermissionMode ToolPermissionMode,
    DirectCapabilityCeiling CapabilityCeiling);
