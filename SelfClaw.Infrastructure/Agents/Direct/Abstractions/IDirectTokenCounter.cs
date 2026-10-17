using Microsoft.Extensions.AI;
using SelfClaw.Infrastructure.AiProviders.Models;

namespace SelfClaw.Infrastructure.Agents.Direct.Abstractions;

/// <summary>
/// Counts prompt tokens for Direct turns with an offline tokenizer. The encoding depends on the model
/// of the turn being counted, so every call takes the profile: one counter instance serves all
/// conversations. A <see langword="null"/> result means counting is unavailable for that encoding;
/// it is not zero tokens, and callers must degrade instead of substituting a byte-based estimate.
/// </summary>
internal interface IDirectTokenCounter
{
    bool IsAvailable(AiModelProfile? model);

    int? CountText(string? text, AiModelProfile? model);

    int? CountMessages(IReadOnlyList<ChatMessage> messages, AiModelProfile? model);

    int? CountTools(IEnumerable<AITool>? tools, AiModelProfile? model);
}
