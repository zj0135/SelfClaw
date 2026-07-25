using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Desktop.Services.AgentActivity;

namespace SelfClaw.Desktop.Pet;

public sealed class PetActivityPresenter : IDisposable
{
    private static readonly TimeSpan ActiveBubbleDuration = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan SucceededBubbleDuration = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan FailedBubbleDuration = TimeSpan.FromSeconds(8);
    private readonly AgentActivityCoordinator _activityCoordinator;
    private PetBubbleViewState _current;
    private bool _disposed;

    public PetActivityPresenter(AgentActivityCoordinator activityCoordinator)
    {
        _activityCoordinator = activityCoordinator;
        _current = BuildState(activityCoordinator.CurrentSnapshot);
        _activityCoordinator.SnapshotChanged += OnSnapshotChanged;
    }

    public event EventHandler<PetBubbleViewState>? StateChanged;

    public event EventHandler<Guid>? ConversationActivationRequested;

    public PetBubbleViewState Current => _current;

    public bool TryResolveApproval(Guid toolExecutionId, bool approved)
        => _activityCoordinator.TryResolveApproval(toolExecutionId, approved);

    public void RequestConversationActivation(Guid conversationId)
        => ConversationActivationRequested?.Invoke(this, conversationId);

    private void OnSnapshotChanged(object? sender, AgentActivitySnapshot snapshot)
    {
        _current = BuildState(snapshot);
        StateChanged?.Invoke(this, _current);
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
            snapshot.Detail,
            IsVisible: !isIdle,
            IsPinned: isApproval,
            snapshot.Approval?.ToolExecutionId,
            Math.Max(0, snapshot.PendingApprovalCount - 1),
            ResolveAutoHideAfter(snapshot.Phase),
            ResolveWorkState(snapshot));
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _activityCoordinator.SnapshotChanged -= OnSnapshotChanged;
    }
}
