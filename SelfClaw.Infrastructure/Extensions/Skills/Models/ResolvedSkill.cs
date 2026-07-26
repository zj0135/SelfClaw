namespace SelfClaw.Infrastructure.Extensions.Skills.Models;

internal sealed record ResolvedSkill(
    string Id,
    string Name,
    string Description,
    IReadOnlyList<string> Triggers,
    string InstallPath,
    string Content);
