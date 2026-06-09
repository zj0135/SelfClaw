using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SelfClaw.Core.Interfaces;
using SelfClaw.Infrastructure.Agents.Runtime.Compaction;
using SelfClaw.Infrastructure.Agents.Runtime.Context;
using SelfClaw.Infrastructure.Agents.Runtime.Execution;
using SelfClaw.Infrastructure.Agents.Runtime.Mcp;
using SelfClaw.Infrastructure.Agents.Runtime.Orchestration;
using SelfClaw.Infrastructure.AiProviders.Anthropic;
using SelfClaw.Infrastructure.AiProviders;
using SelfClaw.Infrastructure.AiProviders.Abstractions;
using SelfClaw.Infrastructure.AiProviders.OpenAi;
using SelfClaw.Infrastructure.Data.Sqlite;
using SelfClaw.Infrastructure.Data.Sqlite.Repositories;
using SelfClaw.Infrastructure.Options;
using SelfClaw.Infrastructure.Security;
using SelfClaw.Infrastructure.Tools.Transcript;
using SelfClaw.Infrastructure.Tools.Workspace;

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
        services.AddSingleton<IProfileRepository, SqliteProfileRepository>();
        services.AddSingleton<IConversationRepository, SqliteConversationRepository>();
        services.AddSingleton<IAiProviderRepository, SqliteAiProviderRepository>();
        services.AddSingleton<IAiProviderAdapter>(provider =>
            new OpenAiProviderAdapter(
                AiProviderKind.OpenAI,
                provider.GetService<ILogger<OpenAiProviderAdapter>>()));
        services.AddSingleton<IAiProviderAdapter>(provider =>
            new OpenAiProviderAdapter(
                AiProviderKind.OpenAICompatible,
                provider.GetService<ILogger<OpenAiProviderAdapter>>()));
        services.AddSingleton<IAiProviderAdapter>(provider =>
            new AnthropicProviderAdapter(
                provider.GetService<ILogger<AnthropicProviderAdapter>>(),
                provider.GetService<ILoggerFactory>(),
                provider));
        services.AddSingleton<IAiProviderRegistry, AiProviderRegistry>();
        services.AddSingleton<ISecretProtector, DpapiSecretProtector>();
        services.AddSingleton<IWorkspaceToolService, WorkspaceToolService>();
        services.AddSingleton<IAgentExecutionService>(provider =>
        {
            var loggerFactory = provider.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>();
            return new ChatClientAgentExecutionService(loggerFactory, provider);
        });
        services.AddSingleton<IAgentContextProviderFactory, FileSystemAgentContextProviderFactory>();
        services.AddSingleton<IWorkspaceMemoryInitializationService, WorkspaceMemoryInitializationService>();
        services.AddSingleton<IAgentMcpToolProvider, McpServerToolProvider>();
        services.AddSingleton<IAgentChatRuntime, SelfClawAgentChatRuntime>();
        services.AddSingleton<IConversationContextCompactionService>(provider =>
        {
            var loggerFactory = provider.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>();
            return new ConversationContextCompactionService(
                provider.GetRequiredService<IConversationRepository>(),
                provider.GetRequiredService<IAgentExecutionService>(),
                loggerFactory.CreateLogger<ConversationContextCompactionService>());
        });
        services.AddSingleton<MarkdownHtmlRenderer>();
        return services;
    }
}
