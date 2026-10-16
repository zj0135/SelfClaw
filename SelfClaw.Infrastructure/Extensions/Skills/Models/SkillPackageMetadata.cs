namespace SelfClaw.Infrastructure.Extensions.Skills.Models;

internal sealed record SkillPackageMetadata(
    string Id,
    string Name,
    string Description,
    string Version,
    IReadOnlyList<string> Triggers,
    string Content,
    string? License,
    string? Compatibility,
    IReadOnlyList<string> AllowedTools,
    IReadOnlyDictionary<string, string> Metadata,
    IReadOnlyList<string> Warnings);
