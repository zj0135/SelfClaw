namespace SelfClaw.Infrastructure.AiProviders.Abstractions;

/// <summary>
/// Stores the selected model profile for a named runtime scope, such as the
/// desktop default model or the compaction default model.
/// </summary>
public sealed record AiModelProfileSelection(
    string Scope,
    Guid ModelProfileId,
    DateTimeOffset UpdatedAtUtc);
