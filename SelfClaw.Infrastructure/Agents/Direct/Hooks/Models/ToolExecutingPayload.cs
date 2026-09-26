using System.Text.Json;
using System.Text.Json.Serialization;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;

namespace SelfClaw.Infrastructure.Agents.Direct.Hooks.Models;

internal sealed record ToolExecutingPayload(
    int SchemaVersion,
    string Event,
    string PluginId,
    string HookId,
    Guid TurnId,
    Guid ConversationId,
    DirectTurnOrigin Origin,
    bool Inherited,
    string AgentId,
    string AgentName,
    string? WorkspaceRoot,
    DateTimeOffset TimestampUtc,
    string CallId,
    int Iteration,
    string ToolName,
    string? DisplayName,
    ToolCallKind Kind,
    ToolSourceKind? SourceKind,
    string? SourceId,
    JsonElement Arguments,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? ArgumentsTruncated,
    bool RequiresApproval,
    ToolPermissionMode PermissionMode);
