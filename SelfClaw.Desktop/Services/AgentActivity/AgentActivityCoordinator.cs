using Microsoft.Extensions.Logging;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;

namespace SelfClaw.Desktop.Services.AgentActivity;

public sealed class AgentActivityCoordinator : IDisposable
{
    private const int ApprovalPreviewLength = 140;
    private const int MaxRetainedTerminalTurns = 20;
    private readonly object _syncRoot = new();
    private readonly DesktopToolApprovalHandler _approvalHandler;
    private readonly ILogger<AgentActivityCoordinator> _logger;
    private readonly Dictionary<Guid, TurnActivity> _turns = [];
    private readonly List<ToolApprovalRequest> _approvals = [];
    private AgentActivitySnapshot _currentSnapshot;
    private Guid? _selectedConversationId;
    private bool _disposed;

    public AgentActivityCoordinator(
        DesktopToolApprovalHandler approvalHandler,
        ILogger<AgentActivityCoordinator> logger)
    {
        _approvalHandler = approvalHandler;
        _logger = logger;
        _currentSnapshot = CreateIdleSnapshot();
        _approvalHandler.ApprovalRequested += OnApprovalRequested;
        _approvalHandler.ApprovalCompleted += OnApprovalCompleted;
    }

    public event EventHandler<AgentActivitySnapshot>? SnapshotChanged;

    public AgentActivitySnapshot CurrentSnapshot
    {
        get
        {
            lock (_syncRoot)
            {
                return _currentSnapshot;
            }
        }
    }

    public void BeginTurn(AgentActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (_syncRoot)
        {
            ThrowIfDisposed();
            _turns[context.TurnId] = new TurnActivity(context);
        }

        PublishCurrentSnapshot();
    }

    public void ApplyEvent(Guid turnId, AgentStreamEvent streamEvent)
    {
        ArgumentNullException.ThrowIfNull(streamEvent);
        var changed = false;
        lock (_syncRoot)
        {
            ThrowIfDisposed();
            if (!_turns.TryGetValue(turnId, out var turn))
            {
                return;
            }

            changed = ApplyEvent(turn, streamEvent);
            if (changed)
            {
                PruneTerminalTurnsLocked();
            }
        }

        if (changed)
        {
            PublishCurrentSnapshot();
        }
    }

    public void CompleteInterrupted(
        Guid turnId,
        AgentActivityOutcome outcome,
        string? errorMessage)
    {
        var changed = false;
        lock (_syncRoot)
        {
            ThrowIfDisposed();
            if (!_turns.TryGetValue(turnId, out var turn) || !turn.IsActive)
            {
                return;
            }

            changed = CompleteTurn(turn, outcome, errorMessage);
            if (changed)
            {
                PruneTerminalTurnsLocked();
            }
        }

        if (changed)
        {
            PublishCurrentSnapshot();
        }
    }

    public void SetSelectedConversation(Guid? conversationId)
    {
        lock (_syncRoot)
        {
            ThrowIfDisposed();
            if (_selectedConversationId == conversationId)
            {
                return;
            }

            _selectedConversationId = conversationId;
        }

        PublishCurrentSnapshot();
    }

    public bool TryResolveApproval(Guid toolExecutionId, bool approved)
        => _approvalHandler.TryResolve(toolExecutionId, approved);

    private static bool ApplyEvent(TurnActivity turn, AgentStreamEvent streamEvent)
    {
        switch (streamEvent)
        {
            case RunStartedEvent started:
                return turn.SetPhase(
                    AgentActivityPhase.Initializing,
                    "正在启动 Agent",
                    BuildRunDetail(started),
                    toolKind: null);

            case RunStatusEvent status:
                var (phase, headline) = MapRunStatus(status.Status);
                return turn.SetPhase(
                    phase,
                    headline,
                    string.IsNullOrWhiteSpace(status.Detail) ? turn.Detail : status.Detail,
                    toolKind: null);

            case AssistantThinkingDeltaEvent:
                return turn.ActiveToolCalls.Count == 0 &&
                       turn.SetPhase(AgentActivityPhase.Thinking, "正在思考方案", null, toolKind: null);

            case AssistantTextDeltaEvent:
                return turn.ActiveToolCalls.Count == 0 &&
                       turn.SetPhase(AgentActivityPhase.Responding, "正在整理回复", null, toolKind: null);

            case ToolCallStartedEvent toolStarted:
                turn.ActiveToolCalls.Add(toolStarted.ToolCallId);
                return turn.SetPhase(
                    AgentActivityPhase.UsingTool,
                    MapToolHeadline(toolStarted.Kind, toolStarted.ToolName),
                    null,
                    toolStarted.Kind);

            case ToolCallCompletedEvent toolCompleted:
                if (!turn.ActiveToolCalls.Remove(toolCompleted.ToolCallId))
                {
                    return false;
                }

                return toolCompleted.Status == ToolCallStatus.Failed
                    ? turn.SetPhase(
                        AgentActivityPhase.UsingTool,
                        "工具执行失败，正在继续处理",
                        toolCompleted.ResultSummary,
                        turn.ToolKind)
                    : turn.ActiveToolCalls.Count == 0 && turn.SetPhase(
                        AgentActivityPhase.Responding,
                        "正在继续处理",
                        null,
                        toolKind: null);

            case RunCompletedEvent completed:
                // A truncated run did not error - it delivered a valid partial answer and
                // stopped at the configured cap. The message status carries the incomplete
                // signal; this transient activity outcome only tracks failures.
                return CompleteTurn(
                    turn,
                    completed.Status is RunCompletionStatus.Succeeded or RunCompletionStatus.Truncated
                        ? AgentActivityOutcome.Succeeded
                        : AgentActivityOutcome.Failed,
                    completed.ErrorMessage);

            default:
                return false;
        }
    }

    private static bool CompleteTurn(
        TurnActivity turn,
        AgentActivityOutcome outcome,
        string? errorMessage)
    {
        if (!turn.IsActive)
        {
            return false;
        }

        turn.IsActive = false;
        turn.ActiveToolCalls.Clear();
        return outcome switch
        {
            AgentActivityOutcome.Succeeded => turn.SetPhase(
                AgentActivityPhase.Succeeded,
                "任务完成",
                null,
                toolKind: null),
            AgentActivityOutcome.Cancelled => turn.SetPhase(
                AgentActivityPhase.Cancelled,
                "任务已停止",
                TrimDetail(errorMessage),
                toolKind: null),
            _ => turn.SetPhase(
                AgentActivityPhase.Failed,
                "任务失败",
                TrimDetail(errorMessage),
                toolKind: null),
        };
    }

    private static (AgentActivityPhase Phase, string Headline) MapRunStatus(AgentRunStatus status)
        => status switch
        {
            AgentRunStatus.Initializing => (AgentActivityPhase.Initializing, "正在初始化"),
            AgentRunStatus.Requesting => (AgentActivityPhase.Requesting, "正在连接模型"),
            AgentRunStatus.Thinking => (AgentActivityPhase.Thinking, "正在思考方案"),
            AgentRunStatus.Running => (AgentActivityPhase.Responding, "正在执行任务"),
            _ => (AgentActivityPhase.Starting, "正在准备任务"),
        };

    private static string MapToolHeadline(ToolCallKind kind, string toolName)
        => kind switch
        {
            ToolCallKind.Read or ToolCallKind.List or ToolCallKind.Search => "正在查看项目",
            ToolCallKind.Edit => "正在修改文件",
            ToolCallKind.Run => "正在运行命令",
            _ => string.IsNullOrWhiteSpace(toolName) ? "正在使用工具" : $"正在使用 {toolName.Trim()}",
        };

    private static string? BuildRunDetail(RunStartedEvent started)
    {
        var parts = new List<string>(2);
        if (started.AgentKind is not null)
        {
            parts.Add(started.AgentKind.Value.ToString());
        }

        if (!string.IsNullOrWhiteSpace(started.Model))
        {
            parts.Add(started.Model.Trim());
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    private void OnApprovalRequested(ToolApprovalRequest request)
    {
        try
        {
            lock (_syncRoot)
            {
                if (_disposed || _approvals.Any(item => item.ToolExecutionId == request.ToolExecutionId))
                {
                    return;
                }

                _approvals.Add(request);
            }

            PublishCurrentSnapshot();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to project tool approval {ToolExecutionId}.", request.ToolExecutionId);
        }
    }

    private void OnApprovalCompleted(Guid toolExecutionId)
    {
        try
        {
            var removed = false;
            lock (_syncRoot)
            {
                if (_disposed)
                {
                    return;
                }

                removed = _approvals.RemoveAll(item => item.ToolExecutionId == toolExecutionId) > 0;
            }

            if (removed)
            {
                PublishCurrentSnapshot();
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to complete tool approval projection {ToolExecutionId}.", toolExecutionId);
        }
    }

    private void PublishCurrentSnapshot()
    {
        AgentActivitySnapshot? snapshot;
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            var next = BuildSnapshotLocked();
            if (next with { UpdatedAtUtc = _currentSnapshot.UpdatedAtUtc } == _currentSnapshot)
            {
                return;
            }

            _currentSnapshot = next;
            snapshot = next;
        }

        RaiseSnapshotChanged(snapshot);
    }

    private AgentActivitySnapshot BuildSnapshotLocked()
    {
        var activeTurnCount = _turns.Values.Count(turn => turn.IsActive);
        if (_approvals.Count > 0)
        {
            var approval = _approvals[0];
            var turn = FindTurnForConversationLocked(approval.ConversationId);
            return CreateSnapshot(
                turn,
                AgentActivityPhase.AwaitingApproval,
                $"需要批准：{approval.DisplayName}",
                BuildApprovalPreview(approval),
                ToolKindForApproval(approval.ToolName),
                approval,
                activeTurnCount);
        }

        var selectedTurn = _selectedConversationId is Guid selectedConversationId
            ? _turns.Values
                .Where(turn => turn.IsActive && turn.Context.ConversationId == selectedConversationId)
                .OrderByDescending(turn => turn.UpdatedAtUtc)
                .FirstOrDefault()
            : null;
        var currentTurn = selectedTurn
            ?? _turns.Values.Where(turn => turn.IsActive).OrderByDescending(turn => turn.UpdatedAtUtc).FirstOrDefault()
            ?? _turns.Values.Where(turn => !turn.IsActive).OrderByDescending(turn => turn.UpdatedAtUtc).FirstOrDefault();

        return currentTurn is null
            ? CreateIdleSnapshot()
            : CreateSnapshot(
                currentTurn,
                currentTurn.Phase,
                currentTurn.Headline,
                currentTurn.Detail,
                currentTurn.ToolKind,
                approval: null,
                activeTurnCount);
    }

    private TurnActivity? FindTurnForConversationLocked(Guid? conversationId)
        => conversationId is Guid id
            ? _turns.Values
                .Where(turn => turn.Context.ConversationId == id)
                .OrderByDescending(turn => turn.UpdatedAtUtc)
                .FirstOrDefault()
            : null;

    private void PruneTerminalTurnsLocked()
    {
        var staleTurnIds = _turns.Values
            .Where(turn => !turn.IsActive)
            .OrderByDescending(turn => turn.UpdatedAtUtc)
            .Skip(MaxRetainedTerminalTurns)
            .Select(turn => turn.Context.TurnId)
            .ToArray();
        foreach (var turnId in staleTurnIds)
        {
            _turns.Remove(turnId);
        }
    }

    private AgentActivitySnapshot CreateSnapshot(
        TurnActivity? turn,
        AgentActivityPhase phase,
        string headline,
        string? detail,
        ToolCallKind? toolKind,
        ToolApprovalRequest? approval,
        int activeTurnCount)
        => new(
            turn?.Context.TurnId,
            turn?.Context.ConversationId ?? approval?.ConversationId,
            turn?.Context.ConversationTitle,
            turn?.Context.AgentId,
            turn?.Context.AgentName,
            turn?.Context.ExecutionMode,
            phase,
            headline,
            detail,
            toolKind,
            approval,
            _approvals.Count,
            activeTurnCount,
            DateTimeOffset.UtcNow);

    private static AgentActivitySnapshot CreateIdleSnapshot()
        => new(
            TurnId: null,
            ConversationId: null,
            ConversationTitle: null,
            AgentId: null,
            AgentName: null,
            ExecutionMode: null,
            AgentActivityPhase.Idle,
            "Ready.",
            Detail: null,
            ToolKind: null,
            Approval: null,
            PendingApprovalCount: 0,
            ActiveTurnCount: 0,
            DateTimeOffset.UtcNow);

    private static string? BuildApprovalPreview(ToolApprovalRequest request)
    {
        var value = string.IsNullOrWhiteSpace(request.ArgumentsJson)
            ? request.Description
            : request.ArgumentsJson;
        var source = string.IsNullOrWhiteSpace(request.SourceId) ? null : $"{request.SourceKind}: {request.SourceId}";
        return TrimDetail(string.Join(" · ", new[] { source, value?.ReplaceLineEndings(" ") }
            .Where(item => !string.IsNullOrWhiteSpace(item))));
    }

    private static ToolCallKind ToolKindForApproval(string toolName)
        => toolName.Trim().ToLowerInvariant() switch
        {
            "write_file" => ToolCallKind.Edit,
            "run_shell_command" => ToolCallKind.Run,
            _ => ToolCallKind.Other,
        };

    private static string? TrimDetail(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return null;
        }

        var value = detail.Trim();
        return value.Length <= ApprovalPreviewLength
            ? value
            : $"{value[..ApprovalPreviewLength]}…";
    }

    private void RaiseSnapshotChanged(AgentActivitySnapshot snapshot)
    {
        var subscribers = SnapshotChanged;
        if (subscribers is null)
        {
            return;
        }

        foreach (EventHandler<AgentActivitySnapshot> subscriber in subscribers.GetInvocationList())
        {
            try
            {
                subscriber(this, snapshot);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Agent activity snapshot subscriber failed.");
            }
        }
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _turns.Clear();
            _approvals.Clear();
        }

        _approvalHandler.ApprovalRequested -= OnApprovalRequested;
        _approvalHandler.ApprovalCompleted -= OnApprovalCompleted;
    }

    private sealed class TurnActivity
    {
        public TurnActivity(AgentActivityContext context)
        {
            Context = context;
            UpdatedAtUtc = context.StartedAtUtc;
        }

        public AgentActivityContext Context { get; }

        public AgentActivityPhase Phase { get; private set; } = AgentActivityPhase.Starting;

        public string Headline { get; private set; } = "正在接收任务";

        public string? Detail { get; private set; }

        public ToolCallKind? ToolKind { get; private set; }

        public DateTimeOffset UpdatedAtUtc { get; private set; }

        public bool IsActive { get; set; } = true;

        public HashSet<string> ActiveToolCalls { get; } = new(StringComparer.Ordinal);

        public bool SetPhase(
            AgentActivityPhase phase,
            string headline,
            string? detail,
            ToolCallKind? toolKind)
        {
            if (Phase == phase &&
                string.Equals(Headline, headline, StringComparison.Ordinal) &&
                string.Equals(Detail, detail, StringComparison.Ordinal) &&
                ToolKind == toolKind)
            {
                return false;
            }

            Phase = phase;
            Headline = headline;
            Detail = detail;
            ToolKind = toolKind;
            UpdatedAtUtc = DateTimeOffset.UtcNow;
            return true;
        }
    }
}
