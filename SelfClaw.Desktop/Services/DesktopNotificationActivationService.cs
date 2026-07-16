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
    private readonly DesktopToolApprovalHandler _toolApprovalHandler;
    private readonly ILogger<DesktopNotificationActivationService> _logger;

    public DesktopNotificationActivationService(
        MainWindowViewModel mainWindowViewModel,
        SystemTrayService systemTrayService,
        DesktopToolApprovalHandler toolApprovalHandler,
        ILogger<DesktopNotificationActivationService> logger)
    {
        _mainWindowViewModel = mainWindowViewModel;
        _systemTrayService = systemTrayService;
        _toolApprovalHandler = toolApprovalHandler;
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

        if (string.Equals(action, DesktopNotificationArguments.ApproveToolAction, StringComparison.Ordinal) ||
            string.Equals(action, DesktopNotificationArguments.RejectToolAction, StringComparison.Ordinal))
        {
            if (!arguments.TryGetValue(DesktopNotificationArguments.ToolExecutionIdKey, out var executionIdText) ||
                !Guid.TryParse(executionIdText, out var executionId))
            {
                _logger.LogWarning("Ignoring tool approval notification with an invalid execution id.");
                return;
            }

            var approved = string.Equals(
                action,
                DesktopNotificationArguments.ApproveToolAction,
                StringComparison.Ordinal);
            if (!_toolApprovalHandler.TryResolve(executionId, approved))
            {
                _logger.LogDebug("Tool approval '{ToolExecutionId}' was already resolved or expired.", executionId);
            }

            return;
        }

        await _mainWindowViewModel.InitializeAsync();

        // Conversation activation still only brings the app to the foreground;
        // tool approval actions are handled above before any potentially slow initialization.
        if (!string.Equals(action, DesktopNotificationArguments.OpenAppAction, StringComparison.Ordinal))
        {
            _logger.LogDebug("Ignoring unsupported notification action '{Action}'.", action);
        }
    }
}
