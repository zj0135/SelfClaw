#pragma warning disable MAAI001

using FluentAssertions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Infrastructure.Agents;

namespace SelfClaw.Tests.Runtime;

public sealed class FileSystemAgentContextProviderFactoryTests : IDisposable
{
    private readonly string _testRootPath = Path.Combine(
        Path.GetTempPath(),
        "SelfClaw.Tests",
        "Skills",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void DiscoverSkillRoots_returns_empty_when_assets_skills_directory_is_missing()
    {
        var factory = CreateFactory();

        factory.DiscoverSkillRoots().Should().BeEmpty();
        factory.CreateProviders().Should().BeEmpty();
    }

    [Fact]
    public void DiscoverSkillRoots_returns_assets_skill_root_when_skill_manifest_exists()
    {
        var skillDirectoryPath = Path.Combine(_testRootPath, "Assets", "skills", "code-review");
        Directory.CreateDirectory(skillDirectoryPath);
        File.WriteAllText(
            Path.Combine(skillDirectoryPath, "SKILL.md"),
            """
            ---
            name: code-review
            description: Review code and call out correctness risks.
            ---

            Review the selected code carefully and focus on correctness.
            """);

        var factory = CreateFactory();

        factory.DiscoverSkillRoots()
            .Should()
            .Equal(Path.GetFullPath(Path.Combine(_testRootPath, "Assets", "skills")));

        factory.CreateProviders()
            .Should()
            .ContainSingle()
            .Which.Should().BeOfType<AgentSkillsProvider>();
    }

    [Fact]
    public void DiscoverSkillRoots_returns_project_assets_skill_root_when_multiple_asset_roots_are_available()
    {
        var outputAssetsPath = Path.Combine(_testRootPath, "SelfClaw.Desktop", "bin", "Debug", "net10.0-windows", "Assets");
        Directory.CreateDirectory(outputAssetsPath);

        var projectSkillDirectoryPath = Path.Combine(_testRootPath, "SelfClaw.Desktop", "Assets", "skills", "code-review");
        Directory.CreateDirectory(projectSkillDirectoryPath);
        File.WriteAllText(
            Path.Combine(projectSkillDirectoryPath, "SKILL.md"),
            """
            ---
            name: code-review
            description: Review code and call out correctness risks.
            ---

            Review the selected code carefully and focus on correctness.
            """);

        var factory = new FileSystemAgentContextProviderFactory(
            NullLoggerFactory.Instance,
            Path.Combine(_testRootPath, "SelfClaw.Desktop", "Assets"),
            outputAssetsPath);

        factory.DiscoverSkillRoots()
            .Should()
            .Equal(Path.GetFullPath(Path.Combine(_testRootPath, "SelfClaw.Desktop", "Assets", "skills")));

        factory.CreateProviders()
            .Should()
            .ContainSingle()
            .Which.Should().BeOfType<AgentSkillsProvider>();
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRootPath))
        {
            Directory.Delete(_testRootPath, recursive: true);
        }
    }

    private FileSystemAgentContextProviderFactory CreateFactory()
        => new(NullLoggerFactory.Instance, Path.Combine(_testRootPath, "Assets"));
}

#pragma warning restore MAAI001
