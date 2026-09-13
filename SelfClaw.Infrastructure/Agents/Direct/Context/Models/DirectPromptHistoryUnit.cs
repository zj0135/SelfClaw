using Microsoft.Extensions.AI;

namespace SelfClaw.Infrastructure.Agents.Direct.Context.Models;

// All replay messages from a stored turn stay together when history is trimmed.
internal sealed record DirectPromptHistoryUnit(
    IReadOnlyList<ChatMessage> Messages,
    long EstimatedTokens);
