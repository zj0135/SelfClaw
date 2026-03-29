using Microsoft.Extensions.DependencyInjection;
using SelfClaw.Core.Interfaces;
using SelfClaw.Infrastructure.Agents;
using SelfClaw.Infrastructure.Data;
using SelfClaw.Infrastructure.Options;
using SelfClaw.Infrastructure.Repositories;
using SelfClaw.Infrastructure.Security;
using SelfClaw.Infrastructure.Tools;

namespace SelfClaw.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSelfClawInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton(StoragePaths.CreateDefault());
        services.AddSingleton<SqliteDatabase>();
        services.AddSingleton<IProfileRepository, SqliteProfileRepository>();
        services.AddSingleton<IConversationRepository, SqliteConversationRepository>();
        services.AddSingleton<ISecretProtector, DpapiSecretProtector>();
        services.AddSingleton<IWorkspaceToolService, WorkspaceToolService>();
        services.AddSingleton<IAgentChatRuntime, SelfClawAgentChatRuntime>();
        services.AddSingleton<MarkdownHtmlRenderer>();
        return services;
    }
}