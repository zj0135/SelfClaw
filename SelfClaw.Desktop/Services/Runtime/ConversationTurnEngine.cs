using Microsoft.Extensions.Logging;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Desktop.Services.AgentActivity;
using SelfClaw.Desktop.Services.ProgrammingAssistant;
using SelfClaw.Desktop.Services.Runtime.Abstractions;
using SelfClaw.Desktop.Services.Tools;

namespace SelfClaw.Desktop.Services.Runtime;

internal sealed class ConversationTurnEngine
{
    private readonly IConversationTurnRepository _turns;
    private readonly DesktopTurnFinalizer _turnFinalizer;
    private readonly ConversationTurnRecorder _turnRecorder;
    private readonly IAgentChatRuntime _agentChatRuntime;
    private readonly ConversationSessionCoordinator _sessions;
    private readonly ConversationRunCoordinator _runs;
    private readonly AgentActivityCoordinator _activity;
    private readonly DesktopToolApprovalHandler _approval;
    private readonly ProgrammingAssistantSettingsService _programmingAssistantSettings;
    private readonly IConversationCompletionNotifier _completionNotifier;
    private readonly ILogger<ConversationTurnEngine> _logger;

    public ConversationTurnEngine(IConversationTurnRepository turns, DesktopTurnFinalizer turnFinalizer,
        ConversationTurnRecorder turnRecorder, IAgentChatRuntime agentChatRuntime, ConversationSessionCoordinator sessions,
        ConversationRunCoordinator runs, AgentActivityCoordinator activity, DesktopToolApprovalHandler approval,
        ProgrammingAssistantSettingsService programmingAssistantSettings, IConversationCompletionNotifier completionNotifier,
        ILogger<ConversationTurnEngine> logger)
    {
        _turns = turns;
        _turnFinalizer = turnFinalizer;
        _turnRecorder = turnRecorder;
        _agentChatRuntime = agentChatRuntime;
        _sessions = sessions;
        _runs = runs;
        _activity = activity;
        _approval = approval;
        _programmingAssistantSettings = programmingAssistantSettings;
        _completionNotifier = completionNotifier;
        _logger = logger;
    }

    internal async Task<ConversationTurnExecutionResult> ExecuteAsync(ConversationRunHandle handle, DesktopConversationTurnRequest request)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(request);
        _runs.BeginExecution(handle);
        ConversationRuntimeState? state = null;
        AgentTurnState? turn = null;
        var activityStarted = false;
        string? error = null;
        try
        {
            ValidateRequest(handle, request);
            var conversation = CreateConversation(handle.ConversationId, request);
            state = await _sessions.PrepareRuntimeStateAsync(conversation, false, handle.CancellationToken);
            var started = await _turns.StartTurnAsync(new(conversation,
                new(handle.TurnId, conversation.Id, handle.Mode, handle.Origin, ConversationTurnStatus.Running, DateTimeOffset.UtcNow),
                request.Prompt, Guid.NewGuid()), handle.CancellationToken);
            turn = new AgentTurnState(started.Turn, request.Agent, handle.InputSession);
            state.ReplaceTurn(started.Turn);
            foreach (var message in started.Messages) state.ReplaceMessage(message);
            _runs.AttachState(handle, state);
            _turnRecorder.BeginTurn(state, turn);
            BeginActivity(conversation, request.Agent, turn);
            activityStarted = true;
            var chatRequest = await BuildChatTurnRequestAsync(handle, state, request);
            await StreamTurnAsync(handle, state, turn, chatRequest);
            if (!turn.Completed && turn.PendingFinalization is null)
                await FinalizeInterruptedAsync(state, turn, TurnFinalizationKind.Failed, AgentActivityOutcome.Failed,
                    "Agent stream ended before the turn reached a terminal state.");
            if (turn.Completed)
                _completionNotifier.Notify(conversation, state.Turns.Single(item => item.Id == turn.TurnId), state.Messages);
        }
        catch (OperationCanceledException)
        {
            handle.InputSession?.Cancel(handle.CancellationToken);
            if (state is not null && turn is not null && !turn.Completed && turn.PendingFinalization is null)
                await FinalizeInterruptedAsync(state, turn, TurnFinalizationKind.Cancelled, AgentActivityOutcome.Cancelled, "Generation stopped.");
            throw;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            handle.InputSession?.Fail(exception);
            _logger.LogError(exception, "Chat turn failed. RunId={RunId}", handle.RunId);
            if (state is not null && turn is not null && !turn.Completed && turn.PendingFinalization is null)
                await FinalizeInterruptedAsync(state, turn, TurnFinalizationKind.Failed, AgentActivityOutcome.Failed, exception.Message);
        }
        finally
        {
            try
            {
                handle.InputSession?.Close();
                if (activityStarted && turn is not null && !turn.Completed)
                    _activity.CompleteInterrupted(turn.TurnId, AgentActivityOutcome.Failed, "The turn could not be finalized.");
            }
            finally { _runs.Complete(handle); }
        }
        return new(state?.Turns.SingleOrDefault(item => item.Id == handle.TurnId), turn?.Completed == true, error);
    }

    private async Task StreamTurnAsync(ConversationRunHandle handle, ConversationRuntimeState state,
        AgentTurnState turn, ChatTurnRequest request)
    {
        handle.CancellationToken.ThrowIfCancellationRequested();
        await using var enumerator = _agentChatRuntime.StreamTurnAsync(request, handle.CancellationToken).GetAsyncEnumerator(handle.CancellationToken);
        try
        {
            while (await enumerator.MoveNextAsync())
            {
                var update = enumerator.Current;
                await _turnRecorder.ApplyEventAsync(state, turn, update, _turnFinalizer, handle.CancellationToken);
                _activity.ApplyEvent(turn.TurnId, update);
            }
        }
        catch (OperationCanceledException)
        {
            handle.InputSession?.Cancel(handle.CancellationToken);
            throw;
        }
        catch (Exception exception)
        {
            handle.InputSession?.Fail(exception);
            throw;
        }
        finally
        {
            // End the ACK waiter before disposing a producer that may itself be waiting for it.
            handle.InputSession?.Close();
        }
    }

    private async Task FinalizeInterruptedAsync(ConversationRuntimeState state, AgentTurnState turn,
        TurnFinalizationKind kind, AgentActivityOutcome outcome, string message)
    {
        await _turnRecorder.FinalizeInterruptedAsync(state, turn, kind, message, _turnFinalizer);
        _activity.CompleteInterrupted(turn.TurnId, outcome, message);
    }

    private async Task<ChatTurnRequest> BuildChatTurnRequestAsync(ConversationRunHandle handle,
        ConversationRuntimeState state, DesktopConversationTurnRequest request)
    {
        var snapshot = state.CaptureSnapshot();
        if (handle.Mode == AgentExecutionMode.Cli)
        {
            var cli = await _programmingAssistantSettings.GetSelectedInvocationAsync(handle.CancellationToken);
            return new CliChatTurnRequest(handle.TurnId, handle.ConversationId, request.WorkspaceRoot, request.Agent,
                snapshot.Messages, snapshot.Turns, cli?.Kind, cli?.Model, cli?.ReasoningEffort);
        }
        return new DirectChatTurnRequest(handle.TurnId, handle.ConversationId, request.WorkspaceRoot, request.Agent,
            snapshot.Messages, snapshot.Turns, request.ModelProfileId, request.ToolPermissionMode, _approval,
            new(DirectTurnOrigin.Interactive, null, null), snapshot.ToolRuns, InputSession: handle.InputSession);
    }

    private void BeginActivity(ConversationRecord conversation, AgentRuntimeDefinition agent, AgentTurnState turn)
        => _activity.BeginTurn(new AgentActivityContext(turn.TurnId, conversation.Id, conversation.Title,
            agent.Id, agent.Name, agent.Mode, turn.StartedAtUtc));

    private static void ValidateRequest(ConversationRunHandle handle, DesktopConversationTurnRequest request)
    {
        if (handle.Origin != DirectTurnOrigin.Interactive || request.Agent.Mode != handle.Mode ||
            (request.Conversation is { } conversation && (conversation.Id != handle.ConversationId || conversation.Kind != ConversationKind.Interactive)) ||
            (request.ConversationId is Guid id && id != handle.ConversationId))
            throw new InvalidOperationException("The submitted turn does not match its run reservation.");
        handle.CancellationToken.ThrowIfCancellationRequested();
    }

    private static ConversationRecord CreateConversation(Guid conversationId, DesktopConversationTurnRequest request)
    {
        var now = DateTimeOffset.UtcNow;
        var conversation = request.Conversation ?? new ConversationRecord(conversationId, "New chat", request.WorkspaceRoot?.Id,
            ConversationMode.Programming, request.ToolPermissionMode, request.Agent.Id, now, now);
        var title = request.Prompt.ReplaceLineEndings(" ").Trim();
        return conversation with
        {
            Title = conversation.Title == "New chat" ? (title.Length > 48 ? title[..48] + "..." : title) : conversation.Title,
            WorkspaceRootId = request.WorkspaceRoot?.Id,
            Mode = ConversationMode.Programming,
            ToolPermissionMode = request.ToolPermissionMode,
            UpdatedAtUtc = now
        };
    }
}
