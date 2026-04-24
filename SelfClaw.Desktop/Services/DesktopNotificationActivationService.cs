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

        switch (action)
        {
            case DesktopNotificationArguments.OpenAppAction:
                return;
            case DesktopNotificationArguments.OpenConversationAction:
                if (TryParseGuid(arguments, DesktopNotificationArguments.ConversationIdKey, out var conversationId))
                {
                    await _mainWindowViewModel.OpenConversationFromNotificationAsync(conversationId);
                }
                return;
            case DesktopNotificationArguments.ApproveToolAction:
                if (TryParseGuid(arguments, DesktopNotificationArguments.ToolExecutionIdKey, out var approveToolExecutionId))
                {
                    await _mainWindowViewModel.ApproveToolExecutionAsync(approveToolExecutionId);
                }
                return;
            case DesktopNotificationArguments.RejectToolAction:
                if (TryParseGuid(arguments, DesktopNotificationArguments.ToolExecutionIdKey, out var rejectToolExecutionId))
                {
                    await _mainWindowViewModel.RejectToolExecutionAsync(rejectToolExecutionId);
                }
                return;
            default:
                _logger.LogDebug("Ignoring unsupported notification action '{Action}'.", action);
                return;
        }
    }

    private static bool TryParseGuid(
        IReadOnlyDictionary<string, string> arguments,
        string key,
        out Guid value)
    {
        if (arguments.TryGetValue(key, out var rawValue) && Guid.TryParse(rawValue, out value))
        {
            return true;
        }

        value = Guid.Empty;
        return false;
    }

}
