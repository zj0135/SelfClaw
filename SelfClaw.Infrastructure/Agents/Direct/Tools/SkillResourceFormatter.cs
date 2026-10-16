using System.Globalization;
using System.Text;
using SelfClaw.Infrastructure.Extensions.Skills.Models;

namespace SelfClaw.Infrastructure.Agents.Direct.Tools;

/// <summary>
/// Renders a Skill inventory for the model. The model can only use files it knows about, so the
/// activation result carries the inventory next to the SKILL.md body: without it a resource that the
/// body does not spell out is invisible and the model is left guessing file names.
/// </summary>
internal static class SkillResourceFormatter
{
    private const int MaximumListedPerKind = 20;

    internal static string Manifest(string skillId, SkillResourceIndex index)
    {
        var builder = new StringBuilder();
        builder.Append("[SelfClaw Skill Resources: ").Append(skillId).Append(']');
        if (index.Entries.Count == 0)
        {
            return builder.Append("\nNo resource or script files.").ToString();
        }

        AppendGroup(
            builder,
            index,
            SkillResourceKind.Resource,
            "Resources (read with read_skill_resource):");
        AppendGroup(
            builder,
            index,
            SkillResourceKind.Script,
            "Scripts (execute with run_skill_script; never transcribe or reimplement a script body):");
        if (index.Truncated)
        {
            builder.Append("\nThe inventory reached its entry limit and may be incomplete.");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Renders one page of the inventory. The manifest attached to activation stops at a small prefix,
    /// so a large Skill needs a way to page through the rest without re-reading SKILL.md.
    /// </summary>
    internal static string Listing(
        string skillId,
        IReadOnlyList<SkillResourceEntry> page,
        int startIndex,
        int total,
        bool truncated)
    {
        var builder = new StringBuilder()
            .Append("[SelfClaw Skill Resources: ").Append(skillId).Append("] showing ");
        if (total == 0)
        {
            return builder.Append("no entries.").ToString();
        }

        builder.Append(startIndex + 1).Append('-').Append(startIndex + page.Count)
            .Append(" of ").Append(total).Append('.');
        foreach (var entry in page)
        {
            builder.Append("\n- [")
                .Append(entry.Kind == SkillResourceKind.Script ? "script" : "resource")
                .Append("] ").Append(entry.RelativePath)
                .Append(" (").Append(HumanizeBytes(entry.LengthBytes)).Append(')');
        }

        if (startIndex + page.Count < total)
        {
            builder.Append("\nUse startIndex ").Append(startIndex + page.Count).Append(" to continue.");
        }

        if (truncated)
        {
            builder.Append("\nThe inventory reached its entry limit and may be incomplete.");
        }

        return builder.ToString();
    }

    private static void AppendGroup(
        StringBuilder builder,
        SkillResourceIndex index,
        SkillResourceKind kind,
        string header)
    {
        var entries = index.Entries.Where(entry => entry.Kind == kind).ToArray();
        if (entries.Length == 0)
        {
            return;
        }

        builder.Append('\n').Append(header);
        foreach (var entry in entries.Take(MaximumListedPerKind))
        {
            builder.Append("\n- ").Append(entry.RelativePath).Append(" (").Append(HumanizeBytes(entry.LengthBytes)).Append(')');
        }

        if (entries.Length > MaximumListedPerKind)
        {
            builder.Append("\n- (+").Append(entries.Length - MaximumListedPerKind).Append(" more not listed)");
        }
    }

    private static string HumanizeBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{(bytes / 1024d).ToString("0.0", CultureInfo.InvariantCulture)} KB",
        _ => $"{(bytes / (1024d * 1024)).ToString("0.0", CultureInfo.InvariantCulture)} MB"
    };
}
