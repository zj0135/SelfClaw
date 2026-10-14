using Microsoft.Extensions.Logging;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Runtime;

namespace SelfClaw.Desktop.Services.Automation;

/// <summary>
/// Automation has no one to answer an approval prompt, so a call that still reaches the approval seam is
/// denied immediately. A denied call is recorded as a cancelled tool result in the transcript and never
/// suspends the run; the diagnostic explains why the unattended turn refused it.
/// </summary>
internal sealed class AutomationToolApprovalHandler : IToolApprovalHandler
{
    internal const string DenialReason =
        "SelfClaw denied this tool call because automation runs unattended and cannot wait for an approval decision.";

    private readonly ILogger<AutomationToolApprovalHandler> _logger;

    public AutomationToolApprovalHandler(ILogger<AutomationToolApprovalHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public Task<ToolApprovalDecision> RequestApprovalAsync(
        ToolApprovalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _logger.LogWarning(
            "Automation hook-forced approval denied for tool {ToolName} in conversation {ConversationId}: {Reason}",
            request.ToolName,
            request.ConversationId,
            DenialReason);
        return Task.FromResult(ToolApprovalDecision.Denied);
    }

    public bool TryResolve(Guid toolExecutionId, ToolApprovalDecision decision) => false;
}
