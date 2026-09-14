using SelfClaw.Desktop.Services.Tools;
using Microsoft.Extensions.Logging;
using SelfClaw.Desktop.Services.Windowing;

namespace SelfClaw.Desktop.Services.Notifications;

internal sealed class DesktopNotificationActivationService
{
    private readonly DesktopConversationActivationService _activation;
    private readonly DesktopToolApprovalHandler _approvals;
    private readonly ILogger<DesktopNotificationActivationService> _logger;

    public DesktopNotificationActivationService(DesktopConversationActivationService activation,
        DesktopToolApprovalHandler approvals, ILogger<DesktopNotificationActivationService> logger)
    {
        _activation = activation;
        _approvals = approvals;
        _logger = logger;
    }

    public async Task<bool> HandleActivationAsync(string argumentsText)
    {
        var arguments = DesktopNotificationArguments.Parse(argumentsText);
        arguments.TryGetValue(DesktopNotificationArguments.ActionKey, out var action);
        switch (action)
        {
            case DesktopNotificationArguments.ApproveToolAction:
            case DesktopNotificationArguments.RejectToolAction:
                if (!arguments.TryGetValue(DesktopNotificationArguments.ToolExecutionIdKey, out var rawExecutionId) ||
                    !Guid.TryParse(rawExecutionId, out var executionId)) return false;
                var resolved = _approvals.TryResolve(executionId, action == DesktopNotificationArguments.ApproveToolAction);
                await _activation.ActivateAsync();
                return resolved;
            case DesktopNotificationArguments.OpenConversationAction:
                if (arguments.TryGetValue(DesktopNotificationArguments.ConversationIdKey, out var rawId) && Guid.TryParse(rawId, out var id))
                    return await _activation.ActivateAsync(id);
                return false;
            case null:
            case "":
            case DesktopNotificationArguments.OpenAppAction:
                return await _activation.ActivateAsync();
            default:
                _logger.LogWarning("Unsupported notification action {Action}.", action);
                return false;
        }
    }
}
