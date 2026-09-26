using System.Text.Json.Serialization;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;

namespace SelfClaw.Infrastructure.Agents.Direct.Hooks.Models;

internal sealed record RunStartingPayload(
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
    string Provider,
    AiProviderKind ProviderKind,
    string Model,
    string? UserPrompt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? UserPromptTruncated,
    IReadOnlyList<HookAttachmentPayload> Attachments,
    IReadOnlyList<string> Tools,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? CompletedSubagentTaskIds);
