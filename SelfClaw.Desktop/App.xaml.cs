using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.WinUI.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SelfClaw.Desktop.Composition;
using SelfClaw.Desktop.Pet;
using SelfClaw.Desktop.Services.Appearance;
using SelfClaw.Desktop.Services.Notifications;
using SelfClaw.Desktop.Services.Plugins;
using SelfClaw.Desktop.Services.ProgrammingAssistant;
using SelfClaw.Desktop.Services.Runtime;
using SelfClaw.Desktop.Services.SystemTray;
using SelfClaw.Desktop.Services.Terminal;
using SelfClaw.Desktop.Services.Tools;
using SelfClaw.Desktop.Services.WebView;
using SelfClaw.Desktop.Services.Windowing;
using SelfClaw.Infrastructure;
using SelfClaw.Infrastructure.Options;
using Serilog;
using Serilog.Events;

namespace SelfClaw.Desktop;

public sealed partial class App : System.Windows.Application
{
    private IHost? _host;
    private StoragePaths? _storagePaths;
    private int _isShowingUnhandledExceptionDialog;
    private bool _toastActivationRegistered;
    private Task _startupTask = Task.CompletedTask;
    private Task? _shutdownTask;
    private bool _allowClose;
    private Action? _emergencyCleanup;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _startupTask = StartAsync();
    }

    private async Task StartAsync()
    {
        _storagePaths = StoragePathDefaults.CreateDefault();
        ConfigureLogging(_storagePaths);
        RegisterGlobalExceptionHandlers();

        try
        {
            ThemeMode = ThemeMode.System;

            var builder = Host.CreateApplicationBuilder();
            builder.Logging.ClearProviders();
            builder.Services.AddSerilog(Log.Logger, dispose: false);
            builder.Services.AddSelfClawInfrastructure(_storagePaths);
            builder.Services.AddSelfClawDesktop(Dispatcher);
            _host = builder.Build();

            Log.Information("SelfClaw starting. LogsDirectory={LogsDirectory}", _storagePaths.LogsDirectory);

            await _host.Services.InitializeSelfClawInfrastructureAsync();
            await _host.Services.GetRequiredService<ProgrammingAssistantSettingsService>().GetCurrentAsync();
            // 必须在窗口显示之前：MainWindow.OnSourceInitialized 要同步读缓存来定标题栏明暗，
            // 晚一步深色用户就会看到标题栏先白一下。
            await _host.Services.GetRequiredService<AppearanceSettingsService>().GetAsync();
            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            RegisterToastNotifications();
            await _host.StartAsync();
            var systemTrayService = _host.Services.GetRequiredService<SystemTrayService>();
            systemTrayService.RegisterMainWindow(mainWindow);
            var approvals = _host.Services.GetRequiredService<DesktopToolApprovalHandler>();
            _emergencyCleanup = () => { approvals.RejectAll(); systemTrayService.Dispose(); };
            _host.Services.GetRequiredService<DesktopConversationActivationService>().RegisterMainWindow(mainWindow);
            _host.Services.GetRequiredService<DesktopNotificationService>().RegisterMainWindow(mainWindow);
            MainWindow = mainWindow;
            mainWindow.Closing += OnMainWindowClosing;
            mainWindow.Show();
            await mainWindow.InitializeAsync();

            // 主窗口已被显式设为 Application.MainWindow,此后再显示 PetWindow 不会篡夺 MainWindow(见 §7.2)。
            await _host.Services.GetRequiredService<PetHost>().InitializeAsync();
        }
        catch (OperationCanceledException)
        {
            _shutdownTask = ShutdownCoreAsync(-1);
            await _shutdownTask;
            throw;
        }
        catch (Exception exception)
        {
            Log.Fatal(exception, "Application startup failed.");
            ShowFatalError("SelfClaw failed to start. The error was written to the log file.", exception.Message);
            _shutdownTask = ShutdownCoreAsync(-1);
            await _shutdownTask;
        }
    }

    internal Task RequestShutdownAsync()
        => _shutdownTask ??= ShutdownAfterStartupAsync();

    private async Task ShutdownAfterStartupAsync()
    {
        try { await _startupTask.WaitAsync(TimeSpan.FromSeconds(20)); }
        catch (TimeoutException exception) { Log.Error(exception, "Initialization did not settle before shutdown."); }
        await ShutdownCoreAsync(0);
    }

    private async void OnMainWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        await RequestShutdownAsync();
    }

    private async Task ShutdownCoreAsync(int exitCode)
    {
        UnregisterToastNotifications();
        using var cancellation = new CancellationTokenSource();
        try
        {
            await StopServicesAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(20));
        }
        catch (TimeoutException)
        {
            await cancellation.CancelAsync();
            Log.Error("Desktop shutdown exceeded its 20 second budget; unfinished work may require recovery.");
            exitCode = -1;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            Log.Error(exception, "Application shutdown failed.");
            exitCode = -1;
        }
        finally
        {
            _allowClose = true;
            Shutdown(exitCode);
        }
    }

    private async Task StopServicesAsync(CancellationToken cancellationToken)
    {
        if (_host is null) return;
        if (MainWindow is MainWindow)
        {
            var services = _host.Services;
            var routing = services.GetRequiredService<WebViewMessageRouter>().StopAsync(cancellationToken);
            await services.GetRequiredService<ConversationTurnEngine>().StopAdmissionsAsync(cancellationToken);
            services.GetRequiredService<DesktopToolApprovalHandler>().RejectAll();
            await Task.WhenAll(routing, _host.StopAsync(cancellationToken),
                services.GetRequiredService<PluginPanelHostController>().StopAsync(cancellationToken),
                services.GetRequiredService<TerminalHostController>().DisposeAsync().AsTask(),
                services.GetRequiredService<ConversationSessionCoordinator>().StopAsync(cancellationToken),
                services.GetRequiredService<PetHost>().StopAsync(cancellationToken));
        }
        else await _host.StopAsync(cancellationToken);
        if (_host is IAsyncDisposable asyncHost) await asyncHost.DisposeAsync();
        else _host.Dispose();
        _host = null;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        UnregisterToastNotifications();
        UnregisterGlobalExceptionHandlers();
        if (_host is not null)
        {
            Log.Error("Desktop exited before asynchronous shutdown completed.");
            try { _emergencyCleanup?.Invoke(); }
            catch (Exception exception) { Log.Error(exception, "Synchronous desktop cleanup failed."); }
        }
        Log.CloseAndFlush();
        base.OnExit(e);
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
        if (_shutdownTask is not null && e.Exception is OperationCanceledException) return;
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
            await activationService.HandleActivationAsync(args.Argument);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Failed to process a Windows toast activation.");
        }
    }
}
