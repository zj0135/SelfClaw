using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Desktop.Services;
using SelfClaw.Desktop.Services.AgentActivity;
using SelfClaw.Desktop.Services.ProgrammingAssistant;
using SelfClaw.Desktop.Services.ProgrammingAssistant.Models;
using SelfClaw.Desktop.Services.Runtime;
using SelfClaw.Desktop.Services.Transcript;
using SelfClaw.Desktop.Services.Workspace.Abstractions;

namespace SelfClaw.Desktop.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject, IDisposable, IWorkspaceSelectionController
{
    #region 字段与构造函数 —— 依赖注入字段、运行时集合状态、流式发布定时器初始化

    private static readonly TimeSpan StreamingPublishInterval = TimeSpan.FromMilliseconds(75);
    private static readonly TimeSpan ConversationDeleteStopTimeout = TimeSpan.FromSeconds(8);
    private readonly IConversationRepository _conversationRepository;
    private readonly IAgentChatRuntime _agentChatRuntime;
    private readonly ConversationTurnEngine _turnEngine;
    private readonly ConversationSessionCoordinator _conversationSessions;
    private readonly AgentActivityCoordinator _agentActivityCoordinator;
    private readonly DesktopToolApprovalHandler _toolApprovalHandler;
    private readonly DesktopNotificationService _desktopNotificationService;
    private readonly TranscriptProjection _transcriptProjection;
    private readonly DesktopAgentDefinitionService _desktopAgentDefinitionService;
    private readonly ProgrammingAssistantSettingsService _programmingAssistantSettings;
    private readonly DesktopSettingsJsonStore _settingsStore;
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly DispatcherTimer? _streamingPublishTimer;
    private readonly SemaphoreSlim _turnAdmissionGate = new(1, 1);

    private readonly List<ConversationRecord> _allConversations = [];
    private readonly List<ConversationRecord> _filteredConversations = [];
    private readonly List<DesktopAgentDefinition> _agents = [];
    private readonly List<WorkspaceRoot> _workspaceRoots = [];
    private Task _selectionLoadTask = Task.CompletedTask;
    private bool _initialized;
    private int _selectionVersion;
    private ConversationRecord? _selectedConversation;
    private WorkspaceRoot? _selectedWorkspaceRoot;
    private string _selectedAgentId = DesktopAgentDefinitionService.BuildAgentId;
    private ToolPermissionMode _selectedToolPermissionMode = ToolPermissionMode.RequireApproval;
    private Guid? _selectedModelProfileId;
    // Composer-level execution mode override ("本地 CLI" / "提供商"). Null defers to the
    // active agent's own mode; a value forces every sent turn onto that runtime branch.
    private AgentExecutionMode? _composerModeOverride;
    private bool _pendingStreamingPublish;
    private bool _pendingStreamingAutoScroll;
    private DateTimeOffset _lastStreamingPublishAtUtc = DateTimeOffset.MinValue;
    private int _disposeStarted;
    private long _capabilityRevision;

    public MainWindowViewModel(
        IConversationRepository conversationRepository,
        IAgentChatRuntime agentChatRuntime,
        ConversationTurnEngine turnEngine,
        ConversationSessionCoordinator conversationSessions,
        AgentActivityCoordinator agentActivityCoordinator,
        DesktopToolApprovalHandler toolApprovalHandler,
        DesktopNotificationService desktopNotificationService,
        TranscriptProjection transcriptProjection,
        DesktopAgentDefinitionService desktopAgentDefinitionService,
        ProgrammingAssistantSettingsService programmingAssistantSettings,
        DesktopSettingsJsonStore settingsStore,
        ILogger<MainWindowViewModel> logger)
    {
        _conversationRepository = conversationRepository;
        _agentChatRuntime = agentChatRuntime;
        _turnEngine = turnEngine;
        _conversationSessions = conversationSessions;
        _agentActivityCoordinator = agentActivityCoordinator;
        _toolApprovalHandler = toolApprovalHandler;
        _desktopNotificationService = desktopNotificationService;
        _transcriptProjection = transcriptProjection;
        _desktopAgentDefinitionService = desktopAgentDefinitionService;
        _programmingAssistantSettings = programmingAssistantSettings;
        _settingsStore = settingsStore;
        _logger = logger;
        _conversationSessions.SelectedTranscriptChanged += OnSelectedTranscriptChanged;
        if (System.Windows.Application.Current?.Dispatcher is Dispatcher dispatcher)
        {
            _streamingPublishTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
            {
                Interval = StreamingPublishInterval
            };
            _streamingPublishTimer.Tick += OnStreamingPublishTimerTick;
        }
    }

    #endregion

    #region 公开入口与属性 —— 暴露给宿主窗口 / WebView 的事件、选中会话属性与发送入口

    /// 渲染输出：每次 transcript（消息 / 工具运行 / 会话列表）变化时携带完整快照触发，
    /// 由宿主窗口推送给 Vue 前端。属于发送→渲染主路径的出口。
    public event EventHandler<TranscriptRenderState>? TranscriptChanged;

    /// <summary>
    /// 当前选中工作区根目录路径。供宿主窗口解析终端工作目录使用。
    /// </summary>
    public string? SelectedWorkspaceRootPath => _selectedWorkspaceRoot?.RootPath;

    public WorkspaceRoot? SelectedWorkspaceRoot => _selectedWorkspaceRoot;

    public string SelectedAgentId => _selectedAgentId;

    public long CapabilityRevision => _capabilityRevision;

    public IReadOnlyList<WorkspaceRoot> WorkspaceRoots => _workspaceRoots.ToArray();

    public void UpdateCapabilityRevision(long revision)
    {
        if (revision <= _capabilityRevision)
        {
            return;
        }

        _capabilityRevision = revision;
        _transcriptProjection.Invalidate();
        PublishShell(false);
    }

    public ConversationRecord? SelectedConversation => _selectedConversation;

    /// <summary>
    /// 启动时一次性加载：代理、工作区、会话列表，并发布初始 transcript。
    /// 由宿主窗口（OnLoaded）与通知激活服务调用，是渲染路径的引导入口。
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        _composerModeOverride = ParseComposerMode(
            (await _settingsStore.ReadNodeAsync<ComposerSettings>(ComposerSettingsNode))?.ExecutionMode);
        ReloadAgents();
        await ReloadWorkspaceRootsAsync();
        await ReloadConversationsAsync();
        await _selectionLoadTask;

        PublishShell(false);
    }

    /// <summary>
    /// 前端唯一保留的入口：WebView 的 "send-prompt" 消息最终落到这里，触发一次发送回合。
    /// 其余前端交互（窗口、终端、面板、设置）都在宿主窗口内处理，不经过 VM。
    /// </summary>
    public Task SubmitPromptAsync(string prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        var normalizedPrompt = prompt.Trim();
        if (normalizedPrompt.Length == 0)
        {
            return Task.CompletedTask;
        }

        return SendAsync(new PromptSubmissionSnapshot(
            normalizedPrompt,
            SelectedConversation,
            _selectedWorkspaceRoot,
            _selectedToolPermissionMode,
            _selectedModelProfileId,
            _selectedAgentId,
            _composerModeOverride,
            _selectionVersion));
    }

    /// <summary>
    /// Cancels the selected conversation's running turn (WebView "stop-generation" / Esc).
    /// The cancellation flows through <c>SendAsync</c>'s OperationCanceledException path,
    /// which finalizes the active turn as cancelled.
    /// </summary>
    public void StopSelectedConversation()
        => _conversationSessions.StopSelected();

    public void SelectModelProfile(Guid? modelProfileId)
    {
        _selectedModelProfileId = modelProfileId;
    }

    /// <summary>
    /// Applies the composer's mode pick ("cli" / "direct") as a persisted override on top of the
    /// active agent's own mode, so provider models can be exercised without a Direct-mode agent.
    /// Unknown values clear the override and fall back to the agent definition.
    /// </summary>
    public async Task SelectComposerModeAsync(string? mode)
    {
        var parsed = ParseComposerMode(mode);
        if (_composerModeOverride == parsed)
        {
            return;
        }

        _composerModeOverride = parsed;
        try
        {
            await _settingsStore.WriteNodeAsync(
                ComposerSettingsNode,
                new ComposerSettings(parsed?.ToString().ToLowerInvariant()));
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to persist the composer execution mode.");
        }

        PublishShell(false);
    }

    private AgentExecutionMode ResolveComposerExecutionMode(AgentExecutionMode agentMode)
        => _composerModeOverride ?? agentMode;

    private static AgentExecutionMode? ParseComposerMode(string? mode)
        => mode?.Trim().ToLowerInvariant() switch
        {
            "cli" => AgentExecutionMode.Cli,
            "direct" => AgentExecutionMode.Direct,
            _ => null
        };

    private const string ComposerSettingsNode = "composer";

    public Task StartNewConversationAsync()
        => SelectConversationCoreAsync(null);

    public Task SelectConversationAsync(Guid conversationId)
    {
        var conversation = _filteredConversations.FirstOrDefault(item => item.Id == conversationId)
            ?? _allConversations.FirstOrDefault(item => item.Id == conversationId);
        if (conversation is null)
        {
            throw new InvalidOperationException("The selected conversation no longer exists.");
        }

        return SelectConversationCoreAsync(conversation);
    }

    public Task DeleteConversationAsync(Guid conversationId)
        => DeleteConversationsAsync([conversationId]);

    public async Task DeleteConversationsAsync(IEnumerable<Guid> conversationIds)
    {
        var requestedIds = conversationIds
            .Where(item => item != Guid.Empty)
            .Distinct()
            .ToHashSet();
        if (requestedIds.Count == 0)
        {
            return;
        }

        var deleteIds = _allConversations
            .Where(item => requestedIds.Contains(item.Id))
            .Select(item => item.Id)
            .ToArray();
        if (deleteIds.Length == 0)
        {
            return;
        }

        foreach (var conversationId in deleteIds)
        {
            await StopConversationForDeletionAsync(conversationId);
        }

        foreach (var conversationId in deleteIds)
        {
            await _conversationRepository.DeleteConversationAsync(conversationId);
            _allConversations.RemoveAll(item => item.Id == conversationId);
            _filteredConversations.RemoveAll(item => item.Id == conversationId);
        }

        var deletedIdSet = deleteIds.ToHashSet();
        var preferredConversationId = SelectedConversation?.Id is Guid selectedId && !deletedIdSet.Contains(selectedId)
            ? (Guid?)selectedId
            : null;
        ApplyConversationFilter(preferredConversationId);
    }

    public async Task DeleteWorkspaceRootAsync(Guid workspaceRootId)
    {
        if (workspaceRootId == Guid.Empty)
        {
            return;
        }

        await _conversationRepository.DeleteWorkspaceRootAsync(workspaceRootId);
        await ReloadWorkspaceRootsAsync();
        await ReloadConversationsAsync();
    }

    #endregion

    #region 工作区选择 —— 切换当前工作区

    private void SelectWorkspaceRoot(WorkspaceRoot? workspaceRoot, bool publishShell)
    {
        if (_selectedWorkspaceRoot?.Id == workspaceRoot?.Id)
        {
            return;
        }

        _selectedWorkspaceRoot = workspaceRoot;
        OnPropertyChanged(nameof(SelectedWorkspaceRootPath));
        if (publishShell)
        {
            PublishShell(false);
        }
    }

    public async Task ReloadWorkspaceSelectionAsync()
    {
        await ReloadWorkspaceRootsAsync();
        ApplyWorkspaceSelectionChanged(SelectedConversation?.Id);
    }

    public void SelectWorkspaceRoot(Guid? workspaceRootId)
    {
        var workspaceRoot = workspaceRootId is Guid id
            ? _workspaceRoots.FirstOrDefault(root => root.Id == id)
            : null;

        if (workspaceRootId is not null && workspaceRoot is null)
        {
            throw new InvalidOperationException("The selected workspace root no longer exists.");
        }

        SelectWorkspaceRoot(workspaceRoot, publishShell: false);
        ApplyWorkspaceSelectionChanged();
    }

    public async Task<WorkspaceRoot> SelectOrAddWorkspaceRootAsync(string rootPath)
    {
        var normalizedPath = NormalizeWorkspaceRootPath(rootPath);
        if (!Directory.Exists(normalizedPath))
        {
            throw new DirectoryNotFoundException($"The selected workspace directory does not exist: {normalizedPath}");
        }

        var existing = _workspaceRoots.FirstOrDefault(root => WorkspacePathsEqual(root.RootPath, normalizedPath));
        if (existing is null)
        {
            var now = DateTimeOffset.UtcNow;
            existing = new WorkspaceRoot(
                Guid.NewGuid(),
                ResolveWorkspaceRootName(normalizedPath),
                normalizedPath,
                now,
                now);
            await _conversationRepository.UpsertWorkspaceRootAsync(existing);
            await ReloadWorkspaceRootsAsync();
            existing = _workspaceRoots.FirstOrDefault(root => WorkspacePathsEqual(root.RootPath, normalizedPath)) ?? existing;
        }

        SelectWorkspaceRoot(existing, publishShell: false);
        ApplyWorkspaceSelectionChanged();
        return existing;
    }

    private void ApplyWorkspaceSelectionChanged(Guid? preferredConversationId = null)
    {
        if (_agents.Count == 0)
        {
            return;
        }

        ApplyConversationFilter(preferredConversationId);
    }

    private static string NormalizeWorkspaceRootPath(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new ArgumentException("A workspace directory path is required.", nameof(rootPath));
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath.Trim()));
    }

    private static bool WorkspacePathsEqual(string left, string right)
        => string.Equals(
            NormalizeWorkspaceRootPath(left),
            NormalizeWorkspaceRootPath(right),
            StringComparison.OrdinalIgnoreCase);

    private static string ResolveWorkspaceRootName(string rootPath)
    {
        var name = Path.GetFileName(NormalizeWorkspaceRootPath(rootPath));
        return string.IsNullOrWhiteSpace(name) ? NormalizeWorkspaceRootPath(rootPath) : name;
    }

    #endregion

    #region 流式发布调度 —— 通过定时器节流向前端推送 transcript 快照（节流 / 立即 / 定时回调）

    private void RequestStreamingShellPublish(bool autoScroll)
    {
        if (System.Windows.Application.Current?.Dispatcher is Dispatcher dispatcher && !dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(new Action(() => RequestStreamingShellPublish(autoScroll)), DispatcherPriority.Background);
            return;
        }

        if (_streamingPublishTimer is null)
        {
            PublishShell(autoScroll);
            return;
        }

        _pendingStreamingPublish = true;
        _pendingStreamingAutoScroll |= autoScroll;

        var elapsed = DateTimeOffset.UtcNow - _lastStreamingPublishAtUtc;
        if (!_streamingPublishTimer.IsEnabled && elapsed >= StreamingPublishInterval)
        {
            FlushStreamingShellPublish();
            return;
        }

        if (_streamingPublishTimer.IsEnabled)
        {
            return;
        }

        var delay = elapsed >= StreamingPublishInterval
            ? StreamingPublishInterval
            : StreamingPublishInterval - elapsed;
        if (delay < TimeSpan.Zero)
        {
            delay = TimeSpan.Zero;
        }

        _streamingPublishTimer.Interval = delay;
        _streamingPublishTimer.Start();
    }

    private void FlushStreamingShellPublish()
    {
        if (_streamingPublishTimer?.IsEnabled == true)
        {
            _streamingPublishTimer.Stop();
        }

        if (!_pendingStreamingPublish)
        {
            return;
        }

        var autoScroll = _pendingStreamingAutoScroll;
        _pendingStreamingPublish = false;
        _pendingStreamingAutoScroll = false;
        _lastStreamingPublishAtUtc = DateTimeOffset.UtcNow;
        PublishShell(autoScroll);
    }

    private void PublishShellNow(bool autoScroll)
    {
        if (System.Windows.Application.Current?.Dispatcher is Dispatcher dispatcher && !dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(new Action(() => PublishShellNow(autoScroll)), DispatcherPriority.Background);
            return;
        }

        if (_pendingStreamingPublish)
        {
            _pendingStreamingAutoScroll |= autoScroll;
            FlushStreamingShellPublish();
            return;
        }

        PublishShell(autoScroll);
    }

    private void OnStreamingPublishTimerTick(object? sender, EventArgs e)
        => FlushStreamingShellPublish();

    #endregion

    #region 数据加载 —— 重载工作区 / 会话列表，并加载选中会话的消息与工具运行

    private async Task ReloadWorkspaceRootsAsync()
    {
        var selectedId = _selectedWorkspaceRoot?.Id;
        var workspaceRoots = await _conversationRepository.ListWorkspaceRootsAsync();
        ReplaceList(_workspaceRoots, workspaceRoots);
        SelectWorkspaceRoot(
            selectedId is Guid id
                ? workspaceRoots.FirstOrDefault(root => root.Id == id)
                : null,
            publishShell: false);
    }

    private async Task ReloadConversationsAsync()
    {
        var selectedId = SelectedConversation?.Id;
        var conversations = await _conversationRepository.ListConversationsAsync();
        _allConversations.Clear();
        _allConversations.AddRange(conversations);
        ApplyConversationFilter(selectedId);
    }

    private async Task StopConversationForDeletionAsync(Guid conversationId)
        => await _conversationSessions.StopAndRemoveAsync(
            conversationId,
            ConversationDeleteStopTimeout);

    private Task SelectConversationCoreAsync(ConversationRecord? conversation)
    {
        if (_selectedConversation?.Id == conversation?.Id)
        {
            SetProperty(ref _selectedConversation, conversation, nameof(SelectedConversation));
            return _selectionLoadTask;
        }

        SetProperty(ref _selectedConversation, conversation, nameof(SelectedConversation));
        _agentActivityCoordinator.SetSelectedConversation(conversation?.Id);
        var version = ++_selectionVersion;
        _selectionLoadTask = LoadSelectedConversationAsync(conversation, version);
        PublishShell(false);
        return _selectionLoadTask;
    }

    private async Task LoadSelectedConversationAsync(ConversationRecord? conversation, int version)
    {
        try
        {
            await _conversationSessions.SelectAsync(conversation?.Id);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to load conversation {ConversationId}.", conversation?.Id);
            return;
        }

        if (version != _selectionVersion || _selectedConversation?.Id != conversation?.Id)
        {
            return;
        }

        if (conversation is not null)
        {
            SelectWorkspaceRoot(
                conversation.WorkspaceRootId is Guid workspaceRootId
                    ? _workspaceRoots.FirstOrDefault(root => root.Id == workspaceRootId)
                    : null,
                publishShell: false);
            _selectedToolPermissionMode = conversation.ToolPermissionMode;
            SyncSelectedAgentFromConversation(conversation);
        }

        ReplaceList(_filteredConversations, GetFilteredConversations());
        PublishShell(false);
    }

    #endregion

    #region 发送回合与释放 —— 组织一次发送（建会话→落用户消息→流式回合），以及 VM 释放

    private async Task SendAsync(PromptSubmissionSnapshot submission)
    {
        ConversationRuntimeState? runtimeState = null;
        AgentTurnState? turnState = null;
        var activityStarted = false;

        try
        {
            var preparation = await TryStartTurnAsync(submission);
            if (preparation is null)
            {
                return;
            }

            var (conversation, runtimeAgent, startedRuntimeState) = preparation.Value;
            runtimeState = startedRuntimeState;
            var preferConversationSelection = SelectedConversation is null ||
                                              _conversationSessions.IsSelected(conversation.Id);

            var cancellationToken = runtimeState.CancellationTokenSource.Token;
            var userMessageId = Guid.NewGuid();
            var userMessage = new MessageRecord(
                userMessageId,
                conversation.Id,
                MessageRole.User,
                submission.Prompt,
                MessageStatus.Completed,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow);
            runtimeState.ReplaceMessage(userMessage);
            await _conversationRepository.UpsertMessageAsync(userMessage);

            if (conversation.Title == "New chat")
            {
                conversation = conversation with
                {
                    Title = CreateConversationTitle(submission.Prompt),
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                };
                runtimeState.Conversation = conversation;
                await PersistConversationAsync(conversation, preferSelection: preferConversationSelection);
            }
            runtimeState.RaiseTranscriptChanged(false);

            var requestMessages = runtimeState.Messages.ToArray();
            turnState = new AgentTurnState(runtimeAgent);
            _agentActivityCoordinator.BeginTurn(new AgentActivityContext(
                turnState.AssistantMessageId,
                conversation.Id,
                conversation.Title,
                runtimeAgent.Id,
                runtimeAgent.Name,
                runtimeAgent.Mode,
                turnState.StartedAtUtc));
            activityStarted = true;

            // Build only the mode's own request shape: a Direct turn never resolves the CLI selection, and a
            // CLI turn never carries the provider model / approval fields. The composer's resolved mode decides.
            var request = await BuildChatTurnRequestAsync(
                runtimeAgent,
                conversation.Id,
                submission.ModelProfileId,
                submission.WorkspaceRoot,
                submission.ToolPermissionMode,
                requestMessages,
                cancellationToken);

            // Surface the assistant placeholder immediately: CLI process startup can take seconds
            // before the first stream event (RunStarted) arrives, and the transcript would
            // otherwise show nothing but the user message.
            _turnEngine.BeginAssistantMessage(runtimeState, turnState);

            await foreach (var update in _agentChatRuntime.StreamTurnAsync(request, cancellationToken))
            {
                await _turnEngine.ApplyEventAsync(runtimeState, turnState, update, cancellationToken);
                _agentActivityCoordinator.ApplyEvent(turnState.AssistantMessageId, update);
            }

            if (turnState.Completed)
            {
                PublishConversationCompletedNotification(conversation, runtimeState.Messages);
            }
        }
        catch (OperationCanceledException) when (runtimeState?.CancellationTokenSource.IsCancellationRequested == true)
        {
            if (runtimeState is not null && turnState is not null)
            {
                await _turnEngine.FinalizeInterruptedAsync(
                    runtimeState,
                    turnState,
                    TurnFinalizationKind.Cancelled,
                    "Generation stopped.");
                _agentActivityCoordinator.CompleteInterrupted(
                    turnState.AssistantMessageId,
                    AgentActivityOutcome.Cancelled,
                    "Generation stopped.");
            }
        }
        catch (OperationCanceledException exception)
        {
            _logger.LogError(exception, "The chat runtime canceled without a user cancellation request.");
            if (runtimeState is not null && turnState is not null)
            {
                await _turnEngine.FinalizeInterruptedAsync(
                    runtimeState,
                    turnState,
                    TurnFinalizationKind.Failed,
                    exception.Message);
                _agentActivityCoordinator.CompleteInterrupted(
                    turnState.AssistantMessageId,
                    AgentActivityOutcome.Failed,
                    exception.Message);
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Chat turn failed.");
            if (runtimeState is null)
            {
                return;
            }

            if (turnState is not null)
            {
                await _turnEngine.FinalizeInterruptedAsync(
                    runtimeState,
                    turnState,
                    TurnFinalizationKind.Failed,
                    exception.Message);
                _agentActivityCoordinator.CompleteInterrupted(
                    turnState.AssistantMessageId,
                    AgentActivityOutcome.Failed,
                    exception.Message);
            }
        }
        finally
        {
            if (runtimeState is not null)
            {
                _conversationSessions.CompleteTurn(runtimeState);
            }

            if (activityStarted && turnState is not null && !turnState.Completed)
            {
                _agentActivityCoordinator.CompleteInterrupted(
                    turnState.AssistantMessageId,
                    AgentActivityOutcome.Failed,
                    "Agent stream ended before the turn reached a terminal state.");
            }
        }
    }

    private async Task<(
        ConversationRecord Conversation,
        AgentRuntimeDefinition Agent,
        ConversationRuntimeState RuntimeState)?> TryStartTurnAsync(PromptSubmissionSnapshot submission)
    {
        await _turnAdmissionGate.WaitAsync();
        try
        {
            if (submission.SelectionVersion != _selectionVersion)
            {
                return null;
            }

            var conversation = submission.Conversation ?? CreateConversationRecord(submission);
            var preferConversationSelection = SelectedConversation is null ||
                                               _conversationSessions.IsSelected(conversation.Id);
            if (_conversationSessions.IsRunning(conversation.Id))
            {
                return null;
            }

            conversation = conversation with
            {
                WorkspaceRootId = submission.WorkspaceRoot?.Id,
                Mode = ConversationMode.Programming,
                ToolPermissionMode = submission.ToolPermissionMode,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            await PersistConversationAsync(conversation, preferSelection: preferConversationSelection);

            var runtimeAgent = ResolveRuntimeAgent(conversation.AgentId);
            runtimeAgent = runtimeAgent with { Mode = submission.ExecutionModeOverride ?? runtimeAgent.Mode };
            var runtimeState = await _conversationSessions.StartTurnAsync(conversation);
            return (conversation, runtimeAgent, runtimeState);
        }
        finally
        {
            _turnAdmissionGate.Release();
        }
    }

    /// <summary>
    /// Builds the request shape for the resolved mode: a Direct turn carries the model profile, permission mode
    /// and approval handler; a CLI turn resolves the selected CLI / model / reasoning effort and carries none of
    /// the provider fields. Only the CLI branch reads <see cref="ProgrammingAssistantSettingsService"/>.
    /// </summary>
    private async Task<ChatTurnRequest> BuildChatTurnRequestAsync(
        AgentRuntimeDefinition runtimeAgent,
        Guid conversationId,
        Guid? selectedModelProfileId,
        WorkspaceRoot? selectedWorkspaceRoot,
        ToolPermissionMode selectedToolPermissionMode,
        IReadOnlyList<MessageRecord> requestMessages,
        CancellationToken cancellationToken)
    {
        if (runtimeAgent.Mode == AgentExecutionMode.Cli)
        {
            // A null selection means nothing is selected (or detected); the CLI runtime fails the turn with
            // actionable guidance. Model / effort are null when the user left the CLI's own default.
            var cliSelection = await _programmingAssistantSettings.GetSelectedInvocationAsync(cancellationToken);
            return new CliChatTurnRequest(
                conversationId,
                selectedWorkspaceRoot,
                runtimeAgent,
                requestMessages,
                cliSelection?.Kind,
                cliSelection?.Model,
                cliSelection?.ReasoningEffort);
        }

        return new DirectChatTurnRequest(
            conversationId,
            selectedWorkspaceRoot,
            runtimeAgent,
            requestMessages,
            selectedModelProfileId,
            selectedToolPermissionMode,
            _toolApprovalHandler);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        if (_streamingPublishTimer is not null)
        {
            _streamingPublishTimer.Stop();
            _streamingPublishTimer.Tick -= OnStreamingPublishTimerTick;
        }

        _conversationSessions.SelectedTranscriptChanged -= OnSelectedTranscriptChanged;
        _conversationSessions.Dispose();
        _turnAdmissionGate.Dispose();
    }

    #endregion

    #region 会话创建 / 持久化 / 过滤 —— 新建会话记录、落盘并同步选中、维护会话列表与可见性过滤

    private static ConversationRecord CreateConversationRecord(PromptSubmissionSnapshot submission)
    {
        var now = DateTimeOffset.UtcNow;
        return new ConversationRecord(
            Guid.NewGuid(),
            "New chat",
            submission.WorkspaceRoot?.Id,
            ConversationMode.Programming,
            submission.ToolPermissionMode,
            submission.AgentId,
            now,
            now);
    }

    private async Task PersistConversationAsync(ConversationRecord conversation, bool preferSelection = true)
    {
        await _conversationRepository.UpsertConversationAsync(conversation);
        var shouldPreferSelection = preferSelection &&
                                    (SelectedConversation is null || SelectedConversation.Id == conversation.Id);
        UpsertConversation(conversation, shouldPreferSelection);
        if (shouldPreferSelection || SelectedConversation?.Id == conversation.Id)
        {
            _selectedConversation = conversation;
            _agentActivityCoordinator.SetSelectedConversation(conversation.Id);
            OnPropertyChanged(nameof(SelectedConversation));
        }

        PublishShell(false);
    }

    private void UpsertConversation(ConversationRecord conversation, bool preferSelection = true)
    {
        var existing = _allConversations.FirstOrDefault(item => item.Id == conversation.Id);
        if (existing is not null)
        {
            _allConversations.Remove(existing);
        }

        _allConversations.Insert(0, conversation);
        ApplyConversationFilter(preferSelection ? conversation.Id : SelectedConversation?.Id);
    }

    private void ApplyConversationFilter(Guid? preferredConversationId = null)
    {
        var filtered = GetFilteredConversations().ToArray();
        ReplaceList(_filteredConversations, filtered);

        var targetConversation = filtered.FirstOrDefault(item => item.Id == preferredConversationId)
            ?? filtered.FirstOrDefault(item => item.Id == SelectedConversation?.Id);

        if (SelectedConversation?.Id == targetConversation?.Id)
        {
            PublishShell(false);
            return;
        }

        _ = SelectConversationCoreAsync(targetConversation);
    }

    private IEnumerable<ConversationRecord> GetFilteredConversations()
        => _allConversations.Where(MatchesConversationFilter);

    private bool MatchesConversationFilter(ConversationRecord conversation)
    {
        if (conversation.Mode != ConversationMode.Programming)
        {
            return false;
        }

        if (!string.Equals(conversation.AgentId, ResolveSelectedAgent().Id, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return _selectedWorkspaceRoot is null
            ? conversation.WorkspaceRootId is null
            : conversation.WorkspaceRootId == _selectedWorkspaceRoot.Id;
    }

    private static void ReplaceList<T>(List<T> target, IEnumerable<T> source)
    {
        target.Clear();
        target.AddRange(source);
    }

    #endregion

    #region Transcript 渲染构建 —— 计算快照指纹去重后，把消息 / 工具运行 / 附件构建成前端渲染项并触发 TranscriptChanged

    private void PublishShell(bool autoScroll)
    {
        var selectedAgent = ResolveSelectedAgent();
        var isBusy = _conversationSessions.IsSelectedRunning;
        var activityText = isBusy ? _conversationSessions.SelectedActivityText : null;
        var state = _transcriptProjection.Build(new TranscriptProjectionRequest(
            _conversationSessions.SelectedMessages,
            _conversationSessions.SelectedToolRuns,
            _conversationSessions.SelectedToolRunAnchors,
            GetNavigationConversations().ToArray(),
            _workspaceRoots,
            SelectedConversation?.Id,
            autoScroll,
            isBusy,
            activityText,
            ResolveComposerExecutionMode(selectedAgent.Mode).ToString().ToLowerInvariant(),
            selectedAgent.Id,
            selectedAgent.Name,
            _capabilityRevision));
        if (state is null)
        {
            return;
        }

        TranscriptChanged?.Invoke(this, state);
    }

    private IEnumerable<ConversationRecord> GetNavigationConversations()
        => _allConversations.Where(MatchesNavigationConversation);

    private bool MatchesNavigationConversation(ConversationRecord conversation)
        => conversation.Mode == ConversationMode.Programming &&
           string.Equals(conversation.AgentId, ResolveSelectedAgent().Id, StringComparison.OrdinalIgnoreCase);

    private static string CreateConversationTitle(string text)
    {
        var normalized = text.ReplaceLineEndings(" ").Trim();
        return normalized.Length > 48 ? normalized[..48] + "..." : normalized;
    }

    #endregion

}






