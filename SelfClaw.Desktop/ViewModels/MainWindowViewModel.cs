using System.IO;
using System.Net;
using System.Text;
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
using SelfClaw.Infrastructure.Options;
using SelfClaw.Infrastructure.Tools.Transcript;
using SelfClaw.Infrastructure.Tools.Transcript.Models;

namespace SelfClaw.Desktop.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    #region 字段与构造函数 —— 依赖注入字段、运行时集合状态、流式发布定时器初始化

    private static readonly TimeSpan StreamingPublishInterval = TimeSpan.FromMilliseconds(75);
    private static readonly TimeSpan ConversationDeleteStopTimeout = TimeSpan.FromSeconds(8);
    private const int MaxPromptImageAttachments = 6;
    private const long MaxPromptImageBytes = 10 * 1024 * 1024;
    private const long MaxPromptImageTotalBytes = 30 * 1024 * 1024;
    private const string AttachmentHostName = "attachments.selfclaw.local";
    private readonly IConversationRepository _conversationRepository;
    private readonly IAgentChatRuntime _agentChatRuntime;
    private readonly ConversationTurnEngine _turnEngine;
    private readonly AgentActivityCoordinator _agentActivityCoordinator;
    private readonly DesktopToolApprovalHandler _toolApprovalHandler;
    private readonly DesktopNotificationService _desktopNotificationService;
    private readonly MarkdownHtmlRenderer _markdownHtmlRenderer;
    private readonly DesktopAgentStore _desktopAgentStore;
    private readonly ProgrammingAssistantSettingsService _programmingAssistantSettings;
    private readonly DesktopSettingsJsonStore _settingsStore;
    private readonly StoragePaths _storagePaths;
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly DispatcherTimer? _streamingPublishTimer;

    private readonly List<ConversationRecord> _allConversations = [];
    private readonly List<ConversationRecord> _filteredConversations = [];
    private readonly List<DesktopAgentDefinition> _agents = [];
    private readonly List<WorkspaceRoot> _workspaceRoots = [];
    private readonly List<MessageRecord> _messages = [];
    private readonly List<ToolExecutionRecord> _toolRuns = [];
    private readonly Dictionary<Guid, ToolRunAnchor> _toolRunAnchors = [];
    private readonly Dictionary<Guid, ConversationRuntimeState> _conversationRuntimeStates = [];
    private IReadOnlyList<PromptImageAttachment> _pendingPromptImageAttachments = [];
    private bool _initialized;
    private int _selectionVersion;
    private ConversationRecord? _selectedConversation;
    private WorkspaceRoot? _selectedWorkspaceRoot;
    private string _selectedAgentId = DesktopAgentStore.BuildAgentId;
    private string _composerText = string.Empty;
    private ToolPermissionMode _selectedToolPermissionMode = ToolPermissionMode.RequireApproval;
    private Guid? _selectedModelProfileId;
    // Composer-level execution mode override ("本地 CLI" / "提供商"). Null defers to the
    // active agent's own mode; a value forces every sent turn onto that runtime branch.
    private AgentExecutionMode? _composerModeOverride;
    private bool _pendingStreamingPublish;
    private bool _pendingStreamingAutoScroll;
    private string? _lastPublishedShellFingerprint;
    private DateTimeOffset _lastStreamingPublishAtUtc = DateTimeOffset.MinValue;
    private int _disposeStarted;

    public MainWindowViewModel(
        IConversationRepository conversationRepository,
        IAgentChatRuntime agentChatRuntime,
        ConversationTurnEngine turnEngine,
        AgentActivityCoordinator agentActivityCoordinator,
        DesktopToolApprovalHandler toolApprovalHandler,
        DesktopNotificationService desktopNotificationService,
        MarkdownHtmlRenderer markdownHtmlRenderer,
        DesktopAgentStore desktopAgentStore,
        ProgrammingAssistantSettingsService programmingAssistantSettings,
        DesktopSettingsJsonStore settingsStore,
        StoragePaths storagePaths,
        ILogger<MainWindowViewModel> logger)
    {
        _conversationRepository = conversationRepository;
        _agentChatRuntime = agentChatRuntime;
        _turnEngine = turnEngine;
        _agentActivityCoordinator = agentActivityCoordinator;
        _toolApprovalHandler = toolApprovalHandler;
        _desktopNotificationService = desktopNotificationService;
        _markdownHtmlRenderer = markdownHtmlRenderer;
        _desktopAgentStore = desktopAgentStore;
        _programmingAssistantSettings = programmingAssistantSettings;
        _settingsStore = settingsStore;
        _storagePaths = storagePaths;
        _logger = logger;
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

    /// <summary>
    /// 渲染输出：每次 transcript（消息 / 工具运行 / 会话列表）变化时携带完整快照触发，
    /// 由宿主窗口推送给 Vue 前端。属于发送→渲染主路径的出口。
    /// </summary>
    public event EventHandler<TranscriptRenderState>? TranscriptChanged;

    /// <summary>
    /// 当前选中工作区根目录路径。供宿主窗口解析终端工作目录使用。
    /// </summary>
    public string? SelectedWorkspaceRootPath => _selectedWorkspaceRoot?.RootPath;

    public WorkspaceRoot? SelectedWorkspaceRoot => _selectedWorkspaceRoot;

    public IReadOnlyList<WorkspaceRoot> WorkspaceRoots => _workspaceRoots.ToArray();

    public ConversationRecord? SelectedConversation
    {
        get => _selectedConversation;
        set
        {
            if (!SetProperty(ref _selectedConversation, value))
            {
                return;
            }

            _agentActivityCoordinator.SetSelectedConversation(value?.Id);

            if (value is not null)
            {
                PublishShell(false);
                _ = LoadConversationAsync(value);
            }
            else
            {
                _selectionVersion++;
                _messages.Clear();
                _toolRuns.Clear();
                _toolRunAnchors.Clear();
                PublishShell(false);
            }
        }
    }

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

        PublishShell(false);
    }

    /// <summary>
    /// 前端唯一保留的入口：WebView 的 "send-prompt" 消息最终落到这里，触发一次发送回合。
    /// 其余前端交互（窗口、终端、面板、设置）都在宿主窗口内处理，不经过 VM。
    /// </summary>
    public async Task SubmitPromptAsync(
        string prompt,
        IReadOnlyList<PromptImageAttachment>? imageAttachments = null)
    {
        _composerText = prompt;
        _pendingPromptImageAttachments = imageAttachments ?? [];
        await SendAsync();
    }

    /// <summary>
    /// Cancels the selected conversation's running turn (WebView "stop-generation" / Esc).
    /// The cancellation flows through <c>SendAsync</c>'s OperationCanceledException path,
    /// which finalizes the active turn as cancelled.
    /// </summary>
    public void StopSelectedConversation()
    {
        var runtimeState = GetSelectedRuntimeState();
        if (runtimeState?.IsRunning != true)
        {
            return;
        }

        try
        {
            runtimeState.CancellationTokenSource.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The turn finished between the IsRunning check and Cancel — nothing to stop.
        }
    }

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

    private sealed record ComposerSettings(string? ExecutionMode);

    public void StartNewConversation()
        => SelectedConversation = null;

    public void SelectConversation(Guid conversationId)
    {
        var conversation = _filteredConversations.FirstOrDefault(item => item.Id == conversationId)
            ?? _allConversations.FirstOrDefault(item => item.Id == conversationId);
        if (conversation is null)
        {
            throw new InvalidOperationException("The selected conversation no longer exists.");
        }

        SelectedConversation = conversation;
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
    {
        if (!_conversationRuntimeStates.TryGetValue(conversationId, out var runtimeState))
        {
            return;
        }

        if (runtimeState.IsRunning)
        {
            try
            {
                runtimeState.CancellationTokenSource.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The turn completed between lookup and cancellation.
            }

            var completedTask = await Task.WhenAny(
                runtimeState.Completion,
                Task.Delay(ConversationDeleteStopTimeout));
            if (completedTask != runtimeState.Completion && runtimeState.IsRunning)
            {
                throw new TimeoutException("The conversation is still running and cannot be deleted yet.");
            }
        }

        runtimeState.Dispose();
        _conversationRuntimeStates.Remove(conversationId);
    }

    private async Task LoadConversationAsync(ConversationRecord conversation)
    {
        var version = ++_selectionVersion;
        var messagesTask = _conversationRepository.ListMessagesAsync(conversation.Id);
        var toolRunsTask = _conversationRepository.ListToolExecutionsAsync(conversation.Id);

        var messages = await messagesTask;
        var toolRuns = await toolRunsTask;
        if (version != _selectionVersion)
        {
            return;
        }

        var runtimeState = _conversationRuntimeStates.TryGetValue(conversation.Id, out var runningState)
            ? runningState
            : null;
        if (runtimeState is not null)
        {
            messages = runtimeState.Messages.ToArray();
            toolRuns = runtimeState.ToolRuns.ToArray();
        }

        _messages.Clear();
        _messages.AddRange(messages);
        _toolRuns.Clear();
        _toolRuns.AddRange(toolRuns);
        _toolRunAnchors.Clear();
        if (runtimeState is not null)
        {
            foreach (var item in runtimeState.ToolRunAnchors)
            {
                _toolRunAnchors[item.Key] = item.Value;
            }
        }
        else
        {
            foreach (var toolRun in toolRuns)
            {
                if (toolRun.MessageId is Guid messageId && toolRun.AfterSegmentIndex is int afterSegmentIndex)
                {
                    _toolRunAnchors[toolRun.Id] = new ToolRunAnchor(messageId, afterSegmentIndex);
                }
            }
        }

        SelectWorkspaceRoot(
            conversation.WorkspaceRootId is Guid workspaceRootId
                ? _workspaceRoots.FirstOrDefault(root => root.Id == workspaceRootId)
                : null,
            publishShell: false);
        _selectedToolPermissionMode = conversation.ToolPermissionMode;
        SyncSelectedAgentFromConversation(conversation);
        ApplyConversationFilter(conversation.Id);

        PublishShell(false);
    }

    #endregion

    #region 发送回合与释放 —— 组织一次发送（建会话→落用户消息→流式回合），以及 VM 释放

    private async Task SendAsync()
    {
        var prompt = _composerText.Trim();
        var promptImageAttachments = _pendingPromptImageAttachments.ToArray();
        var selectedModelProfileId = _selectedModelProfileId;
        var selectedWorkspaceRoot = _selectedWorkspaceRoot;
        var selectedToolPermissionMode = _selectedToolPermissionMode;
        var baseMessages = _messages.ToArray();
        var baseToolRuns = _toolRuns.ToArray();
        var baseToolRunAnchors = new Dictionary<Guid, ToolRunAnchor>(_toolRunAnchors);

        _pendingPromptImageAttachments = [];

        if (string.IsNullOrWhiteSpace(prompt) && promptImageAttachments.Length == 0)
        {
            return;
        }

        _composerText = string.Empty;

        ConversationRuntimeState? runtimeState = null;
        AgentTurnState? turnState = null;
        var activityStarted = false;

        try
        {
            var conversation = await EnsureConversationAsync();
            var preferConversationSelection = SelectedConversation is null || IsSelectedConversation(conversation.Id);
            if (IsConversationRunning(conversation.Id))
            {
                return;
            }

            conversation = conversation with
            {
                WorkspaceRootId = selectedWorkspaceRoot?.Id,
                Mode = ConversationMode.Programming,
                ToolPermissionMode = selectedToolPermissionMode,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            await PersistConversationAsync(conversation, preferSelection: preferConversationSelection);

            var runtimeAgent = ResolveRuntimeAgent(conversation.AgentId);
            // The composer's mode pick wins over the agent front matter, so a CLI agent can run
            // one-off turns against provider models (and vice versa) without editing the agent.
            runtimeAgent = runtimeAgent with { Mode = ResolveComposerExecutionMode(runtimeAgent.Mode) };

            runtimeState = StartConversationRuntimeState(
                conversation,
                baseMessages,
                baseToolRuns,
                baseToolRunAnchors);

            var cancellationToken = runtimeState.CancellationTokenSource.Token;
            var userMessageId = Guid.NewGuid();
            var userAttachments = await PersistPromptImageAttachmentsAsync(
                conversation.Id,
                userMessageId,
                promptImageAttachments,
                cancellationToken);

            var userMessage = new MessageRecord(
                userMessageId,
                conversation.Id,
                MessageRole.User,
                prompt,
                MessageStatus.Completed,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                Attachments: userAttachments);
            runtimeState.ReplaceMessage(userMessage);
            await _conversationRepository.UpsertMessageAsync(userMessage);

            if (conversation.Title == "New chat")
            {
                conversation = conversation with
                {
                    Title = CreateConversationTitle(prompt, userAttachments),
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                };
                runtimeState.Conversation = conversation;
                await PersistConversationAsync(conversation, preferSelection: preferConversationSelection);
            }
            PublishRuntimeState(runtimeState, true);

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
                selectedModelProfileId,
                selectedWorkspaceRoot,
                selectedToolPermissionMode,
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

            PublishConversationCompletedNotification(conversation, runtimeState.Messages);
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
                CompleteConversationRuntimeState(runtimeState);
                runtimeState.CancellationTokenSource.Dispose();
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

        foreach (var runtimeState in _conversationRuntimeStates.Values)
        {
            if (runtimeState.IsRunning)
            {
                runtimeState.CancellationTokenSource.Cancel();
            }

            runtimeState.Dispose();
        }

        _conversationRuntimeStates.Clear();
    }

    #endregion

    #region 图片附件处理 —— 校验并落盘提示图片附件、生成安全文件名、解析图片媒体类型

    private async Task<IReadOnlyList<MessageAttachmentRecord>> PersistPromptImageAttachmentsAsync(
        Guid conversationId,
        Guid messageId,
        IReadOnlyList<PromptImageAttachment> attachments,
        CancellationToken cancellationToken)
    {
        if (attachments.Count == 0)
        {
            return [];
        }

        if (attachments.Count > MaxPromptImageAttachments)
        {
            throw new InvalidOperationException($"最多一次添加 {MaxPromptImageAttachments} 张图片。");
        }

        var totalBytes = attachments.Sum(item => Math.Max(0, item.ByteLength));
        if (totalBytes > MaxPromptImageTotalBytes)
        {
            throw new InvalidOperationException("图片总大小超过 30 MB。");
        }

        var targetDirectory = Path.Combine(
            _storagePaths.AppDataDirectory,
            "attachments",
            conversationId.ToString("D"),
            messageId.ToString("D"));
        Directory.CreateDirectory(targetDirectory);

        var results = new List<MessageAttachmentRecord>(attachments.Count);
        foreach (var attachment in attachments)
        {
            var sourcePath = Path.GetFullPath(attachment.SourcePath);
            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException("找不到要发送的图片。", sourcePath);
            }

            var fileInfo = new FileInfo(sourcePath);
            if (fileInfo.Length <= 0)
            {
                throw new InvalidOperationException($"图片 '{fileInfo.Name}' 是空文件。");
            }

            if (fileInfo.Length > MaxPromptImageBytes)
            {
                throw new InvalidOperationException($"图片 '{fileInfo.Name}' 超过 10 MB。");
            }

            var mediaType = ResolveImageMediaType(fileInfo.FullName, attachment.MediaType);
            if (mediaType is null)
            {
                throw new InvalidOperationException($"不支持的图片格式：{fileInfo.Name}");
            }

            var safeName = CreateSafeAttachmentFileName(attachment.FileName, fileInfo.Name);
            var targetPath = Path.Combine(targetDirectory, $"{results.Count + 1:00}-{safeName}");
            await using (var sourceStream = File.OpenRead(fileInfo.FullName))
            await using (var targetStream = File.Create(targetPath))
            {
                await sourceStream.CopyToAsync(targetStream, cancellationToken);
            }

            results.Add(new MessageAttachmentRecord(
                Guid.NewGuid(),
                messageId,
                MessageAttachmentKind.Image,
                safeName,
                mediaType,
                targetPath,
                fileInfo.Length,
                DateTimeOffset.UtcNow));
        }

        return results.ToArray();
    }

    private static string CreateSafeAttachmentFileName(string? requestedName, string fallbackName)
    {
        var fileName = Path.GetFileName(string.IsNullOrWhiteSpace(requestedName) ? fallbackName : requestedName);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = fallbackName;
        }

        foreach (var invalidChar in Path.GetInvalidFileNameChars())
        {
            fileName = fileName.Replace(invalidChar, '_');
        }

        return string.IsNullOrWhiteSpace(fileName) ? "image.png" : fileName;
    }

    private static string? ResolveImageMediaType(string path, string? suppliedMediaType = null)
    {
        if (!string.IsNullOrWhiteSpace(suppliedMediaType) &&
            IsSupportedImageMediaType(suppliedMediaType))
        {
            return suppliedMediaType.Trim().ToLowerInvariant();
        }

        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ => null
        };
    }

    private static bool IsSupportedImageMediaType(string mediaType)
        => mediaType.Trim().ToLowerInvariant() is "image/png" or "image/jpeg" or "image/webp" or "image/gif";

    #endregion

    #region 会话创建 / 持久化 / 过滤 —— 新建会话记录、落盘并同步选中、维护会话列表与可见性过滤

    private async Task<ConversationRecord> EnsureConversationAsync()
    {
        if (SelectedConversation is not null)
        {
            return SelectedConversation;
        }

        await Task.CompletedTask;
        return CreateConversationRecord();
    }

    private ConversationRecord CreateConversationRecord()
    {
        var now = DateTimeOffset.UtcNow;
        return new ConversationRecord(
            Guid.NewGuid(),
            "New chat",
            _selectedWorkspaceRoot?.Id,
            ConversationMode.Programming,
            _selectedToolPermissionMode,
            ResolveSelectedAgent().Id,
            now,
            now);
    }

    private async Task PersistConversationAsync(ConversationRecord conversation, bool preferSelection = true)
    {
        await _conversationRepository.UpsertConversationAsync(conversation);
        UpsertConversation(conversation, preferSelection);
        if (preferSelection || SelectedConversation?.Id == conversation.Id)
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

    private static void ReplaceMessage(ConversationRuntimeState runtimeState, MessageRecord message)
    {
        var index = runtimeState.Messages.FindIndex(item => item.Id == message.Id);
        if (index >= 0)
        {
            runtimeState.Messages[index] = message;
        }
        else
        {
            runtimeState.Messages.Add(message);
        }
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

        SelectedConversation = targetConversation;
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

    private static void UpsertToolRun(ConversationRuntimeState runtimeState, ToolExecutionRecord record)
    {
        var index = runtimeState.ToolRuns.FindIndex(item => item.Id == record.Id);
        if (index >= 0)
        {
            runtimeState.ToolRuns[index] = record;
        }
        else
        {
            runtimeState.ToolRuns.Add(record);
        }
    }

    #endregion

    #region Transcript 渲染构建 —— 计算快照指纹去重后，把消息 / 工具运行 / 附件构建成前端渲染项并触发 TranscriptChanged

    private void PublishShell(bool autoScroll)
    {
        var transcriptMessages = GetSelectedTranscriptMessages();
        var transcriptToolRuns = GetSelectedTranscriptToolRuns();
        var transcriptToolRunAnchors = GetSelectedTranscriptToolRunAnchors();
        var isBusy = IsSelectedConversationRunning();
        var activityText = isBusy ? GetSelectedRuntimeState()?.ActivityText : null;
        var fingerprint = BuildShellFingerprint(
            autoScroll,
            transcriptMessages,
            transcriptToolRuns,
            transcriptToolRunAnchors,
            activityText);
        if (string.Equals(_lastPublishedShellFingerprint, fingerprint, StringComparison.Ordinal))
        {
            return;
        }

        _lastPublishedShellFingerprint = fingerprint;

        var toolRunsByMessageId = TranscriptToolRunPresenter.BuildToolRunsByMessageId(
            transcriptMessages,
            transcriptToolRuns,
            transcriptToolRunAnchors);
        PruneMessageRenderCache(transcriptMessages);
        var orderedItems = transcriptMessages
            .OrderBy(item => item.CreatedAtUtc)
            .Select(item => BuildMessageItemCached(
                item,
                toolRunsByMessageId.TryGetValue(item.Id, out var toolRuns) ? toolRuns : []))
            .ToArray();

        var conversations = BuildConversationItems();

        TranscriptChanged?.Invoke(this, new TranscriptRenderState(
            orderedItems,
            autoScroll,
            conversations,
            SelectedConversation?.Id.ToString("D"),
            isBusy,
            activityText,
            ResolveComposerExecutionMode(ResolveSelectedAgent().Mode).ToString().ToLowerInvariant()));
    }

    private string BuildShellFingerprint(
        bool autoScroll,
        IReadOnlyList<MessageRecord> transcriptMessages,
        IReadOnlyList<ToolExecutionRecord> transcriptToolRuns,
        IReadOnlyDictionary<Guid, ToolRunAnchor> transcriptToolRunAnchors,
        string? activityText)
    {
        var navigationConversations = GetNavigationConversations()
            .OrderByDescending(item => item.UpdatedAtUtc)
            .ThenBy(item => item.CreatedAtUtc)
            .ToArray();
        var builder = new StringBuilder();
        builder.Append(autoScroll ? '1' : '0')
            .Append('|')
            .Append(SelectedConversation?.Id.ToString("D"))
            .Append('|')
            .Append(IsSelectedConversationRunning() ? '1' : '0')
            .Append('|')
            .Append(activityText)
            .Append('|')
            .Append((int)ResolveComposerExecutionMode(ResolveSelectedAgent().Mode))
            .Append('|')
            .Append(navigationConversations.Length)
            .Append('|');

        foreach (var conversation in navigationConversations)
        {
            builder.Append(conversation.Id.ToString("D"))
                .Append(':')
                .Append(conversation.UpdatedAtUtc.UtcTicks)
                .Append(':')
                .Append(conversation.WorkspaceRootId?.ToString("D"))
                .Append(':')
                .Append(SelectedConversation?.Id == conversation.Id ? '1' : '0')
                .Append(';');
        }

        builder.Append('|')
            .Append(transcriptMessages.Count)
            .Append('|');

        foreach (var message in transcriptMessages.OrderBy(item => item.CreatedAtUtc))
        {
            builder.Append(message.Id.ToString("D"))
                .Append(':')
                .Append((int)message.Role)
                .Append(':')
                .Append((int)message.Status)
                .Append(':')
                .Append(message.UpdatedAtUtc.UtcTicks)
                .Append(':')
                .Append(message.MarkdownContent.Length)
                .Append(':')
                .Append(StringComparer.Ordinal.GetHashCode(message.MarkdownContent))
                .Append(':')
                .Append(message.DurationMs)
                .Append(':')
                .Append(message.ErrorMessage)
                .Append(':');

            if (message.Attachments is { Count: > 0 } attachments)
            {
                foreach (var attachment in attachments)
                {
                    builder.Append(attachment.Id.ToString("D"))
                        .Append(',')
                        .Append(attachment.StoragePath)
                        .Append(',')
                        .Append(attachment.ByteLength)
                        .Append(',');
                }
            }

            builder.Append(';');
        }

        builder.Append('|')
            .Append(transcriptToolRuns.Count)
            .Append('|');

        foreach (var toolRun in transcriptToolRuns.OrderBy(item => item.CreatedAtUtc))
        {
            builder.Append(toolRun.Id.ToString("D"))
                .Append(':')
                .Append((int)toolRun.Status)
                .Append(':')
                .Append(toolRun.UpdatedAtUtc.UtcTicks)
                .Append(':')
                .Append(toolRun.DurationMs)
                .Append(':')
                .Append(toolRun.MessageId)
                .Append(':')
                .Append(toolRun.AfterSegmentIndex)
                .Append(':')
                .Append(toolRun.ResultSummary)
                .Append(':')
                .Append(toolRun.ResultContent?.Length ?? 0)
                .Append(':')
                .Append(toolRun.ResultContent is null ? 0 : StringComparer.Ordinal.GetHashCode(toolRun.ResultContent))
                .Append(';');
        }

        builder.Append('|');
        foreach (var anchor in transcriptToolRunAnchors.OrderBy(item => item.Key))
        {
            builder.Append(anchor.Key.ToString("D"))
                .Append(':')
                .Append(anchor.Value.MessageId.ToString("D"))
                .Append(':')
                .Append(anchor.Value.AfterSegmentIndex)
                .Append(';');
        }

        return builder.ToString();
    }

    private TranscriptConversationItem[] BuildConversationItems()
        => GetNavigationConversations()
            .OrderByDescending(item => item.UpdatedAtUtc)
            .ThenBy(item => item.CreatedAtUtc)
            .Select(BuildConversationItem)
            .ToArray();

    private IEnumerable<ConversationRecord> GetNavigationConversations()
        => _allConversations.Where(MatchesNavigationConversation);

    private bool MatchesNavigationConversation(ConversationRecord conversation)
        => conversation.Mode == ConversationMode.Programming &&
           string.Equals(conversation.AgentId, ResolveSelectedAgent().Id, StringComparison.OrdinalIgnoreCase);

    private TranscriptConversationItem BuildConversationItem(ConversationRecord conversation)
    {
        var workspaceRoot = conversation.WorkspaceRootId is Guid workspaceRootId
            ? _workspaceRoots.FirstOrDefault(root => root.Id == workspaceRootId)
            : null;
        var agentName = ResolveConversationAgentName(conversation);

        return new TranscriptConversationItem(
            conversation.Id.ToString("D"),
            conversation.Title,
            conversation.UpdatedAtUtc.LocalDateTime.ToString("yyyy-MM-dd HH:mm"),
            SelectedConversation?.Id == conversation.Id,
            null,
            agentName,
            conversation.AgentId,
            agentName,
            conversation.WorkspaceRootId?.ToString("D"),
            workspaceRoot?.Name,
            workspaceRoot?.RootPath);
    }

    private IReadOnlyList<TranscriptImageAttachment> BuildImageAttachments(MessageRecord message)
    {
        if (message.Attachments is not { Count: > 0 } attachments)
        {
            return [];
        }

        return attachments
            .Where(item => item.Kind == MessageAttachmentKind.Image)
            .Select(item => new TranscriptImageAttachment(
                item.Id.ToString("D"),
                item.FileName,
                item.MediaType,
                item.ByteLength,
                TryCreateAttachmentSourceUrl(item)))
            .ToArray();
    }

    private string? TryCreateAttachmentSourceUrl(MessageAttachmentRecord attachment)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(attachment.StoragePath) ||
                !File.Exists(attachment.StoragePath))
            {
                return null;
            }

            var attachmentsRoot = Path.GetFullPath(Path.Combine(_storagePaths.AppDataDirectory, "attachments"));
            var attachmentPath = Path.GetFullPath(attachment.StoragePath);
            var relativePath = Path.GetRelativePath(attachmentsRoot, attachmentPath);
            if (relativePath.StartsWith("..", StringComparison.Ordinal) ||
                Path.IsPathRooted(relativePath))
            {
                return null;
            }

            var normalizedPath = relativePath.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
            return $"https://{AttachmentHostName}/{Uri.EscapeDataString(normalizedPath).Replace("%2F", "/", StringComparison.OrdinalIgnoreCase)}";
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Per-message render cache: markdown → HTML conversion, segment splitting and attachment
    /// resolution are skipped for messages whose fingerprint is unchanged, so a streaming tick
    /// only rebuilds the streaming message instead of the whole transcript.
    /// Keyed by message id; only touched on the dispatcher thread via <see cref="PublishShell"/>.
    /// </summary>
    private readonly Dictionary<Guid, (string Fingerprint, TranscriptRenderItem Item)> _messageRenderCache = new();

    private TranscriptRenderItem BuildMessageItemCached(
        MessageRecord message,
        IReadOnlyList<ToolRunPlacement> toolRuns)
    {
        var fingerprint = BuildMessageRenderFingerprint(message, toolRuns);
        if (_messageRenderCache.TryGetValue(message.Id, out var cached) &&
            string.Equals(cached.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            return cached.Item;
        }

        var item = BuildMessageItem(message, toolRuns);
        _messageRenderCache[message.Id] = (fingerprint, item);
        return item;
    }

    private void PruneMessageRenderCache(IReadOnlyList<MessageRecord> messages)
    {
        if (_messageRenderCache.Count == 0)
        {
            return;
        }

        var liveIds = new HashSet<Guid>(messages.Select(item => item.Id));
        List<Guid>? staleIds = null;
        foreach (var id in _messageRenderCache.Keys)
        {
            if (!liveIds.Contains(id))
            {
                (staleIds ??= []).Add(id);
            }
        }

        if (staleIds is null)
        {
            return;
        }

        foreach (var id in staleIds)
        {
            _messageRenderCache.Remove(id);
        }
    }

    private static string BuildMessageRenderFingerprint(
        MessageRecord message,
        IReadOnlyList<ToolRunPlacement> toolRuns)
    {
        var builder = new StringBuilder();
        builder.Append((int)message.Role)
            .Append(':')
            .Append((int)message.Status)
            .Append(':')
            .Append(message.UpdatedAtUtc.UtcTicks)
            .Append(':')
            .Append(message.MarkdownContent.Length)
            .Append(':')
            .Append(StringComparer.Ordinal.GetHashCode(message.MarkdownContent))
            .Append(':')
            .Append(message.DurationMs)
            .Append(':')
            .Append(message.ErrorMessage)
            .Append(':')
            .Append(message.AgentName)
            .Append(':');

        if (message.Attachments is { Count: > 0 } attachments)
        {
            foreach (var attachment in attachments)
            {
                builder.Append(attachment.Id.ToString("D"))
                    .Append(',')
                    .Append(attachment.StoragePath)
                    .Append(',')
                    .Append(attachment.ByteLength)
                    .Append(',');
            }
        }

        builder.Append('|');
        foreach (var placement in toolRuns)
        {
            builder.Append(placement.Record.Id.ToString("D"))
                .Append(':')
                .Append((int)placement.Record.Status)
                .Append(':')
                .Append(placement.Record.UpdatedAtUtc.UtcTicks)
                .Append(':')
                .Append(placement.AfterSegmentIndex)
                .Append(';');
        }

        return builder.ToString();
    }

    private TranscriptRenderItem BuildMessageItem(
        MessageRecord message,
        IReadOnlyList<ToolRunPlacement> toolRuns)
    {
        var contentMarkdown = message.MarkdownContent;
        var renderSegments = new List<TranscriptRenderSegment>();

        if (message.Role == MessageRole.Assistant)
        {
            var segments = AssistantMessageSegmenter.Split(message.MarkdownContent);
            contentMarkdown = segments.ContentMarkdown;
            var toolRunsById = toolRuns.ToDictionary(item => item.Record.Id);
            var consumedToolRunIds = new HashSet<Guid>();

            foreach (var segment in segments.Segments)
            {
                if (segment.Kind == AssistantMessageSegmentKind.ToolAnchor)
                {
                    if (segment.ToolExecutionId is Guid toolExecutionId &&
                        toolRunsById.TryGetValue(toolExecutionId, out var placement) &&
                        consumedToolRunIds.Add(toolExecutionId))
                    {
                        renderSegments.Add(TranscriptToolRunPresenter.BuildToolSegment(placement.Record));
                    }

                    continue;
                }

                var html = string.IsNullOrWhiteSpace(segment.Markdown)
                    ? string.Empty
                    : _markdownHtmlRenderer.ToHtml(segment.Markdown);

                renderSegments.Add(new TranscriptRenderSegment(
                    segment.Kind == AssistantMessageSegmentKind.Thinking ? "thinking" : "content",
                    html,
                    segment.IsPending));
            }

            if (toolRuns.Count > consumedToolRunIds.Count)
            {
                toolRuns = toolRuns
                    .Where(item => !consumedToolRunIds.Contains(item.Record.Id))
                    .ToArray();
            }
            else
            {
                toolRuns = [];
            }
        }
        else if (!string.IsNullOrWhiteSpace(contentMarkdown))
        {
            renderSegments.Add(new TranscriptRenderSegment(
                "content",
                _markdownHtmlRenderer.ToHtml(contentMarkdown),
                false));
        }

        if (message.Status is MessageStatus.Failed or MessageStatus.Cancelled &&
            !string.IsNullOrWhiteSpace(message.ErrorMessage))
        {
            var statusClass = message.Status == MessageStatus.Cancelled
                ? "message-cancelled"
                : "message-error";
            var errorHtml = $"<p class=\"{statusClass}\">{WebUtility.HtmlEncode(message.ErrorMessage)}</p>";

            if (renderSegments.Count > 0 && string.Equals(renderSegments[^1].Kind, "content", StringComparison.Ordinal))
            {
                renderSegments[^1] = renderSegments[^1] with { Html = renderSegments[^1].Html + errorHtml };
            }
            else
            {
                renderSegments.Add(new TranscriptRenderSegment("content", errorHtml, false));
            }
        }

        if (message.Role == MessageRole.Assistant && toolRuns.Count > 0)
        {
            TranscriptToolRunPresenter.InsertToolSegments(renderSegments, toolRuns);
        }

        return new TranscriptRenderItem(
            message.Id.ToString("D"),
            "message",
            message.Role.ToString().ToLowerInvariant(),
            message.Status.ToString().ToLowerInvariant(),
            BuildAvatarText(message),
            message.Role switch
            {
                MessageRole.User => string.Empty,
                MessageRole.Assistant => message.AgentName ?? string.Empty,
                _ => "System"
            },
            message.Role == MessageRole.Assistant ? message.AgentRole : null,
            renderSegments,
            message.Role == MessageRole.Assistant && message.Status == MessageStatus.Streaming,
            null,
            message.ErrorMessage,
            message.DurationMs,
            message.CreatedAtUtc.LocalDateTime.ToString("yyyy-MM-dd HH:mm"),
            message.AgentId?.ToString("D"),
            BuildImageAttachments(message));
    }

    private static string CreateConversationTitle(string text, IReadOnlyList<MessageAttachmentRecord>? attachments = null)
    {
        var normalized = text.ReplaceLineEndings(" ").Trim();
        if (string.IsNullOrWhiteSpace(normalized) && attachments is { Count: > 0 })
        {
            normalized = attachments.Count == 1
                ? attachments[0].FileName
                : $"{attachments.Count} 张图片";
        }

        return normalized.Length > 48 ? normalized[..48] + "..." : normalized;
    }

    #endregion

}






