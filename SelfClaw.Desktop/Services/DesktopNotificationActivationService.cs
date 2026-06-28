using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using SelfClaw.Desktop.ViewModels;
using Windows.Foundation.Collections;

namespace SelfClaw.Desktop.Services;

public sealed class DesktopNotificationActivationService
{
    private readonly MainWindowViewModel _mainWindowViewModel;
    private readonly SystemTrayService _systemTrayService;
    private readonly ILogger<DesktopNotificationActivationService> _logger;

    public DesktopNotificationActivationService(
        MainWindowViewModel mainWindowViewModel,
        SystemTrayService systemTrayService,
        ILogger<DesktopNotificationActivationService> logger)
    {
        _mainWindowViewModel = mainWindowViewModel;
        _systemTrayService = systemTrayService;
        _logger = logger;
    }

    public async Task HandleActivationAsync(string arguments, ValueSet userInput)
    {
        if (Application.Current?.Dispatcher is Dispatcher dispatcher && !dispatcher.CheckAccess())
        {
            var operation = dispatcher.InvokeAsync(() => HandleActivationCoreAsync(arguments, userInput), DispatcherPriority.Background);
            await operation.Task.Unwrap();
            return;
        }

        await HandleActivationCoreAsync(arguments, userInput);
    }

    private async Task HandleActivationCoreAsync(string argumentsText, ValueSet userInput)
    {
        var arguments = DesktopNotificationArguments.Parse(argumentsText);
        if (!arguments.TryGetValue(DesktopNotificationArguments.ActionKey, out var action) ||
            string.IsNullOrWhiteSpace(action))
        {
            _systemTrayService.ActivateMainWindow();
            return;
        }

        _systemTrayService.ActivateMainWindow();
        await _mainWindowViewModel.InitializeAsync();

        // 后端重构后，VM 仅保留发送→渲染主路径：打开指定会话 / 工具审批的入口方法已移除。
        // 通知激活因此只负责把主窗口带到前台（上面的 ActivateMainWindow / InitializeAsync），
        // OpenApp 之外的动作暂不处理，待后续按新架构重新接线。
        if (!string.Equals(action, DesktopNotificationArguments.OpenAppAction, StringComparison.Ordinal))
        {
            _logger.LogDebug("Ignoring unsupported notification action '{Action}'.", action);
        }
    }
}
