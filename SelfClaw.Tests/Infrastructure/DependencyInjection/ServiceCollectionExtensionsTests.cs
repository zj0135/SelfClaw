using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SelfClaw.Infrastructure;
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
    public void AddSelfClawInfrastructure_registers_ai_provider_services()
    {
        var storagePaths = new StoragePaths(
            _rootPath,
            Path.Combine(_rootPath, "selfclaw.db"),
            Path.Combine(_rootPath, "secrets"));
        var services = new ServiceCollection();

        services.AddSelfClawInfrastructure(storagePaths);

        using var provider = services.BuildServiceProvider();
        var adapters = provider.GetServices<IAiProviderAdapter>().ToArray();
        var registry = provider.GetRequiredService<IAiProviderRegistry>();
        var repository = provider.GetRequiredService<IAiProviderRepository>();

        adapters.Select(adapter => adapter.ProviderKind)
            .Should()
            .BeEquivalentTo([AiProviderKind.OpenAI, AiProviderKind.OpenAICompatible, AiProviderKind.Anthropic]);
        registry.GetRequiredAdapter(AiProviderKind.OpenAI).ProviderKind.Should().Be(AiProviderKind.OpenAI);
        registry.GetRequiredAdapter(AiProviderKind.OpenAICompatible).ProviderKind.Should().Be(AiProviderKind.OpenAICompatible);
        registry.GetRequiredAdapter(AiProviderKind.Anthropic).ProviderKind.Should().Be(AiProviderKind.Anthropic);
        repository.Should().BeOfType<SqliteAiProviderRepository>();
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
