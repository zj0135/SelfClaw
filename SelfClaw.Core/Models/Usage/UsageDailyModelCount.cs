namespace SelfClaw.Core.Models;

/// <summary>Tokens one model consumed on one local day.</summary>
public sealed record UsageDailyModelCount(string Model, long TotalTokens);
