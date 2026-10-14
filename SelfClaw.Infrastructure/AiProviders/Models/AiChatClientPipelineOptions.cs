using Microsoft.Extensions.AI;

namespace SelfClaw.Infrastructure.AiProviders.Models;

/// <summary>
/// Everything the per-turn chat pipeline needs beyond the resolved provider request: the bound tools.
/// </summary>
public sealed record AiChatClientPipelineOptions(
    IReadOnlyList<AITool> Tools);
