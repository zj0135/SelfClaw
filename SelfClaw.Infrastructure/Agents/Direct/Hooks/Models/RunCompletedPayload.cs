using System.Text.Json.Serialization;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;

namespace SelfClaw.Infrastructure.Agents.Direct.Hooks.Models;

internal sealed record RunCompletedPayload(
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
    string Status,
    string? FinalText,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? FinalTextTruncated,
    string? ErrorMessage,
    HookUsagePayload? Usage,
    int ToolCallCount,
    double DurationMs);
