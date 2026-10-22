namespace SelfClaw.Core.Models;

/// <summary><see cref="Share"/> is the model's slice of the enclosing window's tokens, 0–1.</summary>
public sealed record UsageFavoriteModel(string Model, long TotalTokens, double Share);
