using Microsoft.Extensions.Logging;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Desktop.Services.Workspace;
using SelfClaw.Desktop.Services.Workspace.Models;
using SelfClaw.Desktop.ViewModels;

namespace SelfClaw.Desktop.Services.Runtime;

/// <summary>
/// Admits one CLI turn through the run coordinator. Direct sends stay on the input queue.
/// </summary>
internal sealed class CliConversationSubmitter
{
    private readonly ConversationRunCoordinator _runs;
    private readonly ConversationTurnEngine _engine;
    private readonly ConversationWorkspaceService _workspaces;
    private readonly ILogger _logger;

    public CliConversationSubmitter(ConversationRunCoordinator runs, ConversationTurnEngine engine,
        ConversationWorkspaceService workspaces, ILogger logger)
    {
        _runs = runs;
        _engine = engine;
        _workspaces = workspaces;
        _logger = logger;
    }

    public async Task<CliPromptSubmissionResult> SubmitAsync(PromptSubmissionSnapshot submission,
        AgentRuntimeDefinition agent, Guid conversationId, Func<bool> selectionCurrent)
    {
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(selectionCurrent);
        PreparedConversationWorkspace? prepared = null;
        ConversationRunHandle? handle = null;
        var handedOff = false;
        var started = false;
        try
        {
            handle = _runs.TryReserve(conversationId, agent.Mode, DirectTurnOrigin.Interactive);
            if (handle is null)
                return new(false, "当前会话正在执行或应用正在退出，请稍候。", null, null);
            prepared = await _workspaces.PrepareAsync(conversationId, submission.Conversation, submission.WorkspaceRoot,
                submission.WorkspaceMode, submission.Prompt, handle.CancellationToken).ConfigureAwait(false);
            handle.CancellationToken.ThrowIfCancellationRequested();
            if (!selectionCurrent())
                return new(false, "当前选择已改变，请重试。", null, null);
            var request = new DesktopConversationTurnRequest(submission.Conversation, agent, submission.Prompt,
                submission.ModelProfileId, prepared.WorkspaceRoot, submission.ToolPermissionMode, conversationId);
            handedOff = true;
            _ = RunAsync(handle, request);
            var conversation = await handle.Started.ConfigureAwait(false);
            if (conversation is null)
                return new(false, "无法开始回合，请重试。", null, null);
            started = true;
            return new(true, null, conversation, prepared.Provisioned ? prepared.WorkspaceRoot : null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to prepare the chat turn.");
            return new(false, exception.Message, null, null);
        }
        finally
        {
            try
            {
                if (!started && prepared?.Provisioned == true) await _workspaces.DiscardAsync(prepared).ConfigureAwait(false);
            }
            finally
            {
                if (!handedOff && handle is not null) _runs.Complete(handle, false);
            }
        }
    }

    private async Task RunAsync(ConversationRunHandle handle, DesktopConversationTurnRequest request)
    {
        try
        {
            await _engine.ExecuteAsync(handle, request).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Accepted chat turn failed outside the turn engine.");
        }
    }
}
