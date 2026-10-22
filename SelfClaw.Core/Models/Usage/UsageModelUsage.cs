namespace SelfClaw.Core.Models;

/// <summary>One model's usage over the range window; <see cref="Share"/> is 0–1 of the window tokens.</summary>
public sealed record UsageModelUsage(
    string Model,
    long TotalTokens,
    long InputTokens,
    long OutputTokens,
    int TurnCount,
    double Share);
