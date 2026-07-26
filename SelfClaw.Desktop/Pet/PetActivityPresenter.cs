using Microsoft.Extensions.Logging;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Desktop.Services.AgentActivity;

namespace SelfClaw.Desktop.Pet;

public sealed class PetActivityPresenter : IDisposable
{
    private static readonly TimeSpan ActiveBubbleDuration = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan SucceededBubbleDuration = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan FailedBubbleDuration = TimeSpan.FromSeconds(8);

    private readonly object _syncRoot = new();
    private readonly AgentActivityCoordinator _activityCoordinator;
    private readonly IPetPresentationScheduler _scheduler;
    private readonly ILogger<PetActivityPresenter> _logger;
    private PetBubbleViewState _current;
    private PetBubbleViewState _latestActivityState;
    private TimeSpan? _latestAutoHideAfter;
    private long _stateVersion;
    private bool _disposed;

    public PetActivityPresenter(
        AgentActivityCoordinator activityCoordinator,
        ILogger<PetActivityPresenter> logger)
        : this(activityCoordinator, new DispatcherPetPresentationScheduler(), logger)
    {
    }

    internal PetActivityPresenter(
        AgentActivityCoordinator activityCoordinator,
        IPetPresentationScheduler scheduler,
        ILogger<PetActivityPresenter> logger)
    {
        ArgumentNullException.ThrowIfNull(activityCoordinator);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(logger);

        _activityCoordinator = activityCoordinator;
        _scheduler = scheduler;
        _logger = logger;
        _current = BuildState(activityCoordinator.CurrentSnapshot);
        _latestActivityState = _current;
        _latestAutoHideAfter = ResolveAutoHideAfter(activityCoordinator.CurrentSnapshot.Phase);
        _activityCoordinator.SnapshotChanged += OnSnapshotChanged;
    }

    public event EventHandler<PetBubbleViewState>? StateChanged;

    public event EventHandler<Guid>? ConversationActivationRequested;

    public PetBubbleViewState Current
    {
        get
        {
            lock (_syncRoot)
            {
                return _current;
            }
        }
    }

    public void ToggleBubble()
    {
        PetBubbleViewState next;
        TimeSpan? autoHideAfter;
        lock (_syncRoot)
        {
            ThrowIfDisposed();
            if (_current.IsPinned)
            {
                return;
            }

            if (_current.IsVisible)
            {
                next = HideState(_current);
                autoHideAfter = null;
            }
            else
            {
                next = _latestActivityState with
                {
                    IsVisible = true,
                    WorkState = IsTerminal(_latestActivityState.WorkState)
                        ? PetWorkState.None
                        : _latestActivityState.WorkState,
                };
                autoHideAfter = _latestAutoHideAfter ?? ActiveBubbleDuration;
            }
        }

        Publish(next, autoHideAfter);
    }

    public void DismissBubble()
    {
        PetBubbleViewState next;
        lock (_syncRoot)
        {
            ThrowIfDisposed();
            if (_current.IsPinned || !_current.IsVisible)
            {
                return;
            }

            next = HideState(_current);
        }

        Publish(next, autoHideAfter: null);
    }

    public bool TryResolveCurrentApproval(bool approved)
    {
        Guid? approvalId;
        lock (_syncRoot)
        {
            ThrowIfDisposed();
            approvalId = _current.ApprovalId;
        }

        return approvalId is Guid toolExecutionId &&
               _activityCoordinator.TryResolveApproval(toolExecutionId, approved);
    }

    public bool RequestCurrentConversationActivation()
    {
        Guid? conversationId;
        lock (_syncRoot)
        {
            ThrowIfDisposed();
            conversationId = _current.ConversationId;
        }

        if (conversationId is not Guid id)
        {
            return false;
        }

        RaiseConversationActivationRequested(id);
        return true;
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stateVersion++;
        }

        _activityCoordinator.SnapshotChanged -= OnSnapshotChanged;
        _scheduler.Dispose();
    }

    private void OnSnapshotChanged(object? sender, AgentActivitySnapshot snapshot)
    {
        var state = BuildState(snapshot);
        TimeSpan? autoHideAfter;
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _latestActivityState = state;
            autoHideAfter = ResolveAutoHideAfter(snapshot.Phase);
            _latestAutoHideAfter = autoHideAfter;
        }

        Publish(state, autoHideAfter);
    }

    private void Publish(PetBubbleViewState state, TimeSpan? autoHideAfter)
    {
        long version;
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _current = state;
            version = ++_stateVersion;
            _scheduler.Cancel();
            if (state.IsVisible && !state.IsPinned && autoHideAfter is TimeSpan delay)
            {
                _scheduler.Schedule(delay, () => AutoHide(version));
            }
        }

        RaiseStateChanged(state);
    }

    private void AutoHide(long version)
    {
        PetBubbleViewState hidden;
        lock (_syncRoot)
        {
            if (_disposed || version != _stateVersion || _current.IsPinned || !_current.IsVisible)
            {
                return;
            }

            hidden = HideState(_current);
            _current = hidden;
            _stateVersion++;
        }

        RaiseStateChanged(hidden);
    }

    private static PetBubbleViewState BuildState(AgentActivitySnapshot snapshot)
    {
        var isIdle = snapshot.Phase == AgentActivityPhase.Idle;
        var isApproval = snapshot.Phase == AgentActivityPhase.AwaitingApproval;
        var modeLabel = snapshot.ExecutionMode switch
        {
            AgentExecutionMode.Direct => "Direct",
            AgentExecutionMode.Cli => "CLI",
            _ => null,
        };
        var agentLabel = string.IsNullOrWhiteSpace(snapshot.AgentName)
            ? "SelfClaw"
            : modeLabel is null
                ? snapshot.AgentName
                : $"{snapshot.AgentName} · {modeLabel}";

        return new PetBubbleViewState(
            snapshot.ConversationId,
            agentLabel,
            snapshot.Headline,
            BuildDetail(snapshot.Detail, snapshot.PendingApprovalCount),
            IsVisible: !isIdle,
            IsPinned: isApproval,
            snapshot.Approval?.ToolExecutionId,
            ResolveWorkState(snapshot));
    }

    private static string? BuildDetail(string? detail, int pendingApprovalCount)
    {
        var additionalApprovalCount = Math.Max(0, pendingApprovalCount - 1);
        if (additionalApprovalCount == 0)
        {
            return detail;
        }

        return string.IsNullOrWhiteSpace(detail)
            ? $"还有 {additionalApprovalCount} 个请求"
            : $"{detail} · 还有 {additionalApprovalCount} 个请求";
    }

    private static TimeSpan? ResolveAutoHideAfter(AgentActivityPhase phase)
        => phase switch
        {
            AgentActivityPhase.Idle or AgentActivityPhase.AwaitingApproval => null,
            AgentActivityPhase.Succeeded => SucceededBubbleDuration,
            AgentActivityPhase.Failed => FailedBubbleDuration,
            AgentActivityPhase.Cancelled => SucceededBubbleDuration,
            _ => ActiveBubbleDuration,
        };

    private static PetWorkState ResolveWorkState(AgentActivitySnapshot snapshot)
        => snapshot.Phase switch
        {
            AgentActivityPhase.Idle => PetWorkState.None,
            AgentActivityPhase.AwaitingApproval => PetWorkState.AwaitingApproval,
            AgentActivityPhase.Succeeded => PetWorkState.Succeeded,
            AgentActivityPhase.Failed => PetWorkState.Failed,
            AgentActivityPhase.Cancelled => PetWorkState.Cancelled,
            AgentActivityPhase.UsingTool when snapshot.ToolKind is ToolCallKind.Read
                or ToolCallKind.List
                or ToolCallKind.Search => PetWorkState.Reviewing,
            AgentActivityPhase.UsingTool when snapshot.ToolKind is ToolCallKind.Edit
                or ToolCallKind.Run => PetWorkState.Running,
            _ => PetWorkState.Working,
        };

    private static PetBubbleViewState HideState(PetBubbleViewState state)
        => state with
        {
            IsVisible = false,
            WorkState = IsTerminal(state.WorkState) ? PetWorkState.None : state.WorkState,
        };

    private static bool IsTerminal(PetWorkState workState)
        => workState is PetWorkState.Succeeded or PetWorkState.Failed or PetWorkState.Cancelled;

    private void RaiseStateChanged(PetBubbleViewState state)
    {
        var subscribers = StateChanged;
        if (subscribers is null)
        {
            return;
        }

        foreach (EventHandler<PetBubbleViewState> subscriber in subscribers.GetInvocationList())
        {
            try
            {
                subscriber(this, state);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Pet presentation subscriber failed.");
            }
        }
    }

    private void RaiseConversationActivationRequested(Guid conversationId)
    {
        var subscribers = ConversationActivationRequested;
        if (subscribers is null)
        {
            return;
        }

        foreach (EventHandler<Guid> subscriber in subscribers.GetInvocationList())
        {
            try
            {
                subscriber(this, conversationId);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Pet conversation activation subscriber failed.");
            }
        }
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);

}
