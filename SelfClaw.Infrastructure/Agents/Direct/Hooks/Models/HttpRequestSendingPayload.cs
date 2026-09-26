using System.Text.Json.Serialization;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;

namespace SelfClaw.Infrastructure.Agents.Direct.Hooks.Models;

internal sealed record HttpRequestSendingPayload(
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
    int RequestSequence,
    string Method,
    string Url,
    IReadOnlyList<string> QueryParameterNames,
    IReadOnlyDictionary<string, string> Headers,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Body,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? BodyTruncated);
