using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Desktop.Services.AgentActivity;
using SelfClaw.Desktop.Services.ProgrammingAssistant;
using SelfClaw.Desktop.Services.Runtime.Abstractions;
using SelfClaw.Infrastructure.AiProviders.Abstractions;
using SelfClaw.Infrastructure.AiProviders.Models;

namespace SelfClaw.Desktop.Services.Runtime;

/// <summary>
/// Executes one desktop turn from admission and conversation persistence through runtime streaming and terminal
/// projection. Direct and CLI turns share this workflow because both produce the same agent event stream.
/// </summary>
internal sealed class ConversationTurnEngine : IDisposable
{
    private readonly IConversationRepository _conversationRepository;
    private readonly DesktopTurnFinalizer _turnFinalizer;
    private readonly ConversationTurnRecorder _turnRecorder;
    private readonly IAgentChatRuntime _agentChatRuntime;
    private readonly ConversationSessionCoordinator _conversationSessions;
    private readonly AgentActivityCoordinator _agentActivityCoordinator;
    private readonly DesktopToolApprovalHandler _toolApprovalHandler;
    private readonly ProgrammingAssistantSettingsService _programmingAssistantSettings;
    private readonly IAiProviderSettingsService _aiProviderSettings;
    private readonly IConversationCompletionNotifier _completionNotifier;
    private readonly ILogger<ConversationTurnEngine> _logger;
    private readonly SemaphoreSlim _turnAdmissionGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, byte> _deletingConversations = new();
    private int _pendingInteractiveAdmissions;
    private int _disposeStarted;

    public ConversationTurnEngine(
        IConversationRepository conversationRepository,
        DesktopTurnFinalizer turnFinalizer,
        ConversationTurnRecorder turnRecorder,
        IAgentChatRuntime agentChatRuntime,
        ConversationSessionCoordinator conversationSessions,
        AgentActivityCoordinator agentActivityCoordinator,
        DesktopToolApprovalHandler toolApprovalHandler,
        ProgrammingAssistantSettingsService programmingAssistantSettings,
        IAiProviderSettingsService aiProviderSettings,
        IConversationCompletionNotifier completionNotifier,
        ILogger<ConversationTurnEngine> logger)
    {
        _conversationRepository = conversationRepository;
        _turnFinalizer = turnFinalizer;
        _turnRecorder = turnRecorder;
        _agentChatRuntime = agentChatRuntime;
        _conversationSessions = conversationSessions;
        _agentActivityCoordinator = agentActivityCoordinator;
        _toolApprovalHandler = toolApprovalHandler;
        _programmingAssistantSettings = programmingAssistantSettings;
        _aiProviderSettings = aiProviderSettings;
        _completionNotifier = completionNotifier;
        _logger = logger;
    }

    internal async Task<AdmittedConversationTurn?> TryAdmitAsync(DesktopConversationTurnRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Interlocked.Increment(ref _pendingInteractiveAdmissions);
        try
        {
            await _turnAdmissionGate.WaitAsync();
            try
            {
                var conversation = CreateConversation(request);
                if (_deletingConversations.ContainsKey(conversation.Id) ||
                    _conversationSessions.IsRunning(conversation.Id))
                {
                    return null;
                }

                conversation = await _conversationRepository.UpsertConversationAsync(conversation);
                var runtimeState = await _conversationSessions.StartTurnAsync(conversation);
                return new AdmittedConversationTurn(request, conversation, runtimeState);
            }
            finally
            {
                _turnAdmissionGate.Release();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _pendingInteractiveAdmissions);
        }
    }

    internal async Task<ConversationRuntimeState?> TryAdmitContinuationAsync(
        ConversationRecord conversation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        if (conversation.Kind != ConversationKind.Interactive ||
            Volatile.Read(ref _pendingInteractiveAdmissions) != 0 ||
            _deletingConversations.ContainsKey(conversation.Id))
        {
            return null;
        }

        await _turnAdmissionGate.WaitAsync(cancellationToken);
        try
        {
            if (Volatile.Read(ref _pendingInteractiveAdmissions) != 0 ||
                _deletingConversations.ContainsKey(conversation.Id) ||
                _conversationSessions.IsRunning(conversation.Id))
            {
                return null;
            }

            return await _conversationSessions.StartDetachedTurnAsync(conversation, cancellationToken);
        }
        finally
        {
            _turnAdmissionGate.Release();
        }
    }

    internal async Task CompleteContinuationAsync(
        ConversationRuntimeState runtimeState,
        bool publishPersistedTurn,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtimeState);
        await _turnAdmissionGate.WaitAsync(cancellationToken);
        try
        {
            if (publishPersistedTurn)
            {
                _conversationSessions.CompleteTurn(runtimeState);
            }
            else
            {
                _conversationSessions.AbandonTurn(runtimeState);
            }
        }
        finally
        {
            _turnAdmissionGate.Release();
        }
    }

    internal void BeginConversationDeletion(Guid conversationId)
    {
        if (conversationId == Guid.Empty)
        {
            throw new ArgumentException("A conversation deletion requires a non-empty id.", nameof(conversationId));
        }

        _deletingConversations[conversationId] = 0;
    }

    internal void EndConversationDeletion(Guid conversationId)
        => _deletingConversations.TryRemove(conversationId, out _);

    internal async Task ExecuteAsync(AdmittedConversationTurn admission)
    {
        ArgumentNullException.ThrowIfNull(admission);

        var runtimeState = admission.RuntimeState;
        AgentTurnState? turnState = null;
        var activityStarted = false;
        try
        {
            await AddUserMessageAsync(admission);
            var turnId = Guid.NewGuid();
            turnState = new AgentTurnState(turnId, admission.Request.Agent);
            var chatRequest = await BuildChatTurnRequestAsync(
                admission,
                turnId,
                runtimeState.CancellationTokenSource.Token);
            BeginActivity(admission.Conversation, admission.Request.Agent, turnState);
            activityStarted = true;
            _turnRecorder.BeginTurn(runtimeState, turnState);
            await StreamTurnAsync(runtimeState, turnState, chatRequest);

            if (turnState.Completed)
            {
                _completionNotifier.Notify(admission.Conversation, runtimeState.Messages);
            }
        }
        catch (OperationCanceledException) when (runtimeState.CancellationTokenSource.IsCancellationRequested)
        {
            await FinalizeIfStartedAsync(
                runtimeState,
                turnState,
                TurnFinalizationKind.Cancelled,
                AgentActivityOutcome.Cancelled,
                "Generation stopped.");
        }
        catch (Exception exception)
        {
            LogTurnFailure(exception);
            await FinalizeIfStartedAsync(
                runtimeState,
                turnState,
                TurnFinalizationKind.Failed,
                AgentActivityOutcome.Failed,
                exception.Message);
        }
        finally
        {
            CompleteExecution(runtimeState, turnState, activityStarted);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        _turnAdmissionGate.Dispose();
    }

    private static ConversationRecord CreateConversation(DesktopConversationTurnRequest request)
    {
        var now = DateTimeOffset.UtcNow;
        var conversation = request.Conversation ?? new ConversationRecord(
            request.ConversationId ?? Guid.NewGuid(),
            "New chat",
            request.WorkspaceRoot?.Id,
            ConversationMode.Programming,
            request.ToolPermissionMode,
            request.Agent.Id,
            now,
            now);
        return conversation with
        {
            Title = conversation.Title == "New chat"
                ? CreateConversationTitle(request.Prompt)
                : conversation.Title,
            WorkspaceRootId = request.WorkspaceRoot?.Id,
            Mode = ConversationMode.Programming,
            ToolPermissionMode = request.ToolPermissionMode,
            UpdatedAtUtc = now
        };
    }

    private static string CreateConversationTitle(string text)
    {
        var normalized = text.ReplaceLineEndings(" ").Trim();
        return normalized.Length > 48 ? normalized[..48] + "..." : normalized;
    }

    private void LogTurnFailure(Exception exception)
    {
        if (exception is OperationCanceledException)
        {
            _logger.LogError(exception, "The chat runtime canceled without a user cancellation request.");
            return;
        }

        _logger.LogError(exception, "Chat turn failed.");
    }

    private void CompleteExecution(
        ConversationRuntimeState runtimeState,
        AgentTurnState? turnState,
        bool activityStarted)
    {
        _conversationSessions.CompleteTurn(runtimeState);
        if (activityStarted && turnState is not null && !turnState.Completed)
        {
            _agentActivityCoordinator.CompleteInterrupted(
                turnState.TurnId,
                AgentActivityOutcome.Failed,
                "Agent stream ended before the turn reached a terminal state.");
        }
    }

    private async Task AddUserMessageAsync(AdmittedConversationTurn admission)
    {
        var request = admission.Request;
        var now = DateTimeOffset.UtcNow;
        var userMessage = new MessageRecord(
            Guid.NewGuid(),
            admission.Conversation.Id,
            MessageRole.User,
            request.Prompt,
            MessageStatus.Completed,
            now,
            now);
        admission.RuntimeState.ReplaceMessage(userMessage);
        await _conversationRepository.UpsertMessageAsync(
            userMessage,
            admission.RuntimeState.CancellationTokenSource.Token);
        admission.RuntimeState.RaiseTranscriptChanged(false);
    }

    private void BeginActivity(
        ConversationRecord conversation,
        AgentRuntimeDefinition agent,
        AgentTurnState turn)
        => _agentActivityCoordinator.BeginTurn(new AgentActivityContext(
            turn.TurnId,
            conversation.Id,
            conversation.Title,
            agent.Id,
            agent.Name,
            agent.Mode,
            turn.StartedAtUtc));

    private async Task StreamTurnAsync(
        ConversationRuntimeState runtimeState,
        AgentTurnState turnState,
        ChatTurnRequest chatRequest)
    {
        var cancellationToken = runtimeState.CancellationTokenSource.Token;
        await foreach (var update in _agentChatRuntime.StreamTurnAsync(chatRequest, cancellationToken))
        {
            await _turnRecorder.ApplyEventAsync(
                runtimeState,
                turnState,
                update,
                _turnFinalizer,
                cancellationToken);
            _agentActivityCoordinator.ApplyEvent(turnState.TurnId, update);
        }
    }

    private async Task FinalizeInterruptedTurnAsync(
        ConversationRuntimeState runtimeState,
        AgentTurnState turnState,
        TurnFinalizationKind finalizationKind,
        AgentActivityOutcome activityOutcome,
        string message)
    {
        await _turnRecorder.FinalizeInterruptedAsync(
            runtimeState,
            turnState,
            finalizationKind,
            message,
            _turnFinalizer);
        _agentActivityCoordinator.CompleteInterrupted(turnState.TurnId, activityOutcome, message);
    }

    private async Task FinalizeIfStartedAsync(
        ConversationRuntimeState runtimeState,
        AgentTurnState? turnState,
        TurnFinalizationKind finalizationKind,
        AgentActivityOutcome activityOutcome,
        string message)
    {
        if (turnState is null)
        {
            return;
        }

        await FinalizeInterruptedTurnAsync(
            runtimeState,
            turnState,
            finalizationKind,
            activityOutcome,
            message);
    }

    private async Task<ChatTurnRequest> BuildChatTurnRequestAsync(
        AdmittedConversationTurn admission,
        Guid turnId,
        CancellationToken cancellationToken)
    {
        var request = admission.Request;
        var messages = admission.RuntimeState.Messages.ToArray();
        if (request.Agent.Mode == AgentExecutionMode.Cli)
        {
            var cliSelection = await _programmingAssistantSettings.GetSelectedInvocationAsync(cancellationToken);
            return new CliChatTurnRequest(
                turnId,
                admission.Conversation.Id,
                request.WorkspaceRoot,
                request.Agent,
                messages,
                cliSelection?.Kind,
                cliSelection?.Model,
                cliSelection?.ReasoningEffort);
        }

        var modelProfileId = request.ModelProfileId
            ?? await _aiProviderSettings.GetDefaultModelAsync(
                AiModelSelectionScopes.DesktopDefault,
                cancellationToken)
            ?? throw new InvalidOperationException(
                "No default Direct model is selected. Choose a default model in the AI provider settings.");
        return new DirectChatTurnRequest(
            turnId,
            admission.Conversation.Id,
            request.WorkspaceRoot,
            request.Agent,
            messages,
            modelProfileId,
            request.ToolPermissionMode,
            _toolApprovalHandler,
            new DirectTurnExecutionContext(DirectTurnOrigin.Interactive, null, null));
    }
}
