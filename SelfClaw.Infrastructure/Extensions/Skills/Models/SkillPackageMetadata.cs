namespace SelfClaw.Infrastructure.Extensions.Skills.Models;

internal sealed record SkillPackageMetadata(
    string Id,
    string Name,
    string Description,
    string Version,
    IReadOnlyList<string> Triggers,
    string Content,
    string Body);
