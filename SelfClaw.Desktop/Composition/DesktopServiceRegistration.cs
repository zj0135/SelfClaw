using SelfClaw.Desktop.Services.Agents;
using SelfClaw.Desktop.Services.Notifications;
using SelfClaw.Desktop.Services.Settings;
using SelfClaw.Desktop.Services.SystemTray;
using SelfClaw.Desktop.Services.Agents.Definitions;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.WinUI.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Runtime;
using SelfClaw.Desktop.Pet;
using SelfClaw.Desktop.Services.AiProviders;
using SelfClaw.Desktop.Services.AgentActivity;
using SelfClaw.Desktop.Services.Activities;
using SelfClaw.Desktop.Services.Appearance;
using SelfClaw.Desktop.Services.Extensions;
using SelfClaw.Desktop.Services.Git;
using SelfClaw.Desktop.Services.Extensions.Abstractions;
using SelfClaw.Desktop.Services.ProgrammingAssistant;
using SelfClaw.Desktop.Services.ProgrammingAssistant.Models;
using SelfClaw.Desktop.Services.Plugins;
using SelfClaw.Desktop.Services.Runtime;
using SelfClaw.Desktop.Services.Runtime.Abstractions;
using SelfClaw.Desktop.Services.Subagents;
using SelfClaw.Desktop.Services.Terminal;
using SelfClaw.Desktop.Services.Tools;
using SelfClaw.Desktop.Services.Windowing;
using SelfClaw.Desktop.Services.Terminal.Abstractions;
using SelfClaw.Desktop.Services.Transcript;
using SelfClaw.Desktop.Services.Transcript.Abstractions;
using SelfClaw.Desktop.Services.WebView;
using SelfClaw.Desktop.Services.Workspace;
using SelfClaw.Desktop.Services.Workspace.Abstractions;
using SelfClaw.Desktop.ViewModels;
using SelfClaw.Infrastructure;
using SelfClaw.Infrastructure.AiProviders.Abstractions;
using SelfClaw.Infrastructure.Extensions;
using SelfClaw.Infrastructure.Extensions.Abstractions;
using SelfClaw.Infrastructure.Extensions.Discovery;
using SelfClaw.Infrastructure.Options;
using Serilog;
using Serilog.Events;

namespace SelfClaw.Desktop.Composition;

internal static class DesktopServiceRegistration
{
    internal static void AddSelfClawDesktop(this IServiceCollection services, Dispatcher dispatcher)
    {
        services.AddSingleton(dispatcher);
        services.AddSingleton<DesktopAgentDefinitionService>();
        services.AddSingleton<SubagentDefinitionCatalog>();
        services.AddSingleton<DesktopSettingsJsonStore>();
        services.AddSingleton<DesktopToolApprovalHandler>();
        services.AddSingleton<AgentActivityCoordinator>();
        services.AddSingleton<DesktopNotificationService>();
        services.AddSingleton<DesktopNotificationActivationService>();
        services.AddSingleton<DesktopConversationActivationService>();
        services.AddSingleton<ToolApprovalPresenter>();
        services.AddSingleton<DesktopTurnFinalizer>();
        services.AddSingleton<ConversationTurnRecorder>();
        services.AddSingleton<SubagentTaskSnapshotSerializer>();
        services.AddSingleton<SubagentCompletionBatchSerializer>();
        services.AddSingleton<SubagentTaskWakeSignal>();
        services.AddSingleton<SubagentTaskExecutionRegistry>();
        services.AddSingleton<SubagentActivityRegistry>();
        services.AddSingleton<SubagentActivityService>();
        services.AddSingleton<SubagentTaskCoordinator>();
        services.AddSingleton<ISubagentTaskCoordinator>(services =>
            services.GetRequiredService<SubagentTaskCoordinator>());
        services.AddSingleton<ISubagentConversationLifecycle>(services =>
            services.GetRequiredService<SubagentTaskCoordinator>());
        services.AddSingleton<SubagentTaskExecutor>();
        services.AddSingleton<SubagentTaskBackgroundHost>();
        services.AddHostedService(services =>
            services.GetRequiredService<SubagentTaskBackgroundHost>());
        services.AddSingleton<SubagentContinuationExecutor>();
        services.AddSingleton<SubagentDeliveryDispatcher>();
        services.AddHostedService(services =>
            services.GetRequiredService<SubagentDeliveryDispatcher>());
        services.AddSingleton<IConversationCompletionNotifier, ConversationCompletionNotifier>();
        services.AddSingleton<ConversationTurnEngine>();
        services.AddSingleton<ConversationWorkspaceService>();
        services.AddSingleton<ConversationDeletionService>();
        services.AddSingleton<TranscriptProjection>();
        services.AddSingleton<WebViewHostChannel>();
        services.AddSingleton<TranscriptDelivery>();
        services.AddSingleton<ActivityPanelSnapshotBuilder>();
        services.AddSingleton(services => new ActivityPanelPublisher(
            services.GetRequiredService<SubagentActivityService>(),
            services.GetRequiredService<ActivityPanelSnapshotBuilder>(),
            services.GetRequiredService<IActivityPanelScopeSource>(),
            services.GetRequiredService<WebViewHostChannel>(),
            dispatcher,
            services.GetRequiredService<ILogger<ActivityPanelPublisher>>()));
        services.AddSingleton(services => new TranscriptPublisher(
            services.GetRequiredService<TranscriptProjection>(),
            services.GetRequiredService<TranscriptDelivery>(),
            dispatcher));
        services.AddSingleton<ITranscriptChangeSink>(services =>
            services.GetRequiredService<TranscriptPublisher>());
        services.AddSingleton<ConversationSessionCoordinator>();
        services.AddSingleton<ITerminalSessionFactory, ConPtyTerminalSessionFactory>();
        services.AddSingleton(services => new TerminalHostController(
            services.GetRequiredService<ITerminalSessionFactory>(),
            services.GetRequiredService<WebViewHostChannel>(),
            dispatcher, services.GetRequiredService<ILogger<TerminalHostController>>()));
        services.AddSingleton<IWorkspaceFolderPicker, WpfWorkspaceFolderPicker>();
        services.AddSingleton<PluginPanelResourceReader>();
        services.AddSingleton(services => new PluginPanelHostController(
            services.GetRequiredService<IPluginPanelCatalog>(),
            services.GetRequiredService<IExtensionPackageRepository>(),
            services.GetRequiredService<IPluginVersionLeaseManager>(),
            services.GetRequiredService<DesktopSettingsJsonStore>(),
            services.GetRequiredService<WebViewHostChannel>(),
            dispatcher,
            services.GetRequiredService<PluginPanelResourceReader>(),
            services.GetRequiredService<ILogger<PluginPanelHostController>>()));
        services.AddSingleton<IPluginPanelSessionRegistry>(services =>
            services.GetRequiredService<PluginPanelHostController>());
        services.AddSingleton(services => new PluginPanelContextPublisher(
            services.GetRequiredService<IPluginPanelContextSource>(),
            services.GetRequiredService<WebViewHostChannel>(),
            services.GetRequiredService<PluginPanelHostController>(),
            dispatcher));
        services.AddSingleton<PluginPanelBridge>();
        services.AddSingleton<CliProbeProcess>();
        services.AddSingleton<SelfClaw.Desktop.Services.ProgrammingAssistant.Abstractions.IProgrammingCliDiscovery, ProgrammingCliDiscovery>();
        services.AddSingleton<ProgrammingAssistantSettingsService>();
        services.AddHostedService<ProgrammingCliDiscoveryHost>();
        services.AddSingleton<ProgrammingAssistantSettingsBridge>();
        services.AddSingleton<AppearanceSettingsService>();
        services.AddSingleton<AppearanceSettingsBridge>();
        services.AddSingleton<AiProviderSettingsBridge>();
        services.AddSingleton<ExtensionSettingsBridge>();
        services.AddSingleton<AgentSettingsService>();
        services.AddSingleton<AgentSettingsBridge>();
        services.AddSingleton<IExtensionPackagePicker, ExtensionPackagePicker>();
        services.AddSingleton<PetPackageCatalog>();
        services.AddSingleton<PetActivityPresenter>();
        services.AddSingleton<IPetSettingsRepository, DesktopPetSettingsRepository>();
        services.AddSingleton<IPetWindowAdapter, WpfPetWindowAdapter>();
        services.AddSingleton<PetHost>(services => new PetHost(
            services.GetRequiredService<IPetSettingsRepository>(),
            services.GetRequiredService<IPetWindowAdapter>(),
            services.GetRequiredService<PetPackageCatalog>(),
            services.GetRequiredService<ILogger<PetHost>>()));
        services.AddSingleton<PetSettingsBridge>();
        services.AddSingleton<SystemTrayService>();
        services.AddSingleton(services => new MainWindowViewModel(
            services.GetRequiredService<IConversationRepository>(),
            services.GetRequiredService<ConversationTurnEngine>(),
            services.GetRequiredService<ConversationSessionCoordinator>(),
            services.GetRequiredService<AgentActivityCoordinator>(),
            services.GetRequiredService<TranscriptPublisher>(),
            services.GetRequiredService<AgentSettingsService>(),
            services.GetRequiredService<IExtensionStateChangeNotifier>(),
            services.GetRequiredService<DesktopSettingsJsonStore>(),
            services.GetRequiredService<ConversationWorkspaceService>(),
            services.GetRequiredService<ConversationDeletionService>(),
            services.GetRequiredService<ILogger<MainWindowViewModel>>(),
            dispatcher));
        services.AddSingleton<IWorkspaceSelectionController>(services =>
            services.GetRequiredService<MainWindowViewModel>());
        services.AddSingleton<IPluginPanelContextSource>(services =>
            services.GetRequiredService<MainWindowViewModel>());
        services.AddSingleton<WorkspaceSelectionBridge>();
        services.AddSingleton<GitWorkspaceBridge>();
        services.AddSingleton<IActivityPanelScopeSource>(services => services.GetRequiredService<MainWindowViewModel>());
        services.AddSingleton(services => new ActivityPanelBridge(
            services.GetRequiredService<ActivityPanelPublisher>(),
            services.GetRequiredService<SubagentActivityService>(),
            services.GetRequiredService<ISubagentTaskCoordinator>(),
            services.GetRequiredService<WebViewHostChannel>(), dispatcher));
        services.AddSingleton(services => new WebViewMessageRouter(
            services.GetRequiredService<AiProviderSettingsBridge>(),
            services.GetRequiredService<ExtensionSettingsBridge>(),
            services.GetRequiredService<AgentSettingsBridge>(),
            services.GetRequiredService<ProgrammingAssistantSettingsBridge>(),
            services.GetRequiredService<AppearanceSettingsBridge>(),
            services.GetRequiredService<PetSettingsBridge>(),
            services.GetRequiredService<WorkspaceSelectionBridge>(),
            services.GetRequiredService<TerminalHostController>(),
            services.GetRequiredService<PluginPanelHostController>(),
            services.GetRequiredService<PluginPanelBridge>(),
            services.GetRequiredService<MainWindowViewModel>(),
            services.GetRequiredService<ToolApprovalPresenter>(),
            services.GetRequiredService<WebViewHostChannel>(),
            services.GetRequiredService<TranscriptDelivery>(),
            services.GetRequiredService<GitWorkspaceBridge>(),
            services.GetRequiredService<ActivityPanelBridge>(),
            services.GetRequiredService<ILogger<WebViewMessageRouter>>()));
        services.AddSingleton(services => new MainWindow(
            services.GetRequiredService<MainWindowViewModel>(),
            services.GetRequiredService<PetActivityPresenter>(),
            services.GetRequiredService<WebViewHostChannel>(),
            services.GetRequiredService<WebViewMessageRouter>(),
            services.GetRequiredService<TerminalHostController>(),
            services.GetRequiredService<PluginPanelHostController>(),
            services.GetRequiredService<AppearanceSettingsService>(),
            services.GetRequiredService<StoragePaths>(),
            services.GetRequiredService<DesktopConversationActivationService>(),
            services.GetRequiredService<ILogger<MainWindow>>()));
    }
}
