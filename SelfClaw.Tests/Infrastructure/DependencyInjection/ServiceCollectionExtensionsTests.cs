using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SelfClaw.Core.Interfaces;
using SelfClaw.Infrastructure;
using SelfClaw.Infrastructure.Agents.Runtime;
using SelfClaw.Infrastructure.AiProviders.Abstractions;
using SelfClaw.Infrastructure.Data.Sqlite.Repositories;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Tests.Infrastructure.DependencyInjection;

public sealed class ServiceCollectionExtensionsTests : IDisposable
{
    private readonly string _rootPath;

    public ServiceCollectionExtensionsTests()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public void AddSelfClawInfrastructure_registers_repository_and_runtime_services()
    {
        var storagePaths = new StoragePaths(
            _rootPath,
            Path.Combine(_rootPath, "selfclaw.db"),
            Path.Combine(_rootPath, "secrets"));
        var services = new ServiceCollection();

        services.AddSelfClawInfrastructure(storagePaths);

        using var provider = services.BuildServiceProvider();
        var repository = provider.GetRequiredService<IAiProviderRepository>();
        var runtime = provider.GetRequiredService<IAgentChatRuntime>();

        repository.Should().BeOfType<SqliteAiProviderRepository>();
        runtime.Should().BeOfType<DispatchingAgentChatRuntime>();
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
}
