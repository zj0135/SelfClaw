using System.Collections.Immutable;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Desktop.Services.Runtime;
using SelfClaw.Desktop.Services.Subagents.Models;

namespace SelfClaw.Desktop.Services.Subagents;

internal sealed class SubagentExecutionSession : IAsyncDisposable
{
    private readonly SubagentTaskRecord _task;
    private readonly ConversationRuntimeState _state;
    private readonly AgentTurnState _turn;
    private readonly ConversationTurnRecorder _recorder;
    private readonly SubagentChildTurnCommitter _committer;
    private readonly ISubagentStateChangeNotifier? _changes;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _lifetimeLock = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private SubagentExecutionActivity _activity = new(0, "initializing", null, null, null, false, null);
    private string _phase = "initializing";
    private string? _model;
    private string? _recordingError;
    private bool _immediate;
    private bool _closing;
    private int _users;
    private Task? _disposal;

    internal SubagentExecutionSession(SubagentTaskRecord task, SubagentExecutionInput input,
        ConversationTurnRecorder recorder, ISubagentTaskExecutionStore store, TimeProvider timeProvider,
        ISubagentStateChangeNotifier? changes)
    {
        _task = task;
        _state = new ConversationRuntimeState(input.Conversation, input.Turns, input.Messages, input.ToolRuns);
        _turn = CreateTurn(task, _state);
        _recorder = recorder;
        _committer = new SubagentChildTurnCommitter(store, task.Id, timeProvider);
        _changes = changes;
        InitialMessages = input.Messages.ToImmutableArray();
        InitialTurns = input.Turns.ToImmutableArray();
        InitialToolRuns = input.ToolRuns.ToImmutableArray();
        _state.TranscriptChanged += OnTranscriptChanged;
    }

    internal Guid TaskId => _task.Id;
    internal Guid ParentConversationId => _task.ParentConversationId;
    internal Guid ChildConversationId => _task.ChildConversationId;
    internal IReadOnlyList<MessageRecord> InitialMessages { get; }
    internal IReadOnlyList<ConversationTurnRecord> InitialTurns { get; }
    internal IReadOnlyList<ToolExecutionRecord> InitialToolRuns { get; }
    internal SubagentExecutionActivity Activity => Volatile.Read(ref _activity);

    internal Task BeginAsync(CancellationToken cancellationToken = default)
        => MutateAsync(() =>
        {
            _recorder.BeginTurn(_state, _turn);
            return Task.CompletedTask;
        }, immediate: true, cancellationToken);

    internal Task ApplyEventAsync(AgentStreamEvent streamEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(streamEvent);
        return MutateAsync(() => ApplyCoreAsync(streamEvent, cancellationToken),
            streamEvent is ToolCallStartedEvent or ToolCallCompletedEvent or RunCompletedEvent or UsageReportedEvent,
            cancellationToken);
    }

    internal Task FinalizeAsync(SubagentTaskStatus status, string errorCode, string errorMessage)
        => MutateAsync(async () =>
        {
            if (_turn.Completed)
            {
                return;
            }

            _committer.OverrideTerminal(status, errorCode, errorMessage);
            var kind = status switch
            {
                SubagentTaskStatus.Cancelled => TurnFinalizationKind.Cancelled,
                SubagentTaskStatus.Interrupted => TurnFinalizationKind.Interrupted,
                _ => TurnFinalizationKind.Failed
            };
            await _recorder.FinalizeInterruptedAsync(_state, _turn, kind,
                errorMessage, _committer).ConfigureAwait(false);
        }, immediate: true, CancellationToken.None);

    internal async Task<SubagentExecutionSnapshot?> CaptureSnapshotAsync(CancellationToken cancellationToken = default)
    {
        if (!await EnterAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        try
        {
            var turn = _state.Turns.Single(item => item.Id == _task.ChildTurnId);
            turn = turn with { Usage = _turn.Usage.Build() ?? turn.Usage };
            var message = _state.Messages.SingleOrDefault(item => item.TurnId == turn.Id && item.Role == MessageRole.Assistant);
            if (message is not null)
            {
                message = message with
                {
                    Segments = message.Segments?.ToImmutableArray(),
                    Attachments = message.Attachments?.ToImmutableArray()
                };
            }

            return new SubagentExecutionSnapshot(_task.Id, _task.ParentConversationId, turn, message,
                _state.ToolRuns.Where(tool => tool.MessageId == message?.Id).ToImmutableArray(), Activity);
        }
        finally
        {
            Exit();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifetimeLock)
        {
            _closing = true;
            if (_users == 0)
            {
                _drained.TrySetResult();
            }

            return new ValueTask(_disposal ??= DisposeCoreAsync());
        }
    }

    private async Task MutateAsync(Func<Task> mutation, bool immediate, CancellationToken cancellationToken)
    {
        if (!await EnterAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new ObjectDisposedException(nameof(SubagentExecutionSession));
        }

        try
        {
            await mutation().ConfigureAwait(false);
            if (_turn.Completed)
            {
                _recordingError = null;
            }
        }
        catch (Exception exception)
        {
            if (_turn.PendingFinalization is not null)
            {
                _recordingError = exception.Message.Length <= 2048 ? exception.Message : exception.Message[..2048];
            }

            throw;
        }
        finally
        {
            var usage = _turn.Usage.Build();
            Volatile.Write(ref _activity, new SubagentExecutionActivity(_activity.Revision + 1, _phase, _model,
                usage?.InputTokens, usage?.OutputTokens, _turn.Completed, _recordingError));
            var publishImmediately = immediate || _immediate;
            _immediate = false;
            Exit();
            _changes?.Publish(_task.ParentConversationId, _task.Id, publishImmediately
                ? SubagentStateChangeKind.ExecutionBoundary : SubagentStateChangeKind.ExecutionContent);
        }
    }

    private async Task ApplyCoreAsync(AgentStreamEvent streamEvent, CancellationToken cancellationToken)
    {
        if (_turn.Completed)
        {
            return;
        }

        UpdatePhase(streamEvent);
        await _recorder.ApplyEventAsync(_state, _turn, streamEvent, _committer, cancellationToken).ConfigureAwait(false);
        if (streamEvent is ToolCallCompletedEvent &&
            _state.ToolRuns.Any(tool => tool.Status is ToolExecutionStatus.Running or ToolExecutionStatus.AwaitingApproval))
        {
            _phase = "tool";
        }
    }

    private void UpdatePhase(AgentStreamEvent streamEvent)
    {
        if (streamEvent is RunStartedEvent started)
        {
            _model = started.Model;
        }

        _phase = streamEvent switch
        {
            RunStatusEvent { Status: AgentRunStatus.Initializing } => "initializing",
            RunStatusEvent { Status: AgentRunStatus.Thinking } => "thinking",
            RunStatusEvent or RunStartedEvent => "requesting",
            AssistantThinkingDeltaEvent => "thinking",
            AssistantTextDeltaEvent => "responding",
            ToolCallStartedEvent => "tool",
            ToolCallCompletedEvent => "requesting",
            RunCompletedEvent => "settling",
            _ => _phase
        };
    }

    private void OnTranscriptChanged(bool immediate) => _immediate |= immediate;

    private async Task<bool> EnterAsync(CancellationToken cancellationToken)
    {
        lock (_lifetimeLock)
        {
            if (_closing)
            {
                return false;
            }

            _users++;
        }

        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            ReleaseUser();
            throw;
        }
    }

    private void Exit()
    {
        _gate.Release();
        ReleaseUser();
    }

    private void ReleaseUser()
    {
        lock (_lifetimeLock)
        {
            if (--_users == 0 && _closing)
            {
                _drained.TrySetResult();
            }
        }
    }

    private async Task DisposeCoreAsync()
    {
        await _drained.Task.ConfigureAwait(false);
        _state.TranscriptChanged -= OnTranscriptChanged;
        _gate.Dispose();
    }

    private static AgentTurnState CreateTurn(SubagentTaskRecord task, ConversationRuntimeState state)
    {
        var agent = new AgentRuntimeDefinition(task.SubagentId, task.SubagentName, string.Empty, AgentExecutionMode.Direct,
            AgentRuntimeDefinition.ReadOnlyToolPolicy, [], [], [], [], string.Empty);
        var recordedTurn = state.Turns.Single(turn => turn.Id == task.ChildTurnId);
        var message = state.Messages.SingleOrDefault(message => message.TurnId == task.ChildTurnId && message.Role == MessageRole.Assistant);
        var turn = new AgentTurnState(recordedTurn, agent)
        {
            CurrentAssistantMessageId = message?.Id
        };
        foreach (var tool in state.ToolRuns.Where(tool => tool.MessageId == message?.Id))
        {
            turn.ToolRunsByCallId[tool.CorrelationId ?? tool.Id.ToString("D")] = tool;
        }

        return turn;
    }
}
