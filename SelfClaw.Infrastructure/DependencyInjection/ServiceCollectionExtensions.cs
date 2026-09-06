using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SelfClaw.Core.Interfaces;
using SelfClaw.Infrastructure.Agents.Cli;
using SelfClaw.Infrastructure.Agents.Cli.Adapters;
using SelfClaw.Infrastructure.Agents.Cli.Adapters.Abstractions;
using SelfClaw.Infrastructure.Agents.Cli.Process;
using SelfClaw.Infrastructure.Agents.Cli.Session;
using SelfClaw.Infrastructure.Agents.Cli.Session.Abstractions;
using SelfClaw.Infrastructure.Agents.Runtime;
using SelfClaw.Infrastructure.AiProviders;
using SelfClaw.Infrastructure.AiProviders.Abstractions;
using SelfClaw.Infrastructure.AiProviders.Anthropic;
using SelfClaw.Infrastructure.AiProviders.Http;
using SelfClaw.Infrastructure.AiProviders.Models;
using SelfClaw.Infrastructure.AiProviders.OpenAi;
using SelfClaw.Infrastructure.AiProviders.Ollama;
using SelfClaw.Infrastructure.Data.Sqlite;
using SelfClaw.Infrastructure.Data.Sqlite.Repositories;
using SelfClaw.Infrastructure.Extensions;
using SelfClaw.Infrastructure.Extensions.Discovery;
using SelfClaw.Infrastructure.Extensions.Models;
using SelfClaw.Infrastructure.Extensions.Skills;
using SelfClaw.Infrastructure.Extensions.Abstractions;
using SelfClaw.Infrastructure.Extensions.Runtime;
using SelfClaw.Infrastructure.Extensions.Mcp;
using SelfClaw.Infrastructure.Extensions.Plugins;
using SelfClaw.Infrastructure.Options;
using SelfClaw.Infrastructure.Git;
using SelfClaw.Infrastructure.Security;
using SelfClaw.Infrastructure.Tools.Workspace;
using SelfClaw.Infrastructure.Agents.Cli.Process.Abstractions;
using SelfClaw.Infrastructure.Agents.Runtime.Abstractions;
using SelfClaw.Infrastructure.Agents.Subagents.Persistence;
using SelfClaw.Infrastructure.Agents.Subagents.Runtime;

namespace SelfClaw.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSelfClawInfrastructure(
        this IServiceCollection services,
        StoragePaths? storagePaths = null)
    {
        storagePaths ??= StoragePaths.CreateDefault();

        services.AddSingleton(storagePaths);
        services.AddSingleton<SqliteDatabase>();
        services.AddSingleton<SqliteConversationRepository>();
        services.AddSingleton<IConversationRepository>(serviceProvider =>
            serviceProvider.GetRequiredService<SqliteConversationRepository>());
        services.AddSingleton<ITurnFinalizationRepository>(serviceProvider =>
            serviceProvider.GetRequiredService<SqliteConversationRepository>());
        services.AddSingleton<SqliteGitWorkspaceRepository>();
        services.AddSingleton<IGitWorkspaceStore>(serviceProvider =>
            serviceProvider.GetRequiredService<SqliteGitWorkspaceRepository>());
        services.AddSingleton<GitCommandRunner>();
        services.AddSingleton<GitWorkspaceService>();
        services.AddSingleton<IGitWorkspaceQuery>(serviceProvider =>
            serviceProvider.GetRequiredService<GitWorkspaceService>());
        services.AddSingleton<IGitWorkspaceManager>(serviceProvider =>
            serviceProvider.GetRequiredService<GitWorkspaceService>());
        services.AddSingleton<IGitMergeManager, GitMergeService>();
        services.AddSingleton<SubagentCompletionEnvelopeFactory>();
        services.AddSingleton<SqliteSubagentTaskRepository>();
        services.AddSingleton<ISubagentTaskStore>(serviceProvider =>
            serviceProvider.GetRequiredService<SqliteSubagentTaskRepository>());
        services.AddSingleton<ISubagentTaskExecutionStore>(serviceProvider =>
            serviceProvider.GetRequiredService<SqliteSubagentTaskRepository>());
        services.AddSingleton<SqliteSubagentDeliveryRepository>();
        services.AddSingleton<ISubagentDeliveryStore>(serviceProvider =>
            serviceProvider.GetRequiredService<SqliteSubagentDeliveryRepository>());
        services.AddSingleton<IAiProviderRepository, SqliteAiProviderRepository>();
        services.AddSingleton<SqliteExtensionRepository>();
        services.AddSingleton<IExtensionPackageRepository>(serviceProvider =>
            serviceProvider.GetRequiredService<SqliteExtensionRepository>());
        services.AddSingleton<IMcpServerRepository>(serviceProvider =>
            serviceProvider.GetRequiredService<SqliteExtensionRepository>());
        services.AddSingleton(new ExtensionPackageLimits(
            100L * 1024 * 1024,
            300L * 1024 * 1024,
            5000,
            50L * 1024 * 1024,
            256L * 1024));
        services.AddSingleton<SkillPackageReader>();
        services.AddSingleton<PluginManifestReader>();
        services.AddSingleton<PluginVersionLeaseManager>();
        services.AddSingleton<IPluginVersionLeaseManager>(serviceProvider =>
            serviceProvider.GetRequiredService<PluginVersionLeaseManager>());
        services.AddSingleton<SkillTokenParser>();
        services.AddSingleton<SkillRuntimeToolset>();
        services.AddSingleton<McpToolAdapter>();
        services.AddSingleton<PluginContributionService>();
        services.AddSingleton<ExtensionStateChangeNotifier>();
        services.AddSingleton<IExtensionStateChangeNotifier>(serviceProvider =>
            serviceProvider.GetRequiredService<ExtensionStateChangeNotifier>());
        services.AddSingleton<ExtensionPackageInstaller>();
        services.AddSingleton<ExtensionCatalog>();
        services.AddSingleton<IExtensionCatalogReconciler>(serviceProvider =>
            serviceProvider.GetRequiredService<ExtensionCatalog>());
        services.AddSingleton<UserSkillDiscoveryService>(serviceProvider =>
            new UserSkillDiscoveryService(
                UserSkillDiscoveryService.DefaultUserSkillsRoot,
                serviceProvider.GetRequiredService<IExtensionPackageRepository>(),
                serviceProvider.GetRequiredService<SkillPackageReader>(),
                serviceProvider.GetRequiredService<ILogger<UserSkillDiscoveryService>>()));
        services.AddSingleton<McpConfigurationResolver>();
        services.AddSingleton<McpTransportFactory>();
        services.AddSingleton<IMcpClientConnectionFactory, SdkMcpClientConnectionFactory>();
        services.AddSingleton<McpClientManager>();
        services.AddSingleton<IMcpClientManager>(serviceProvider =>
            serviceProvider.GetRequiredService<McpClientManager>());
        services.AddSingleton<IExtensionSettingsService, ExtensionSettingsService>();
        services.AddSingleton<DirectPromptComposer>();
        services.AddSingleton<CapabilityContentCache>();
        services.AddSingleton<SkillCapabilitySource>();
        services.AddSingleton<PluginCapabilitySource>();
        services.AddSingleton<McpCapabilitySource>();
        services.AddSingleton(serviceProvider => new SubagentCapabilitySource(
            serviceProvider.GetService<ISubagentTaskCoordinator>()));
        services.AddSingleton<IDirectTurnCapabilityResolver, DirectTurnCapabilityResolver>();
        services.AddSingleton<AiProviderHttpClientProvider>();
        services.AddSingleton<OpenAiModelListClient>();
        services.AddSingleton<AnthropicModelListClient>();
        services.AddSingleton<IAiProviderAdapter>(serviceProvider =>
            new OpenAiProviderAdapter(
                AiProviderKind.OpenAI,
                serviceProvider.GetService<ILogger<OpenAiProviderAdapter>>(),
                serviceProvider.GetRequiredService<OpenAiModelListClient>(),
                serviceProvider.GetRequiredService<AiProviderHttpClientProvider>()));
        services.AddSingleton<IAiProviderAdapter>(serviceProvider =>
            new OpenAiProviderAdapter(
                AiProviderKind.OpenAICompatible,
                serviceProvider.GetService<ILogger<OpenAiProviderAdapter>>(),
                serviceProvider.GetRequiredService<OpenAiModelListClient>(),
                serviceProvider.GetRequiredService<AiProviderHttpClientProvider>()));
        services.AddSingleton<IAiProviderAdapter>(serviceProvider =>
            new AnthropicProviderAdapter(
                serviceProvider.GetService<ILogger<AnthropicProviderAdapter>>(),
                serviceProvider.GetService<ILoggerFactory>(),
                serviceProvider,
                serviceProvider.GetRequiredService<AnthropicModelListClient>(),
                serviceProvider.GetRequiredService<AiProviderHttpClientProvider>()));
        services.AddSingleton<IAiProviderAdapter, OllamaProviderAdapter>();
        services.AddSingleton<IAiProviderRegistry, AiProviderRegistry>();
        services.AddSingleton<IAiProviderSettingsService, AiProviderSettingsService>();
        services.AddSingleton<IAiChatClientFactory, AiChatClientFactory>();
        services.AddSingleton<ISecretProtector, DpapiSecretProtector>();
        services.AddSingleton<IWorkspaceToolService, WorkspaceToolService>();
        services.AddSingleton<WorkspaceAgentToolset>();
        services.AddSingleton<CliCommandResolver>();
        services.AddSingleton<ICliAgentProcessHost, CliAgentProcessHost>();
        services.AddSingleton<ICliAgentAdapter, ClaudeCliAgentAdapter>();
        services.AddSingleton<ICliAgentAdapter, CodexCliAgentAdapter>();
        services.AddSingleton<ICliAgentAdapter, OpenCodeCliAgentAdapter>();
        services.AddSingleton<CliAgentAdapterRegistry>();
        services.AddSingleton<ICliAgentSessionStore, SqliteCliAgentSessionStore>();
        services.AddSingleton<CliAgentChatRuntime>();
        services.AddSingleton<DirectAgentChatRuntime>();
        services.AddSingleton<IAgentRuntimeAdapter>(serviceProvider =>
            serviceProvider.GetRequiredService<CliAgentChatRuntime>());
        services.AddSingleton<IAgentRuntimeAdapter>(serviceProvider =>
            serviceProvider.GetRequiredService<DirectAgentChatRuntime>());
        services.AddSingleton<IAgentChatRuntime>(serviceProvider =>
            new DispatchingAgentChatRuntime(
                serviceProvider.GetServices<IAgentRuntimeAdapter>(),
                serviceProvider.GetService<ILogger<DispatchingAgentChatRuntime>>()));
        return services;
    }
}
