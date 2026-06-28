using Microsoft.Extensions.DependencyInjection;
using SelfClaw.Core.Interfaces;
using SelfClaw.Infrastructure.Agents.Cli;
using SelfClaw.Infrastructure.Agents.Cli.Definitions;
using SelfClaw.Infrastructure.Agents.Cli.Process;
using SelfClaw.Infrastructure.Agents.Cli.Session;
using SelfClaw.Infrastructure.Agents.Runtime;
using SelfClaw.Infrastructure.AiProviders.Abstractions;
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
        services.AddSingleton<ISecretProtector, DpapiSecretProtector>();
        services.AddSingleton<IWorkspaceToolService, WorkspaceToolService>();
        services.AddSingleton<CliCommandResolver>();
        services.AddSingleton<ICliAgentProcessHost, CliAgentProcessHost>();
        // Use the parameterless constructor so the built-in definitions (Claude) are seeded. Registering
        // the type directly would make DI pick the IEnumerable<CliAgentDefinition> constructor and resolve
        // it to an empty set, leaving the registry without any agents.
        services.AddSingleton(_ => new CliAgentRegistry());
        services.AddSingleton<ICliAgentSessionStore, SqliteCliAgentSessionStore>();
        services.AddSingleton<CliSessionResolver>();
        services.AddSingleton<CliAgentChatRuntime>();
        services.AddSingleton<IAgentChatRuntime, DispatchingAgentChatRuntime>();
        services.AddSingleton<MarkdownHtmlRenderer>();
        return services;
    }
}
