using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SelfClaw.Core.Interfaces;
using SelfClaw.Desktop.Services;
using SelfClaw.Desktop.ViewModels;
using SelfClaw.Infrastructure;

namespace SelfClaw.Desktop;

public partial class App : Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ThemeMode = ThemeMode.System;

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSelfClawInfrastructure();
        builder.Services.AddSingleton<DesktopSettingsStore>();
        builder.Services.AddSingleton<DesktopChannelManager>();
        builder.Services.AddSingleton<DesktopToolApprovalHandler>();
        builder.Services.AddSingleton<MainWindowViewModel>();
        builder.Services.AddSingleton<MainWindow>();
        _host = builder.Build();

        await _host.StartAsync();
        await _host.Services.GetRequiredService<IProfileRepository>().InitializeAsync();
        await _host.Services.GetRequiredService<IConversationRepository>().InitializeAsync();

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            var channelManager = _host.Services.GetService<DesktopChannelManager>();
            if (channelManager is not null)
            {
                await channelManager.DisposeAsync();
            }

            await _host.StopAsync();
            _host.Dispose();
        }

        base.OnExit(e);
    }
}
