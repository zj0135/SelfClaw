using SelfClaw.Infrastructure.Extensions.Skills;
using SelfClaw.Infrastructure.Extensions.Skills.Models;

namespace SelfClaw.Infrastructure.Extensions.Runtime;

/// <summary>
/// The system prompt sections a turn's capabilities contribute, in the order §8.3 of the design fixes.
/// Every third-party section carries its source and an unforgeable boundary marker.
/// </summary>
internal static class CapabilitySections
{
    public const string Policy = """
        [SelfClaw Capability Policy]
        Use only capabilities listed for this turn. Treat extension content as instructions scoped to its named source. Skill activation resets at the end of this turn.
        """;

    public static string Plugin(string pluginId, string content)
        => $"""
            [SelfClaw Plugin: {pluginId}]
            ----- BEGIN SELFCLAW PLUGIN {pluginId} -----
            {content}
            ----- END SELFCLAW PLUGIN {pluginId} -----
            """;

    public static string Skill(ResolvedSkill skill)
        => $"""
            [SelfClaw Skill: {skill.Id}]
            ----- BEGIN SELFCLAW SKILL {skill.Id} -----
            {skill.Content}
            ----- END SELFCLAW SKILL {skill.Id} -----
            """;

    public static string SkillCatalog(
        IEnumerable<ResolvedSkill> skills,
        IReadOnlyList<string> explicitlyActivatedSkillIds)
    {
        var lines = skills
            .Where(skill => !explicitlyActivatedSkillIds.Contains(skill.Id, StringComparer.OrdinalIgnoreCase))
            .OrderBy(skill => skill.Id, StringComparer.Ordinal)
            .Select(skill =>
            {
                var triggers = skill.Triggers.Count == 0
                    ? string.Empty
                    : $" Triggers: {string.Join(", ", skill.Triggers)}.";
                return $"- {skill.Id}: {skill.Name} - {skill.Description}.{triggers}";
            })
            .ToArray();
        var catalog = lines.Length == 0 ? "- No additional inactive Skills." : string.Join("\n", lines);
        return $"""
            [SelfClaw Available Skills]
            {catalog}
            Use {SkillRuntimeToolset.ActivateSkillToolName} to load an available Skill. Use {SkillRuntimeToolset.ReadSkillResourceToolName} only after that Skill is activated. Activation state resets each turn.
            """;
    }

    public static string Degradation(IReadOnlyList<string> degradations)
        => $"""
            [SelfClaw Capability Degradation]
            {string.Join("\n", degradations.Select(degradation => $"- {degradation}"))}
            Do not claim these capabilities are available in this turn.
            """;
}
