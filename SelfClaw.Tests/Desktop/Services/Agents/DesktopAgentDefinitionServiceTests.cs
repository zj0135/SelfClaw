using System.Text;
using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Desktop.Services;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Tests.Desktop.Services.Agents;

public sealed class DesktopAgentDefinitionServiceTests : IDisposable
{
    private readonly string _rootPath;

    public DesktopAgentDefinitionServiceTests()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public void LoadAll_seeds_the_build_agent()
    {
        var service = CreateService();

        var agents = service.LoadAll();

        agents.Should().ContainSingle();
        agents[0].Id.Should().Be(DesktopAgentDefinitionService.BuildAgentId);
        agents[0].IsBuiltIn.Should().BeTrue();
        File.Exists(Path.Combine(service.AgentsDirectory, "build.md")).Should().BeTrue();
    }

    [Fact]
    public void LoadAll_parses_plugins_and_applies_legacy_disabled_lists_once()
    {
        var service = CreateService();
        Directory.CreateDirectory(service.AgentsDirectory);
        File.WriteAllText(
            Path.Combine(service.AgentsDirectory, "custom.md"),
            """
            ---
            name: "Custom"
            description: "Migrated"
            mode: direct
            tools: system
            plugins:
              - office-workflows
            skills:
              - code-review
              - nested/reviewer
            disabledSkills:
              - code-review
            mcpServers:
              - github
              - local
            disabledMcpServers:
              - local
            ---

            Follow the configured workflow.
            """,
            new UTF8Encoding(false));

        var agent = service.LoadAll().Single(item => item.Id == "custom");

        agent.PluginIds.Should().Equal("office-workflows");
        agent.SkillIds.Should().Equal("nested/reviewer");
        agent.McpServerIds.Should().Equal("github");
        agent.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void Save_writes_only_non_empty_binding_lists_and_round_trips_normalized_ids()
    {
        var service = CreateService();
        var definition = new DesktopAgentDefinition(
            "review",
            "Reviewer",
            "Reviews changes",
            AgentExecutionMode.Direct,
            AgentRuntimeDefinition.SystemToolPolicy,
            [],
            ["engineering/code-review"],
            [],
            ["Reviewer", "test-runner", "REVIEWER"],
            "Review the current change.",
            string.Empty,
            false,
            []);

        var saved = service.Save(definition);
        var markdown = File.ReadAllText(saved.FilePath).ReplaceLineEndings("\n");
        var loaded = service.LoadAll().Single(item => item.Id == "review");

        markdown.Should().Contain("skills:\n  - engineering/code-review");
        markdown.Should().Contain("subagents:\n  - reviewer\n  - test-runner");
        markdown.Should().NotContain("plugins:");
        markdown.Should().NotContain("mcpServers:");
        markdown.Should().NotContain("disabledSkills:");
        markdown.Should().NotContain("disabledMcpServers:");
        loaded.SkillIds.Should().Equal("engineering/code-review");
        loaded.SubagentIds.Should().Equal("reviewer", "test-runner");
    }

    [Fact]
    public void LoadAll_warns_when_a_cli_agent_declares_subagents_and_ignores_invalid_ids()
    {
        var service = CreateService();
        Directory.CreateDirectory(service.AgentsDirectory);
        File.WriteAllText(
            Path.Combine(service.AgentsDirectory, "cli.md"),
            """
            ---
            name: CLI
            description: CLI agent
            mode: cli
            tools: system
            subagents:
              - Reviewer
              - nested/reviewer
            ---
            Use the configured CLI.
            """,
            new UTF8Encoding(false));

        var agent = service.LoadAll().Single(item => item.Id == "cli");

        agent.SubagentIds.Should().Equal("reviewer");
        agent.Warnings.Should().Contain(item => item.Contains("invalid Subagent id", StringComparison.Ordinal));
        agent.Warnings.Should().Contain(item => item.Contains("CLI agents cannot delegate", StringComparison.Ordinal));
    }

    [Fact]
    public void SetExtensionBinding_replaces_the_agent_file_without_losing_other_bindings()
    {
        var service = CreateService();
        service.LoadAll();

        service.SetExtensionBinding("build", new ExtensionItemKey(ExtensionKind.Skill, "code-review"), true);
        service.SetExtensionBinding("build", new ExtensionItemKey(ExtensionKind.McpServer, "github"), true);
        service.SetExtensionBinding("build", new ExtensionItemKey(ExtensionKind.Skill, "code-review"), false);

        var build = service.LoadAll().Single(item => item.Id == "build");
        build.SkillIds.Should().BeEmpty();
        build.McpServerIds.Should().Equal("github");
        Directory.EnumerateFiles(service.AgentsDirectory, "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public void Save_failure_does_not_damage_the_existing_agent_file()
    {
        var service = CreateService();
        var original = service.LoadAll().Single(item => item.Id == "build");
        var originalMarkdown = File.ReadAllText(original.FilePath);

        using (new FileStream(original.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var action = () => service.Save(original with { Description = "Must not be persisted" });
            action.Should().Throw<IOException>();
        }

        File.ReadAllText(original.FilePath).Should().Be(originalMarkdown);
        Directory.EnumerateFiles(service.AgentsDirectory, "*.tmp").Should().BeEmpty();
    }

    public void Dispose()
    {
        if (!Directory.Exists(_rootPath))
        {
            return;
        }

        try
        {
            Directory.Delete(_rootPath, true);
        }
        catch (IOException)
        {
        }
    }

    private DesktopAgentDefinitionService CreateService()
        => new(new StoragePaths(
            _rootPath,
            Path.Combine(_rootPath, "selfclaw.db"),
            Path.Combine(_rootPath, "secrets")));
}
