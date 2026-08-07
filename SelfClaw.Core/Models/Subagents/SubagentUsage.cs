namespace SelfClaw.Core.Models;

public sealed record SubagentUsage(
    int? InputTokens,
    int? OutputTokens);
