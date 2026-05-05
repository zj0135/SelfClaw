#pragma warning disable MAAI001

using FluentAssertions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Runtime;
using SelfClaw.Infrastructure.Agents.Runtime;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Tests.Infrastructure.Agents.Runtime;

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
        var agent = new AgentRuntimeDefinition(
            "build",
            "build",
            "Build agent",
            AgentExecutionMode.Direct,
            AgentRuntimeDefinition.SystemToolPolicy,
            [],
            [],
            [],
            string.Empty);

        factory.DiscoverSkillRoots([]).Should().BeEmpty();
        factory.CreateProviders(agent).Should().BeEmpty();
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRootPath))
        {
            Directory.Delete(_testRootPath, recursive: true);
        }
    }

    private FileSystemAgentContextProviderFactory CreateFactory()
        => new(NullLoggerFactory.Instance, CreateStoragePaths());

    private StoragePaths CreateStoragePaths()
        => new(
            Path.Combine(_testRootPath, "appdata"),
            Path.Combine(_testRootPath, "appdata", "selfclaw.db"),
            Path.Combine(_testRootPath, "appdata", "secrets"));
}

#pragma warning restore MAAI001
