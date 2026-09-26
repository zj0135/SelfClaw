using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;

namespace SelfClaw.Infrastructure.Agents.Direct.Hooks.Models;

internal sealed record HttpResponseReceivedPayload(
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
    int? StatusCode,
    string? ReasonPhrase,
    IReadOnlyDictionary<string, string> Headers,
    double ElapsedMs,
    string? Error);
