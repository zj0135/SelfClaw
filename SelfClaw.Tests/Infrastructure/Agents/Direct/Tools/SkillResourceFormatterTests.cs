using FluentAssertions;
using SelfClaw.Infrastructure.Agents.Direct.Tools;
using SelfClaw.Infrastructure.Extensions.Skills.Models;

namespace SelfClaw.Tests.Infrastructure.Agents.Direct.Tools;

public sealed class SkillResourceFormatterTests
{
    [Fact]
    public void Listing_reports_an_empty_inventory()
        => SkillResourceFormatter.Listing("review", [], 0, 0, truncated: false)
            .Should().Be("[SelfClaw Skill Resources: review] showing no entries.");

    [Fact]
    public void Listing_notes_a_truncated_inventory()
    {
        var page = new[] { new SkillResourceEntry("references/api.md", SkillResourceKind.Resource, 2048) };

        var text = SkillResourceFormatter.Listing("review", page, 0, 1, truncated: true);

        text.Should().Contain("[resource] references/api.md (2.0 KB)");
        text.Should().Contain("reached its entry limit");
    }

    [Fact]
    public void Listing_points_at_the_next_page()
    {
        var page = new[] { new SkillResourceEntry("scripts/deploy.ps1", SkillResourceKind.Script, 512) };

        var text = SkillResourceFormatter.Listing("review", page, 0, 5, truncated: false);

        text.Should().Contain("[script] scripts/deploy.ps1 (512 B)");
        text.Should().Contain("Use startIndex 1 to continue.");
    }
}
