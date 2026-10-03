using SelfClaw.Core.Runtime;

namespace SelfClaw.Core.Models;

/// <summary>
/// The frozen execution choices captured when a FollowUp is accepted. The default model is
/// resolved to a concrete id at accept time; provider/MCP connections are never prepared here.
/// </summary>
public sealed record ConversationInputExecutionSnapshot(
    string AgentId,
    long AgentRevision,
    AgentExecutionMode Mode,
    Guid? ModelProfileId,
    Guid? WorkspaceRootId,
    string? WorkspaceRootPath,
    ToolPermissionMode ToolPermissionMode,
    AgentRuntimeDefinition? AgentDefinition = null,
    string? DefinitionHash = null,
    int Version = 1);
