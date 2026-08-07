using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;

namespace SelfClaw.Desktop.Services.Subagents.Models;

internal sealed record SubagentParentExecutionSnapshot(
    int Version,
    AgentRuntimeDefinition Agent,
    Guid ModelProfileId,
    WorkspaceRoot? WorkspaceRoot,
    ToolPermissionMode ToolPermissionMode,
    DirectCapabilityCeiling CapabilityCeiling);
