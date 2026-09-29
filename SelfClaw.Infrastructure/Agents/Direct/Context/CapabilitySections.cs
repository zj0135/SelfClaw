using SelfClaw.Infrastructure.Agents.Direct.Tools;
using SelfClaw.Infrastructure.Extensions.Skills;
using SelfClaw.Infrastructure.Extensions.Skills.Models;
using SelfClaw.Infrastructure.Agents.Direct.Hooks;
using SelfClaw.Core.Models;

namespace SelfClaw.Infrastructure.Agents.Direct.Context;

/// <summary>
/// The system prompt sections a turn's capabilities contribute, in the order §8.3 of the design fixes.
/// Every third-party section carries its source and an unforgeable boundary marker.
/// </summary>
internal static class CapabilitySections
{
    /// <summary>Per-Subagent description budget for the catalog; the definition allows far more.</summary>
    private const int MaximumSubagentDescriptionBytes = 256;

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

    public static string SubagentCatalog(
        IReadOnlyList<SubagentCatalogEntry> subagents,
        string delegateToolName)
    {
        var catalog = string.Join("\n", subagents
            .OrderBy(subagent => subagent.Id, StringComparer.Ordinal)
            .Select(FormatSubagent));
        return $"""
            [SelfClaw Available Subagents]
            {catalog}
            Delegate with {delegateToolName} using an exact id from this list. Never invent, translate or guess a Subagent id; an id that is not listed is rejected.
            """;
    }

    public static string Degradation(IReadOnlyList<string> degradations)
        => $"""
            [SelfClaw Capability Degradation]
            {string.Join("\n", degradations.Select(degradation => $"- {degradation}"))}
            Do not claim these capabilities are available in this turn.
            """;

    private static string FormatSubagent(SubagentCatalogEntry subagent)
    {
        var (description, _) = HookProtocol.Truncate(
            subagent.Description.ReplaceLineEndings(" ").Trim(),
            MaximumSubagentDescriptionBytes);
        var detail = description.Length == 0 ? string.Empty : $" - {EndSentence(description)}";
        var policy = string.IsNullOrWhiteSpace(subagent.ToolPolicy)
            ? string.Empty
            : $" Tools: {subagent.ToolPolicy}.";
        return $"- {subagent.Id}: {subagent.Name}{detail}{policy}";
    }

    private static string EndSentence(string text)
        => text.EndsWith('.') || text.EndsWith('!') || text.EndsWith('?')
            ? text
            : $"{text}.";
}
