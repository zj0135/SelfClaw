using Microsoft.Extensions.AI;

namespace SelfClaw.Infrastructure.AiProviders.Models;

public sealed record AiChatRuntimeInputs(
    bool EnableReasoning,
    IReadOnlyList<AITool> Tools);
