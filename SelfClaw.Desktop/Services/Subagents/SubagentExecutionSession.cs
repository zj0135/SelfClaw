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
        _state = new ConversationRuntimeState(input.Conversation, input.Messages, input.ToolRuns);
        _turn = CreateTurn(task, _state);
        _recorder = recorder;
        _committer = new SubagentChildTurnCommitter(store, task.Id, timeProvider);
        _changes = changes;
        ProviderMessages = input.Messages.Where(message => message.Id != task.ChildTurnId).ToImmutableArray();
        InitialToolRuns = input.ToolRuns.ToImmutableArray();
        _state.TranscriptChanged += OnTranscriptChanged;
    }

    internal Guid TaskId => _task.Id;
    internal Guid ParentConversationId => _task.ParentConversationId;
    internal Guid ChildConversationId => _task.ChildConversationId;
    internal IReadOnlyList<MessageRecord> ProviderMessages { get; }
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
            await _recorder.FinalizeInterruptedAsync(_state, _turn,
                status == SubagentTaskStatus.Cancelled ? TurnFinalizationKind.Cancelled : TurnFinalizationKind.Failed,
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
            var message = _state.Messages.FirstOrDefault(item => item.Id == _task.ChildTurnId);
            if (message is not null)
            {
                message = message with
                {
                    Segments = message.Segments?.ToImmutableArray(),
                    Attachments = message.Attachments?.ToImmutableArray(),
                    InputTokens = _turn.InputTokens,
                    OutputTokens = _turn.OutputTokens
                };
            }

            return new SubagentExecutionSnapshot(_task.Id, _task.ParentConversationId, message,
                _state.ToolRuns.Where(tool => tool.MessageId == _task.ChildTurnId).ToImmutableArray(), Activity);
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
            Volatile.Write(ref _activity, new SubagentExecutionActivity(_activity.Revision + 1, _phase, _model,
                _turn.InputTokens, _turn.OutputTokens, _turn.Completed, _recordingError));
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
        if (streamEvent is RunCompletedEvent { Status: RunCompletionStatus.Truncated } truncated)
        {
            var error = string.IsNullOrWhiteSpace(truncated.ErrorMessage)
                ? "The Subagent reached the model output limit. Partial output was preserved."
                : truncated.ErrorMessage;
            _committer.OverrideTerminal(SubagentTaskStatus.Failed, SubagentErrorCodes.OutputTruncated, error);
            streamEvent = truncated with { Status = RunCompletionStatus.Failed, ErrorMessage = error };
        }

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
        _state.Dispose();
        _gate.Dispose();
    }

    private static AgentTurnState CreateTurn(SubagentTaskRecord task, ConversationRuntimeState state)
    {
        var agent = new AgentRuntimeDefinition(task.SubagentId, task.SubagentName, string.Empty, AgentExecutionMode.Direct,
            "read-only", [], [], [], [], string.Empty);
        var turn = new AgentTurnState(task.ChildTurnId, agent)
        {
            MessageCreated = state.Messages.Any(message => message.Id == task.ChildTurnId)
        };
        foreach (var tool in state.ToolRuns.Where(tool => tool.MessageId == task.ChildTurnId))
        {
            turn.ToolRunsByCallId[tool.CorrelationId ?? tool.Id.ToString("D")] = tool;
        }

        return turn;
    }
}
