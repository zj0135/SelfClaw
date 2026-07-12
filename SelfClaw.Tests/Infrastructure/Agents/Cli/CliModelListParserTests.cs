using FluentAssertions;
using SelfClaw.Infrastructure.Agents.Cli.Discovery;

namespace SelfClaw.Tests.Infrastructure.Agents.Cli;

public sealed class CliModelListParserTests
{
    [Fact]
    public void ParseCodexDebugModels_orders_by_priority_and_drops_hidden()
    {
        // Priorities intentionally out of order; "hide" and slug-less entries must be excluded.
        const string json = """
        {
          "models": [
            { "slug": "gpt-5.4", "display_name": "gpt-5.4", "visibility": "list", "priority": 2 },
            { "slug": "gpt-5.5", "display_name": "GPT-5.5", "visibility": "list", "priority": 0 },
            { "slug": "codex-auto-review", "visibility": "hide", "priority": 1 },
            { "display_name": "no slug", "visibility": "list", "priority": 3 },
            { "slug": "gpt-5.2", "visibility": "list", "priority": 10 }
          ]
        }
        """;

        var models = CliModelListParser.ParseCodexDebugModels(json);

        models.Should().Equal("gpt-5.5", "gpt-5.4", "gpt-5.2");
    }

    [Fact]
    public void ParseCodexDebugModels_places_models_without_priority_last()
    {
        const string json = """
        {
          "models": [
            { "slug": "no-priority", "visibility": "list" },
            { "slug": "first", "visibility": "list", "priority": 0 }
          ]
        }
        """;

        var models = CliModelListParser.ParseCodexDebugModels(json);

        models.Should().Equal("first", "no-priority");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{ \"models\": \"unexpected\" }")]
    [InlineData("{ \"other\": [] }")]
    [InlineData("[]")]
    public void ParseCodexDebugModels_returns_empty_for_unusable_input(string stdout)
    {
        CliModelListParser.ParseCodexDebugModels(stdout).Should().BeEmpty();
    }

    [Fact]
    public void ParseCodexReasoningLevels_unions_visible_models_preserving_order()
    {
        // Two visible models with the same levels in a different order plus a hidden model with an extra
        // level: the union follows first-encounter order and ignores the hidden model.
        const string json = """
        {
          "models": [
            {
              "slug": "gpt-5.5", "visibility": "list",
              "supported_reasoning_levels": [
                { "effort": "low" }, { "effort": "medium" }, { "effort": "high" }, { "effort": "xhigh" }
              ]
            },
            {
              "slug": "gpt-5.2", "visibility": "list",
              "supported_reasoning_levels": [ { "effort": "medium" }, { "effort": "high" } ]
            },
            {
              "slug": "hidden", "visibility": "hide",
              "supported_reasoning_levels": [ { "effort": "ultra" } ]
            }
          ]
        }
        """;

        var levels = CliModelListParser.ParseCodexReasoningLevels(json);

        levels.Should().Equal("low", "medium", "high", "xhigh");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{ \"models\": [ { \"slug\": \"a\", \"visibility\": \"list\" } ] }")]
    public void ParseCodexReasoningLevels_returns_empty_for_unusable_input(string stdout)
    {
        CliModelListParser.ParseCodexReasoningLevels(stdout).Should().BeEmpty();
    }

    [Fact]
    public void ParseOpenCodeModels_keeps_slugs_and_drops_noise_and_duplicates()
    {
        var stdout = string.Join('\n',
            "opencode/big-pickle",
            "iflowcn/deepseek-r1",
            "",
            "Loading models from models.dev",   // spaces, no slash -> noise
            "note: see provider/model docs",     // has a slash but also spaces -> noise
            "opencode/big-pickle",               // duplicate
            "zy/glm-5.1");

        var models = CliModelListParser.ParseOpenCodeModels(stdout);

        models.Should().Equal("opencode/big-pickle", "iflowcn/deepseek-r1", "zy/glm-5.1");
    }

    [Fact]
    public void ParseOpenCodeModels_tolerates_carriage_returns()
    {
        var models = CliModelListParser.ParseOpenCodeModels("opencode/big-pickle\r\nzy/glm-5.1\r\n");

        models.Should().Equal("opencode/big-pickle", "zy/glm-5.1");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-slash-here\njust words")]
    public void ParseOpenCodeModels_returns_empty_for_unusable_input(string stdout)
    {
        CliModelListParser.ParseOpenCodeModels(stdout).Should().BeEmpty();
    }
}
