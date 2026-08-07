using FluentAssertions;
using SelfClaw.Desktop.Services;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Tests.Desktop.Services.Agents;

public sealed class SubagentDefinitionCatalogTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(),
        "SelfClawTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void LoadAll_maps_a_valid_definition_and_applies_safe_defaults()
    {
        var modelProfileId = Guid.NewGuid();
        var catalog = CreateCatalog();
        WriteDefinition(
            catalog,
            "Reviewer",
            $$"""
            ---
            name: Code reviewer
            description: Reviews delegated changes.
            modelProfileId: {{modelProfileId:D}}
            plugins:
              - engineering-workflows
            skills:
              - code-review
            mcpServers:
              - github-readonly
            ---
            Review only the delegated task.
            """);

        var definition = catalog.LoadAll().Should().ContainSingle().Subject;

        definition.Id.Should().Be("reviewer");
        definition.Name.Should().Be("Code reviewer");
        definition.ModelProfileId.Should().Be(modelProfileId);
        definition.ToolPolicy.Should().Be(SubagentDefinitionCatalog.DefaultToolPolicy);
        definition.MaxRunSeconds.Should().Be(SubagentDefinitionCatalog.DefaultMaxRunSeconds);
        definition.PluginIds.Should().Equal("engineering-workflows");
        definition.SkillIds.Should().Equal("code-review");
        definition.McpServerIds.Should().Equal("github-readonly");
        definition.Instructions.Should().Be("Review only the delegated task.");
        definition.IsValid.Should().BeTrue();
        definition.Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void Get_normalizes_the_definition_id_and_rejects_invalid_ids()
    {
        var catalog = CreateCatalog();
        WriteDefinition(
            catalog,
            "reviewer",
            """
            ---
            name: Reviewer
            description: Reviews changes.
            tools: none
            maxRunSeconds: 30
            ---
            Review the task.
            """);

        catalog.Get(" REVIEWER ").Should().NotBeNull();
        catalog.Get("nested/reviewer").Should().BeNull();
    }

    [Theory]
    [MemberData(nameof(InvalidDefinitions))]
    public void LoadAll_marks_strict_contract_violations_invalid(string metadata, string body, string diagnostic)
    {
        var catalog = CreateCatalog();
        WriteDefinition(
            catalog,
            Guid.NewGuid().ToString("N"),
            $"---\n{metadata}\n---\n{body}");

        var definition = catalog.LoadAll().Should().ContainSingle().Subject;

        definition.IsValid.Should().BeFalse();
        definition.Diagnostics.Should().Contain(item => item.Contains(diagnostic, StringComparison.Ordinal));
    }

    public static TheoryData<string, string, string> InvalidDefinitions => new()
    {
        {
            "name: Reviewer\ndescription: Reviews changes.\nunknown: value",
            "Review the task.",
            "Unsupported subagent front matter field 'unknown'."
        },
        {
            "name: Reviewer\ndescription: Reviews changes.\nmodelProfileId: invalid",
            "Review the task.",
            "modelProfileId 'invalid' is invalid"
        },
        {
            "name: Reviewer\ndescription: Reviews changes.\ntools: write-only",
            "Review the task.",
            "tools value 'write-only' is invalid"
        },
        {
            "name: Reviewer\ndescription: Reviews changes.\nmaxRunSeconds: 29",
            "Review the task.",
            "must be between 30 and 3600"
        },
        {
            "name: Reviewer\ndescription: Reviews changes.",
            "   ",
            "Subagent instructions are required."
        }
    };

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

    private SubagentDefinitionCatalog CreateCatalog()
        => new(new StoragePaths(
            _rootPath,
            Path.Combine(_rootPath, "selfclaw.db"),
            Path.Combine(_rootPath, "secrets")));

    private static void WriteDefinition(SubagentDefinitionCatalog catalog, string id, string markdown)
    {
        Directory.CreateDirectory(catalog.SubagentsDirectory);
        File.WriteAllText(Path.Combine(catalog.SubagentsDirectory, $"{id}.md"), markdown);
    }
}
