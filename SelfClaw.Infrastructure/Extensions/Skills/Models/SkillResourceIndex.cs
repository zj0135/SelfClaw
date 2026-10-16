namespace SelfClaw.Infrastructure.Extensions.Skills.Models;

internal sealed record SkillResourceIndex(IReadOnlyList<SkillResourceEntry> Entries, bool Truncated);
