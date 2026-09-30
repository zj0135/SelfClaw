using SelfClaw.Core.Models;

namespace SelfClaw.Infrastructure.Agents.Direct.Hooks.Models;

internal sealed record HookUsagePayload(
    int? InputTokens,
    int? OutputTokens,
    int? CachedInputTokens = null,
    int? CacheWriteInputTokens = null,
    int? ReasoningTokens = null,
    int? ContextTokens = null,
    int? ContextWindowTokens = null,
    decimal? CostUsd = null,
    TurnUsageCostSource? CostSource = null);
