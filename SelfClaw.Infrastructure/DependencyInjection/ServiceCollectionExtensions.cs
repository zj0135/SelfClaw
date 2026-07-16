using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SelfClaw.Core.Interfaces;
using SelfClaw.Infrastructure.Agents.Cli;
using SelfClaw.Infrastructure.Agents.Cli.Definitions;
using SelfClaw.Infrastructure.Agents.Cli.Process;
using SelfClaw.Infrastructure.Agents.Cli.Session;
using SelfClaw.Infrastructure.Agents.Runtime;
using SelfClaw.Infrastructure.AiProviders;
using SelfClaw.Infrastructure.AiProviders.Abstractions;
using SelfClaw.Infrastructure.AiProviders.Anthropic;
using SelfClaw.Infrastructure.AiProviders.Azure;
using SelfClaw.Infrastructure.AiProviders.Http;
using SelfClaw.Infrastructure.AiProviders.Gemini;
using SelfClaw.Infrastructure.AiProviders.Models;
using SelfClaw.Infrastructure.AiProviders.OpenAi;
using SelfClaw.Infrastructure.AiProviders.Ollama;
using SelfClaw.Infrastructure.Data.Sqlite;
using SelfClaw.Infrastructure.Data.Sqlite.Repositories;
using SelfClaw.Infrastructure.Options;
using SelfClaw.Infrastructure.Security;
using SelfClaw.Infrastructure.Tools.Transcript;
using SelfClaw.Infrastructure.Tools.Workspace;
using SelfClaw.Infrastructure.Agents.Cli.Definitions.Models;
using SelfClaw.Infrastructure.Agents.Cli.Process.Abstractions;

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
        services.AddSingleton<IConversationRepository, SqliteConversationRepository>();
        services.AddSingleton<IAiProviderRepository, SqliteAiProviderRepository>();
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
            new OpenAiProviderAdapter(
                AiProviderKind.DeepSeek,
                serviceProvider.GetService<ILogger<OpenAiProviderAdapter>>(),
                serviceProvider.GetRequiredService<OpenAiModelListClient>(),
                serviceProvider.GetRequiredService<AiProviderHttpClientProvider>()));
        services.AddSingleton<IAiProviderAdapter>(serviceProvider =>
            new AnthropicProviderAdapter(
                serviceProvider.GetService<ILogger<AnthropicProviderAdapter>>(),
                serviceProvider.GetService<ILoggerFactory>(),
                serviceProvider,
                serviceProvider.GetRequiredService<AnthropicModelListClient>()));
        services.AddSingleton<IAiProviderAdapter, OllamaProviderAdapter>();
        services.AddSingleton<IAiProviderAdapter, GeminiProviderAdapter>();
        services.AddSingleton<IAiProviderAdapter, AzureOpenAiProviderAdapter>();
        services.AddSingleton<IAiProviderRegistry, AiProviderRegistry>();
        services.AddSingleton<IAiProviderSettingsService, AiProviderSettingsService>();
        services.AddSingleton<IAiChatClientFactory, AiChatClientFactory>();
        services.AddSingleton<ISecretProtector, DpapiSecretProtector>();
        services.AddSingleton<IWorkspaceToolService, WorkspaceToolService>();
        services.AddSingleton<WorkspaceAgentToolset>();
        services.AddSingleton<CliCommandResolver>();
        services.AddSingleton<ICliAgentProcessHost, CliAgentProcessHost>();
        // Use the parameterless constructor so the built-in definitions (Claude) are seeded. Registering
        // the type directly would make DI pick the IEnumerable<CliAgentDefinition> constructor and resolve
        // it to an empty set, leaving the registry without any agents.
        services.AddSingleton(_ => new CliAgentRegistry());
        services.AddSingleton<ICliAgentSessionStore, SqliteCliAgentSessionStore>();
        services.AddSingleton<CliSessionResolver>();
        services.AddSingleton<CliAgentChatRuntime>();
        services.AddSingleton<DirectAgentChatRuntime>();
        services.AddSingleton<IAgentChatRuntime, DispatchingAgentChatRuntime>();
        services.AddSingleton<MarkdownHtmlRenderer>();
        return services;
    }
}
