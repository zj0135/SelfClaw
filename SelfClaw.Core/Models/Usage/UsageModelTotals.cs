namespace SelfClaw.Core.Models;

/// <summary>Lifetime token total of one model, used to pick the favorite model.</summary>
public sealed record UsageModelTotals(string Model, long TotalTokens);
