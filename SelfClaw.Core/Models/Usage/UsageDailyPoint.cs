namespace SelfClaw.Core.Models;

/// <summary>One local day of the range window. Days without activity are present with zeros so the
/// trend chart has a continuous x axis.</summary>
public sealed record UsageDailyPoint(
    DateOnly Date,
    long TotalTokens,
    long InputTokens,
    long OutputTokens,
    int TurnCount,
    IReadOnlyList<UsageDailyModelCount> Models);
