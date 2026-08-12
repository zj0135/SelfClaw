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
using SelfClaw.Desktop.Services;
using SelfClaw.Desktop.Services.AiProviders;
using SelfClaw.Desktop.Services.AgentActivity;
using SelfClaw.Desktop.Services.Extensions;
using SelfClaw.Desktop.Services.Git;
using SelfClaw.Desktop.Services.Extensions.Abstractions;
using SelfClaw.Desktop.Services.ProgrammingAssistant;
using SelfClaw.Desktop.Services.ProgrammingAssistant.Models;
using SelfClaw.Desktop.Services.Pet;
using SelfClaw.Desktop.Services.Runtime;
using SelfClaw.Desktop.Services.Runtime.Abstractions;
using SelfClaw.Desktop.Services.Subagents;
using SelfClaw.Desktop.Services.Terminal;
using SelfClaw.Desktop.Services.Terminal.Abstractions;
using SelfClaw.Desktop.Services.Transcript;
using SelfClaw.Desktop.Services.Transcript.Abstractions;
using SelfClaw.Desktop.Services.WebView;
using SelfClaw.Desktop.Services.Workspace;
using SelfClaw.Desktop.Services.Workspace.Abstractions;
using SelfClaw.Desktop.ViewModels;
using SelfClaw.Infrastructure;
using SelfClaw.Infrastructure.AiProviders.Abstractions;
using SelfClaw.Infrastructure.Extensions.Discovery;
using SelfClaw.Infrastructure.Options;
using Serilog;
using Serilog.Events;

namespace SelfClaw.Desktop;

public partial class App : System.Windows.Application
{
    private IHost? _host;
    private StoragePaths? _storagePaths;
    private int _isShowingUnhandledExceptionDialog;
    private bool _toastActivationRegistered;

    protected override async void OnStartup(StartupEventArgs e)
    {
        _storagePaths = StoragePaths.CreateDefault();
        ConfigureLogging(_storagePaths);
        RegisterGlobalExceptionHandlers();

        try
        {
            base.OnStartup(e);

            ThemeMode = ThemeMode.System;
            ShutdownMode = ShutdownMode.OnMainWindowClose;

            var builder = Host.CreateApplicationBuilder();
            builder.Logging.ClearProviders();
            builder.Services.AddSerilog(Log.Logger, dispose: false);
            builder.Services.AddSelfClawInfrastructure(_storagePaths);
            builder.Services.AddSingleton<DesktopAgentDefinitionService>();
            builder.Services.AddSingleton<SubagentDefinitionCatalog>();
            builder.Services.AddSingleton<DesktopSettingsJsonStore>();
            builder.Services.AddSingleton<DesktopToolApprovalHandler>();
            builder.Services.AddSingleton<AgentActivityCoordinator>();
            builder.Services.AddSingleton<DesktopNotificationService>();
            builder.Services.AddSingleton<DesktopNotificationActivationService>();
            builder.Services.AddSingleton<DesktopTurnFinalizer>();
            builder.Services.AddSingleton<ConversationTurnRecorder>();
            builder.Services.AddSingleton<SubagentTaskSnapshotSerializer>();
            builder.Services.AddSingleton<SubagentCompletionBatchSerializer>();
            builder.Services.AddSingleton<SubagentTaskPreflight>();
            builder.Services.AddSingleton<SubagentTaskWakeSignal>();
            builder.Services.AddSingleton<SubagentTaskExecutionRegistry>();
            builder.Services.AddSingleton<SubagentTaskCoordinator>();
            builder.Services.AddSingleton<ISubagentTaskCoordinator>(services =>
                services.GetRequiredService<SubagentTaskCoordinator>());
            builder.Services.AddSingleton<ISubagentConversationLifecycle>(services =>
                services.GetRequiredService<SubagentTaskCoordinator>());
            builder.Services.AddSingleton<SubagentTaskExecutor>();
            builder.Services.AddSingleton<SubagentTaskBackgroundHost>();
            builder.Services.AddHostedService(services =>
                services.GetRequiredService<SubagentTaskBackgroundHost>());
            builder.Services.AddSingleton<SubagentContinuationExecutor>();
            builder.Services.AddSingleton<SubagentDeliveryDispatcher>();
            builder.Services.AddHostedService(services =>
                services.GetRequiredService<SubagentDeliveryDispatcher>());
            builder.Services.AddSingleton<IConversationCompletionNotifier, ConversationCompletionNotifier>();
            builder.Services.AddSingleton<ConversationTurnEngine>();
            builder.Services.AddSingleton<TranscriptProjection>();
            builder.Services.AddSingleton<WebViewHostChannel>();
            builder.Services.AddSingleton(services => new TranscriptPublisher(
                services.GetRequiredService<TranscriptProjection>(),
                services.GetRequiredService<WebViewHostChannel>(),
                Dispatcher));
            builder.Services.AddSingleton<ITranscriptChangeSink>(services =>
                services.GetRequiredService<TranscriptPublisher>());
            builder.Services.AddSingleton<ConversationSessionCoordinator>();
            builder.Services.AddSingleton<ITerminalSessionFactory, ConPtyTerminalSessionFactory>();
            builder.Services.AddSingleton(services => new TerminalHostController(
                services.GetRequiredService<ITerminalSessionFactory>(),
                services.GetRequiredService<WebViewHostChannel>(),
                Dispatcher));
            builder.Services.AddSingleton<IWorkspaceFolderPicker, WpfWorkspaceFolderPicker>();
            builder.Services.AddSingleton<ProgrammingAssistantSettingsService>();
            builder.Services.AddSingleton<ProgrammingAssistantSettingsBridge>();
            builder.Services.AddSingleton<AiProviderSettingsBridge>();
            builder.Services.AddSingleton<ExtensionSettingsBridge>();
            builder.Services.AddSingleton<IExtensionPackagePicker, ExtensionPackagePicker>();
            builder.Services.AddSingleton<PetPackageCatalog>();
            builder.Services.AddSingleton<PetActivityPresenter>();
            builder.Services.AddSingleton<IPetSettingsRepository, DesktopPetSettingsRepository>();
            builder.Services.AddSingleton<IPetWindowAdapter, WpfPetWindowAdapter>();
            builder.Services.AddSingleton<PetHost>(services => new PetHost(
                services.GetRequiredService<IPetSettingsRepository>(),
                services.GetRequiredService<IPetWindowAdapter>(),
                services.GetRequiredService<PetPackageCatalog>(),
                services.GetRequiredService<ILogger<PetHost>>()));
            builder.Services.AddSingleton<PetSettingsBridge>();
            builder.Services.AddSingleton<SystemTrayService>();
            builder.Services.AddSingleton(services => new MainWindowViewModel(
                services.GetRequiredService<IConversationRepository>(),
                services.GetRequiredService<ConversationTurnEngine>(),
                services.GetRequiredService<ConversationSessionCoordinator>(),
                services.GetRequiredService<AgentActivityCoordinator>(),
                services.GetRequiredService<TranscriptPublisher>(),
                services.GetRequiredService<DesktopAgentDefinitionService>(),
                services.GetRequiredService<DesktopSettingsJsonStore>(),
                services.GetRequiredService<ISubagentConversationLifecycle>(),
                services.GetRequiredService<ILogger<MainWindowViewModel>>(),
                services.GetRequiredService<IGitWorkspaceManager>(),
                services.GetRequiredService<IGitWorkspaceQuery>(),
                services.GetRequiredService<IGitWorkspaceStore>()));
            builder.Services.AddSingleton<IWorkspaceSelectionController>(services =>
                services.GetRequiredService<MainWindowViewModel>());
            builder.Services.AddSingleton<WorkspaceSelectionBridge>();
            builder.Services.AddSingleton<GitWorkspaceBridge>();
            builder.Services.AddSingleton(services => new WebViewMessageRouter(
                services.GetRequiredService<AiProviderSettingsBridge>(),
                services.GetRequiredService<ExtensionSettingsBridge>(),
                services.GetRequiredService<IExtensionStateChangeNotifier>(),
                services.GetRequiredService<ProgrammingAssistantSettingsBridge>(),
                services.GetRequiredService<PetSettingsBridge>(),
                services.GetRequiredService<WorkspaceSelectionBridge>(),
                services.GetRequiredService<TerminalHostController>(),
                services.GetRequiredService<MainWindowViewModel>(),
                services.GetRequiredService<AgentActivityCoordinator>(),
                services.GetRequiredService<WebViewHostChannel>(),
                Dispatcher,
                services.GetRequiredService<GitWorkspaceBridge>()));
            builder.Services.AddSingleton(services => new MainWindow(
                services.GetRequiredService<MainWindowViewModel>(),
                services.GetRequiredService<DesktopNotificationService>(),
                services.GetRequiredService<DesktopToolApprovalHandler>(),
                services.GetRequiredService<PetActivityPresenter>(),
                services.GetRequiredService<AgentActivityCoordinator>(),
                services.GetRequiredService<WebViewHostChannel>(),
                services.GetRequiredService<WebViewMessageRouter>(),
                services.GetRequiredService<TerminalHostController>(),
                services.GetRequiredService<StoragePaths>()));
            _host = builder.Build();

            Log.Information("SelfClaw starting. LogsDirectory={LogsDirectory}", _storagePaths.LogsDirectory);

            await _host.Services.GetRequiredService<IConversationRepository>().InitializeAsync();
            await _host.Services.GetRequiredService<IAiProviderRepository>().InitializeAsync();
            await _host.Services.GetRequiredService<IExtensionPackageRepository>().InitializeAsync();
            await _host.Services.GetRequiredService<IExtensionCatalogReconciler>().ReconcileAsync();
            await _host.Services.GetRequiredService<UserSkillDiscoveryService>().DiscoverAndRegisterAsync();
            await _host.Services.GetRequiredService<ProgrammingAssistantSettingsService>().GetOrInitializeAsync();
            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            RegisterToastNotifications();
            await _host.StartAsync();
            var systemTrayService = _host.Services.GetRequiredService<SystemTrayService>();
            systemTrayService.RegisterMainWindow(mainWindow);
            MainWindow = mainWindow;
            mainWindow.Show();

            // 主窗口已被显式设为 Application.MainWindow,此后再显示 PetWindow 不会篡夺 MainWindow(见 §7.2)。
            await _host.Services.GetRequiredService<PetHost>().InitializeAsync();
        }
        catch (Exception exception)
        {
            Log.Fatal(exception, "Application startup failed.");
            ShowFatalError("SelfClaw failed to start. The error was written to the log file.", exception.Message);
            Shutdown(-1);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_host is not null)
            {
                await _host.StopAsync();

                if (_host is IAsyncDisposable asyncDisposableHost)
                {
                    await asyncDisposableHost.DisposeAsync();
                }
                else
                {
                    _host.Dispose();
                }

                _host = null;
            }
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Application shutdown failed.");
        }
        finally
        {
            UnregisterToastNotifications();
            UnregisterGlobalExceptionHandlers();
            Log.CloseAndFlush();
            base.OnExit(e);
        }
    }

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
    }

    private void UnregisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException -= OnCurrentDomainUnhandledException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled dispatcher exception.");
        e.Handled = true;
        ShowUnhandledExceptionDialog("An unexpected UI error was written to the log file.", e.Exception.Message);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unobserved task exception.");
        e.SetObserved();
    }

    private static void OnCurrentDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            Log.Fatal(exception, "Unhandled AppDomain exception. IsTerminating={IsTerminating}", e.IsTerminating);
            return;
        }

        Log.Fatal(
            "Unhandled AppDomain exception. IsTerminating={IsTerminating}. ExceptionObject={ExceptionObject}",
            e.IsTerminating,
            e.ExceptionObject);
    }

    private void ShowUnhandledExceptionDialog(string summary, string details)
    {
        if (Interlocked.Exchange(ref _isShowingUnhandledExceptionDialog, 1) != 0)
        {
            return;
        }

        try
        {
            System.Windows.MessageBox.Show(
                BuildUserFacingErrorMessage(summary, details),
                "SelfClaw",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
        finally
        {
            Interlocked.Exchange(ref _isShowingUnhandledExceptionDialog, 0);
        }
    }

    private void ShowFatalError(string summary, string details)
    {
        System.Windows.MessageBox.Show(
            BuildUserFacingErrorMessage(summary, details),
            "SelfClaw",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Error);
    }

    private string BuildUserFacingErrorMessage(string summary, string details)
    {
        var builder = new StringBuilder();
        builder.AppendLine(summary);

        if (!string.IsNullOrWhiteSpace(details))
        {
            builder.AppendLine();
            builder.AppendLine(details.Trim());
        }

        var logsDirectory = _storagePaths?.LogsDirectory;
        if (!string.IsNullOrWhiteSpace(logsDirectory))
        {
            builder.AppendLine();
            builder.AppendLine($"Log directory: {logsDirectory}");
        }

        return builder.ToString();
    }

    private static void ConfigureLogging(StoragePaths storagePaths)
    {
        Directory.CreateDirectory(storagePaths.LogsDirectory);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "SelfClaw")
            .WriteTo.File(
                Path.Combine(storagePaths.LogsDirectory, "selfclaw-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 31,
                shared: true,
                encoding: new UTF8Encoding(false),
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] ({SourceContext}) {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    private void RegisterToastNotifications()
    {
        if (_toastActivationRegistered)
        {
            return;
        }

        try
        {
            ToastNotificationManagerCompat.OnActivated += OnToastActivated;
            _toastActivationRegistered = true;
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Failed to register Windows toast activation.");
        }
    }

    private void UnregisterToastNotifications()
    {
        if (!_toastActivationRegistered)
        {
            return;
        }

        try
        {
            ToastNotificationManagerCompat.OnActivated -= OnToastActivated;
            _toastActivationRegistered = false;
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Failed to unregister Windows toast activation.");
        }
    }

    private async void OnToastActivated(ToastNotificationActivatedEventArgsCompat args)
    {
        if (_host is null)
        {
            return;
        }

        try
        {
            var activationService = _host.Services.GetRequiredService<DesktopNotificationActivationService>();
            await activationService.HandleActivationAsync(args.Argument, args.UserInput);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Failed to process a Windows toast activation.");
        }
    }
}
