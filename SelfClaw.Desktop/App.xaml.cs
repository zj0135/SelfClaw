using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.WinUI.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SelfClaw.Core.Interfaces;
using SelfClaw.Desktop.Pet;
using SelfClaw.Desktop.Services;
using SelfClaw.Desktop.Services.AiProviders;
using SelfClaw.Desktop.Services.ProgrammingAssistant;
using SelfClaw.Desktop.Services.ProgrammingAssistant.Models;
using SelfClaw.Desktop.Services.Runtime;
using SelfClaw.Desktop.ViewModels;
using SelfClaw.Infrastructure;
using SelfClaw.Infrastructure.AiProviders.Abstractions;
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
            builder.Services.AddSingleton<DesktopAgentStore>();
            builder.Services.AddSingleton<DesktopSettingsJsonStore>();
            builder.Services.AddSingleton<DesktopToolApprovalHandler>();
            builder.Services.AddSingleton<DesktopNotificationService>();
            builder.Services.AddSingleton<DesktopNotificationActivationService>();
            builder.Services.AddSingleton<DesktopTurnFinalizer>();
            builder.Services.AddSingleton<ConversationTurnEngine>();
            builder.Services.AddSingleton<ProgrammingAssistantSettingsService>();
            builder.Services.AddSingleton<AiProviderSettingsBridge>();
            builder.Services.AddSingleton<PetService>();
            builder.Services.AddSingleton<SystemTrayService>();
            builder.Services.AddSingleton<MainWindowViewModel>();
            builder.Services.AddSingleton<MainWindow>();
            _host = builder.Build();

            Log.Information("SelfClaw starting. LogsDirectory={LogsDirectory}", _storagePaths.LogsDirectory);

            await _host.StartAsync();
            await _host.Services.GetRequiredService<IConversationRepository>().InitializeAsync();
            await _host.Services.GetRequiredService<IAiProviderRepository>().InitializeAsync();
            await _host.Services.GetRequiredService<ProgrammingAssistantSettingsService>().GetOrInitializeAsync();
            RegisterToastNotifications();

            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            var systemTrayService = _host.Services.GetRequiredService<SystemTrayService>();
            systemTrayService.RegisterMainWindow(mainWindow);
            MainWindow = mainWindow;
            mainWindow.Show();

            // 主窗口已被显式设为 Application.MainWindow,此后再显示 PetWindow 不会篡夺 MainWindow(见 §7.2)。
            await _host.Services.GetRequiredService<PetService>().InitializeAsync();
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
