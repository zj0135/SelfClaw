using FluentAssertions;
using SelfClaw.Infrastructure.Agents.Direct.Context;
using SelfClaw.Infrastructure.Agents.Direct.Tools;
using SelfClaw.Infrastructure.Extensions.Skills.Models;

namespace SelfClaw.Tests.Infrastructure.Agents.Direct.Context;

public sealed class CapabilitySectionsTests
{
    [Fact]
    public void SkillCatalog_names_all_three_skill_tools()
    {
        var catalog = CapabilitySections.SkillCatalog([CreateSkill(["deploy"])], []);

        catalog.Should().Contain(SkillRuntimeToolset.ActivateSkillToolName);
        catalog.Should().Contain(SkillRuntimeToolset.ReadSkillResourceToolName);
        catalog.Should().Contain(SkillRuntimeToolset.RunSkillScriptToolName);
    }

    [Fact]
    public void SkillCatalog_labels_triggers_as_use_when_guidance()
    {
        var catalog = CapabilitySections.SkillCatalog([CreateSkill(["deploy", "staging"])], []);

        catalog.Should().Contain("Use when: deploy, staging.");
        catalog.Should().NotContain("Triggers:");
    }

    [Fact]
    public void SkillCatalog_omits_skills_that_the_turn_activated_explicitly()
    {
        var catalog = CapabilitySections.SkillCatalog([CreateSkill([])], ["review"]);

        catalog.Should().Contain("No additional inactive Skills.");
    }

    [Fact]
    public void Policy_forbids_reconstructing_a_skill_path_or_script_body()
    {
        CapabilitySections.Policy.Should().Contain(SkillRuntimeToolset.RunSkillScriptToolName);
        CapabilitySections.Policy.Should().Contain("Never reconstruct a Skill installation path");
        CapabilitySections.Policy.Should().Contain("reimplement a script body through the shell");
    }

    private static ResolvedSkill CreateSkill(IReadOnlyList<string> triggers)
        => new("review", "Review", "Reviews changes", triggers, "C:\\skills\\review", "body",
            new SkillResourceIndex([], false));
}
