using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure;
using SelfClaw.Infrastructure.Agents.Runtime;
using SelfClaw.Infrastructure.AiProviders;
using SelfClaw.Infrastructure.AiProviders.Abstractions;
using SelfClaw.Infrastructure.AiProviders.Models;
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
    public async Task Dispatcher_routes_direct_mode_to_the_in_process_runtime()
    {
        var storagePaths = new StoragePaths(
            _rootPath,
            Path.Combine(_rootPath, "selfclaw.db"),
            Path.Combine(_rootPath, "secrets"));
        var services = new ServiceCollection();
        services.AddSelfClawInfrastructure(storagePaths);

        using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IAiProviderRepository>().InitializeAsync();
        var runtime = provider.GetRequiredService<IAgentChatRuntime>();
        var request = new ChatTurnRequest(
            Guid.NewGuid(),
            ModelProfileId: null,
            WorkspaceRoot: null,
            ConversationMode.Programming,
            new AgentRuntimeDefinition(
                "direct", "Direct", "test", AgentExecutionMode.Direct,
                AgentRuntimeDefinition.SystemToolPolicy, [], [], [], ""),
            CliAgent: null,
            CliModel: null,
            CliReasoningEffort: null,
            ToolPermissionMode.RequireApproval,
            ToolApprovalHandler: null,
            Messages: []);

        var events = new List<AgentStreamEvent>();
        await foreach (var streamEvent in runtime.StreamTurnAsync(request))
        {
            events.Add(streamEvent);
        }

        events.Should().ContainSingle().Which.Should().BeOfType<RunCompletedEvent>()
            .Which.ErrorMessage.Should().Contain("Direct").And.Contain("default");
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
        var adapters = provider.GetServices<IAiProviderAdapter>().ToArray();
        var registry = provider.GetRequiredService<IAiProviderRegistry>();
        var settingsService = provider.GetRequiredService<IAiProviderSettingsService>();
        var chatClientFactory = provider.GetRequiredService<IAiChatClientFactory>();
        var directRuntime = provider.GetRequiredService<DirectAgentChatRuntime>();

        repository.Should().BeOfType<SqliteAiProviderRepository>();
        runtime.Should().BeOfType<DispatchingAgentChatRuntime>();
        adapters.Select(adapter => adapter.ProviderKind).Should().BeEquivalentTo(new[]
        {
            AiProviderKind.OpenAI,
            AiProviderKind.OpenAICompatible,
            AiProviderKind.DeepSeek,
            AiProviderKind.Anthropic,
            AiProviderKind.Ollama,
            AiProviderKind.GoogleGemini,
            AiProviderKind.AzureOpenAI
        });
        registry.GetRequiredAdapter(AiProviderKind.DeepSeek).ProviderKind.Should().Be(AiProviderKind.DeepSeek);
        registry.GetRequiredAdapter(AiProviderKind.Anthropic).ProviderKind.Should().Be(AiProviderKind.Anthropic);
        registry.GetRequiredAdapter(AiProviderKind.Ollama).ProviderKind.Should().Be(AiProviderKind.Ollama);
        registry.GetRequiredAdapter(AiProviderKind.GoogleGemini).ProviderKind.Should().Be(AiProviderKind.GoogleGemini);
        registry.GetRequiredAdapter(AiProviderKind.AzureOpenAI).ProviderKind.Should().Be(AiProviderKind.AzureOpenAI);
        settingsService.Should().BeOfType<AiProviderSettingsService>();
        chatClientFactory.Should().BeOfType<AiChatClientFactory>();
        directRuntime.Should().NotBeNull();
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
