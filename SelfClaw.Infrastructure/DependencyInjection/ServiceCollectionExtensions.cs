using Microsoft.Extensions.DependencyInjection;
using SelfClaw.Core.Interfaces;
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
        services.AddSingleton<IAgentChatRuntime, PlaceholderAgentChatRuntime>();
        services.AddSingleton<MarkdownHtmlRenderer>();
        return services;
    }
}
