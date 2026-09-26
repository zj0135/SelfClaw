using SelfClaw.Core.Models;

namespace SelfClaw.Infrastructure.Agents.Direct.Hooks.Models;

internal sealed record RunStartingInput(
    string ProviderName,
    AiProviderKind ProviderKind,
    string Model,
    string? UserPrompt,
    IReadOnlyList<HookAttachmentPayload> Attachments,
    IReadOnlyList<string> Tools,
    IReadOnlyList<string>? CompletedSubagentTaskIds);
