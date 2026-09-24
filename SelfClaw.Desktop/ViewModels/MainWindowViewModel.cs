using SelfClaw.Desktop.Services.Agents;
using SelfClaw.Desktop.Services.Agents.Definitions;
using SelfClaw.Desktop.Services.Settings;
using SelfClaw.Desktop.Services.Transcript.Views;
using SelfClaw.Desktop.Services.Workspace.Models;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Desktop.Services.AgentActivity;
using SelfClaw.Desktop.Services.Plugins;
using SelfClaw.Desktop.Services.ProgrammingAssistant;
using SelfClaw.Desktop.Services.ProgrammingAssistant.Models;
using SelfClaw.Desktop.Services.Runtime;
using SelfClaw.Desktop.Services.Workspace;
using SelfClaw.Desktop.Services.Transcript;
using SelfClaw.Desktop.Services.Workspace.Abstractions;

namespace SelfClaw.Desktop.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject, IDisposable, IWorkspaceSelectionController, IPluginPanelContextSource,
    SelfClaw.Desktop.Services.Activities.IActivityPanelScopeSource
{
    #region 字段与构造函数 —— 依赖注入字段、运行时集合状态、流式发布定时器初始化

    private readonly IConversationRepository _conversationRepository;
    private readonly ConversationTurnEngine _turnEngine;
    private readonly ConversationSessionCoordinator _conversationSessions;
    private readonly AgentActivityCoordinator _agentActivityCoordinator;
    private readonly TranscriptPublisher _transcriptPublisher;
    private readonly AgentSettingsService _agentSettings;
    private readonly IExtensionStateChangeNotifier _extensionChanges;
    private readonly DesktopSettingsJsonStore _settingsStore;
    private readonly ConversationWorkspaceService _workspaces;
    private readonly ConversationDeletionService _deletion;
    private readonly Dispatcher? _dispatcher;
    private readonly ILogger<MainWindowViewModel> _logger;

    private readonly List<ConversationRecord> _allConversations = [];
    private readonly List<ConversationRecord> _filteredConversations = [];
    private readonly List<DesktopAgentDefinition> _agents = [];
    private readonly List<WorkspaceRoot> _workspaceRoots = [];
    private Task _selectionLoadTask = Task.CompletedTask;
    private Task? _initializationTask;
    private int _workspaceLoadVersion;
    private int _conversationLoadVersion;
    private int _selectionVersion;
    private ConversationRecord? _selectedConversation;
    private WorkspaceRoot? _selectedWorkspaceRoot;
    private string _selectedAgentId = DesktopAgentDefinitionService.BuildAgentId;
    private ToolPermissionMode _selectedToolPermissionMode = ToolPermissionMode.RequireApproval;
    private bool _disposed;
    private readonly SemaphoreSlim _composerModeGate = new(1, 1);
    // Composer-level execution mode override ("本地 CLI" / "提供商"). Null defers to the
    // active agent's own mode; a value forces every sent turn onto that runtime branch.
    private AgentExecutionMode? _composerModeOverride;
    private long _capabilityRevision;

    internal MainWindowViewModel(
        IConversationRepository conversationRepository,
        ConversationTurnEngine turnEngine,
        ConversationSessionCoordinator conversationSessions,
        AgentActivityCoordinator agentActivityCoordinator,
        TranscriptPublisher transcriptPublisher,
        AgentSettingsService agentSettings,
        IExtensionStateChangeNotifier extensionChanges,
        DesktopSettingsJsonStore settingsStore,
        ConversationWorkspaceService workspaces,
        ConversationDeletionService deletion,
        ILogger<MainWindowViewModel> logger,
        Dispatcher? dispatcher = null)
    {
        _conversationRepository = conversationRepository;
        _turnEngine = turnEngine;
        _conversationSessions = conversationSessions;
        _agentActivityCoordinator = agentActivityCoordinator;
        _transcriptPublisher = transcriptPublisher;
        _agentSettings = agentSettings;
        _extensionChanges = extensionChanges;
        _settingsStore = settingsStore;
        _workspaces = workspaces;
        _deletion = deletion;
        _dispatcher = dispatcher;
        _logger = logger;
        _transcriptPublisher.Attach(BuildTranscriptProjectionRequest);
        _agentSettings.Changed += OnAgentsChanged;
        _extensionChanges.StateChanged += OnExtensionStateChanged;
        _conversationSessions.SelectedStateChanged += OnSelectedRuntimeChanged;
    }

    #endregion

    #region 公开入口与属性
    /// <summary>
    /// 当前选中工作区根目录路径。供宿主窗口解析终端工作目录使用。
    /// </summary>
    public string? SelectedWorkspaceRootPath => _selectedWorkspaceRoot?.RootPath;

    public WorkspaceRoot? SelectedWorkspaceRoot => _selectedWorkspaceRoot;

    public string SelectedAgentId => _selectedAgentId;

    public IReadOnlyList<WorkspaceRoot> WorkspaceRoots => _workspaceRoots.ToArray();

    public void UpdateCapabilityRevision(long revision)
    {
        if (revision <= _capabilityRevision)
        {
            return;
        }

        _capabilityRevision = revision;
        _transcriptPublisher.Invalidate();
        PublishShell(false);
    }

    private ConversationRecord? SelectedConversation => _selectedConversation;

    /// <summary>
    /// 启动时一次性加载：代理、工作区、会话列表，并发布初始 transcript。
    /// 由宿主窗口（OnLoaded）与通知激活服务调用，是渲染路径的引导入口。
    /// </summary>
    public Task InitializeAsync()
    {
        _dispatcher?.VerifyAccess();
        if (_initializationTask is null || _initializationTask.IsFaulted || _initializationTask.IsCanceled)
            _initializationTask = InitializeCoreAsync();
        return _initializationTask;
    }

    private async Task InitializeCoreAsync()
    {
        await _composerModeGate.WaitAsync();
        try
        {
            _composerModeOverride = ParseComposerMode(
                (await _settingsStore.ReadNodeAsync<ComposerSettings>(ComposerSettingsNode))?.ExecutionMode);
        }
        finally { _composerModeGate.Release(); }
        ReloadAgents();
        // First render must not wait for git probing: the root list comes from the database and the
        // selected root's git state is fetched on demand by the selection bridge.
        await ReloadWorkspaceRootListAsync();
        await ReloadConversationsAsync();
        await _selectionLoadTask;

        PublishShell(false);
    }

    /// <summary>
    /// WebView 的 "send-prompt" 消息经宿主路由后落到这里，触发一次发送回合。
    /// </summary>
    public Task<PromptSubmissionResult> SubmitPromptAsync(string prompt, string? workspaceMode = null, Guid? modelProfileId = null)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        var normalizedPrompt = prompt.Trim();
        if (normalizedPrompt.Length == 0)
        {
            return Task.FromResult(new PromptSubmissionResult(false));
        }

        return SendAsync(new PromptSubmissionSnapshot(
            normalizedPrompt,
            SelectedConversation,
            _selectedWorkspaceRoot,
            _selectedToolPermissionMode,
            modelProfileId,
            _selectedAgentId,
            _composerModeOverride,
            ParseWorkspaceMode(workspaceMode),
            _selectionVersion));
    }

    /// <summary>
    /// Cancels the selected conversation's running turn (WebView "stop-generation" / Esc).
    /// The turn engine observes the cancellation and persists the cancelled terminal state.
    /// </summary>
    public void StopSelectedConversation()
        => _conversationSessions.StopSelected();

    /// <summary>
    /// Applies the composer's mode pick ("cli" / "direct") as a persisted override on top of the
    /// active agent's own mode, so provider models can be exercised without a Direct-mode agent.
    /// Unknown values clear the override and fall back to the agent definition.
    /// </summary>
    public async Task SelectComposerModeAsync(string? mode)
    {
        await _composerModeGate.WaitAsync();
        try
        {
            var parsed = ParseComposerMode(mode);
            if (_composerModeOverride == parsed)
            {
                return;
            }

            await _settingsStore.WriteNodeAsync(
                ComposerSettingsNode,
                new ComposerSettings(parsed?.ToString().ToLowerInvariant()));
            _composerModeOverride = parsed;

            PublishShell(false);

        }
        finally { _composerModeGate.Release(); }
    }

    /// <summary>
    /// Applies the composer's agent pick as the VM's current selection. The conversation record is the
    /// persistence unit: agent id is bound at conversation creation and is immutable for the
    /// conversation's lifetime, so this only governs the next new conversation and the conversation list
    /// filter (which narrows to the selected agent's history). The capability/skill/mcp/prompt for any
    /// running or selected conversation remain driven by that conversation's own bound agent id.
    /// </summary>
    public Task SelectAgentAsync(string? agentId)
    {
        var normalizedAgentId = NormalizeAgentId(agentId);
        var nextAgent = ResolveAgent(normalizedAgentId);
        if (string.Equals(_selectedAgentId, nextAgent.Id, StringComparison.OrdinalIgnoreCase))
        {
            return Task.CompletedTask;
        }

        SelectAgentCore(nextAgent.Id);
        _selectionVersion++;
        ApplyConversationFilter(SelectedConversation?.Id);
        PublishShell(false);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Applies the composer's tool permission pick ("require-approval" / "full-access") as the mode for
    /// the next admitted turn. The conversation record itself is the persistence unit: the turn engine
    /// writes this mode onto the conversation, and loading a conversation resyncs this field.
    /// </summary>
    public Task SelectToolPermissionModeAsync(string? mode)
    {
        var parsed = ParseToolPermissionMode(mode);
        if (parsed is null || parsed == _selectedToolPermissionMode)
        {
            return Task.CompletedTask;
        }

        _selectedToolPermissionMode = parsed.Value;
        PublishShell(false);
        return Task.CompletedTask;
    }

    private static ToolPermissionMode? ParseToolPermissionMode(string? mode)
        => mode?.Trim().ToLowerInvariant() switch
        {
            "require-approval" => ToolPermissionMode.RequireApproval,
            "full-access" => ToolPermissionMode.FullAccess,
            _ => null
        };

    private static string ToToolPermissionModeWire(ToolPermissionMode mode)
        => mode switch
        {
            ToolPermissionMode.FullAccess => "full-access",
            _ => "require-approval"
        };

    private AgentExecutionMode ResolveComposerExecutionMode(AgentExecutionMode agentMode)
        => _composerModeOverride ?? agentMode;

    private static AgentExecutionMode? ParseComposerMode(string? mode)
        => mode?.Trim().ToLowerInvariant() switch
        {
            "cli" => AgentExecutionMode.Cli,
            "direct" => AgentExecutionMode.Direct,
            _ => null
        };

    private static GitWorkspaceMode ParseWorkspaceMode(string? mode)
        => mode?.Trim().ToLowerInvariant() switch
        {
            "worktree" or "managed-worktree" => GitWorkspaceMode.ManagedWorktree,
            _ => GitWorkspaceMode.Local
        };

    private const string ComposerSettingsNode = "composer";

    public Task StartNewConversationAsync()
        => SelectConversationCoreAsync(null, clearWorkspace: true);

    public Task SelectConversationAsync(Guid conversationId)
    {
        var conversation = _filteredConversations.FirstOrDefault(item => item.Id == conversationId)
            ?? _allConversations.FirstOrDefault(item => item.Id == conversationId);
        if (conversation is null || conversation.Kind != ConversationKind.Interactive)
        {
            throw new InvalidOperationException("The selected conversation no longer exists.");
        }

        return SelectConversationCoreAsync(conversation);
    }

    public Task DeleteConversationAsync(Guid conversationId, bool removeManagedWorktree = false)
        => DeleteConversationsAsync([conversationId], removeManagedWorktree);

    public async Task DeleteConversationsAsync(IEnumerable<Guid> conversationIds, bool removeManagedWorktree = false)
    {
        ArgumentNullException.ThrowIfNull(conversationIds);
        var requestedIds = conversationIds.Where(id => id != Guid.Empty).ToHashSet();
        var deleteIds = _allConversations.Where(item => requestedIds.Contains(item.Id)).Select(item => item.Id).ToArray();
        if (deleteIds.Length == 0) return;
        try
        {
            await _deletion.DeleteAsync(deleteIds, removeManagedWorktree);
        }
        finally
        {
            // Deleting a conversation only releases a checkout; re-probing every workspace root
            // with git is expensive and unnecessary unless a worktree was actually removed.
            if (removeManagedWorktree) await ReloadWorkspaceRootsAsync();
            else await ReloadWorkspaceRootListAsync();
            await ReloadConversationsAsync();
        }
    }

    public async Task DeleteWorkspaceRootAsync(Guid workspaceRootId)
    {
        if (workspaceRootId == Guid.Empty)
        {
            return;
        }

        await _workspaces.DeleteRootAsync(workspaceRootId);
        await ReloadWorkspaceRootsAsync();
        await ReloadConversationsAsync();
    }

    #endregion

    #region 工作区选择 —— 切换当前工作区

    private void SelectWorkspaceRoot(WorkspaceRoot? workspaceRoot, bool publishShell)
    {
        _dispatcher?.VerifyAccess();
        if (_selectedWorkspaceRoot?.Id == workspaceRoot?.Id)
        {
            if (!ReferenceEquals(_selectedWorkspaceRoot, workspaceRoot))
            {
                _selectedWorkspaceRoot = workspaceRoot;
                OnPropertyChanged(nameof(SelectedWorkspaceRootPath));
            }

            return;
        }

        _selectionVersion++;
        _selectedWorkspaceRoot = workspaceRoot;
        OnPropertyChanged(nameof(SelectedWorkspaceRootPath));
        if (publishShell)
        {
            PublishShell(false);
        }
    }

    public async Task ReloadWorkspaceSelectionAsync()
    {
        var version = _selectionVersion;
        await ReloadWorkspaceRootsAsync();
        if (version == _selectionVersion) ApplyWorkspaceSelectionChanged(SelectedConversation?.Id);
        else PublishShell(false);
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
        var version = _selectionVersion;
        var root = await _workspaces.AddAsync(rootPath);
        await ReloadWorkspaceRootsAsync();
        if (version == _selectionVersion)
        {
            SelectWorkspaceRoot(_workspaceRoots.FirstOrDefault(item => item.Id == root.Id) ?? root, publishShell: false);
            ApplyWorkspaceSelectionChanged();
        }
        return root;
    }

    private void ApplyWorkspaceSelectionChanged(Guid? preferredConversationId = null)
    {
        if (_agents.Count == 0)
        {
            return;
        }

        ApplyConversationFilter(preferredConversationId);
    }

    #endregion

    #region 数据加载 —— 重载工作区 / 会话列表，并加载选中会话的消息与工具运行

    private Task ReloadWorkspaceRootsAsync() => ReloadWorkspaceRootsCoreAsync(probeGitState: true);

    private Task ReloadWorkspaceRootListAsync() => ReloadWorkspaceRootsCoreAsync(probeGitState: false);

    private async Task ReloadWorkspaceRootsCoreAsync(bool probeGitState)
    {
        var version = ++_workspaceLoadVersion;
        var roots = probeGitState
            ? await _workspaces.ListAsync()
            : await _workspaces.ListRootsAsync();
        _dispatcher?.VerifyAccess();
        if (version != _workspaceLoadVersion) return;
        var selectedId = _selectedWorkspaceRoot?.Id;
        ReplaceList(_workspaceRoots, roots);
        SelectWorkspaceRoot(selectedId is Guid id ? roots.FirstOrDefault(root => root.Id == id) : null, publishShell: false);
    }

    private async Task ReloadConversationsAsync()
    {
        var version = ++_conversationLoadVersion;
        var conversations = await _conversationRepository.ListConversationsAsync();
        _dispatcher?.VerifyAccess();
        if (version != _conversationLoadVersion) return;
        var selectedId = SelectedConversation?.Id;
        _allConversations.Clear();
        _allConversations.AddRange(conversations.Where(conversation =>
            conversation.Kind == ConversationKind.Interactive));
        ApplyConversationFilter(selectedId);
    }

    private Task SelectConversationCoreAsync(ConversationRecord? conversation, bool clearWorkspace = false)
    {
        _dispatcher?.VerifyAccess();
        if (conversation is null && clearWorkspace)
        {
            SelectWorkspaceRoot(null, publishShell: false);
        }

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
            throw;
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

    #region 回合意图与释放

    private async Task<PromptSubmissionResult> SendAsync(PromptSubmissionSnapshot submission)
    {
        PreparedConversationWorkspace? prepared = null;
        var admitted = false;
        try
        {
            if (submission.SelectionVersion != _selectionVersion)
                return new PromptSubmissionResult(false, "当前选择已改变，请重试。");
            var runtimeAgent = ResolveRuntimeAgent(submission.Conversation?.AgentId ?? submission.AgentId);
            runtimeAgent = runtimeAgent with { Mode = submission.ExecutionModeOverride ?? runtimeAgent.Mode };
            prepared = await _workspaces.PrepareAsync(submission.Conversation, submission.WorkspaceRoot,
                submission.WorkspaceMode, submission.Prompt);
            if (submission.SelectionVersion != _selectionVersion)
                return new PromptSubmissionResult(false, "当前选择已改变，请重试。");
            var admission = await _turnEngine.TryAdmitAsync(new DesktopConversationTurnRequest(
                submission.Conversation, runtimeAgent, submission.Prompt, submission.ModelProfileId,
                prepared.WorkspaceRoot, submission.ToolPermissionMode, prepared.ConversationId));
            if (admission is null)
                return new PromptSubmissionResult(false, "当前会话正在执行或应用正在退出，请稍候。");
            admitted = true;
            _ = ExecuteAcceptedTurnAsync(admission);
            if (prepared.Provisioned && prepared.WorkspaceRoot is { } root) _workspaceRoots.Add(root);
            ApplyAdmittedConversation(admission.Conversation, submission.SelectionVersion == _selectionVersion);
            return new PromptSubmissionResult(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to prepare the chat turn.");
            return new PromptSubmissionResult(false, exception.Message);
        }
        finally
        {
            if (!admitted && prepared?.Provisioned == true)
                await _workspaces.DiscardAsync(prepared);
        }
    }

    private async Task ExecuteAcceptedTurnAsync(AdmittedConversationTurn admission)
    {
        try
        {
            await _turnEngine.ExecuteAsync(admission);
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

    #endregion

    #region 会话导航状态与过滤

    private void ApplyAdmittedConversation(ConversationRecord conversation, bool preferSelection)
    {
        _dispatcher?.VerifyAccess();
        _allConversations.RemoveAll(item => item.Id == conversation.Id);
        _allConversations.Insert(0, conversation);
        if (preferSelection)
        {
            SelectWorkspaceRoot(_workspaceRoots.FirstOrDefault(item => item.Id == conversation.WorkspaceRootId), publishShell: false);
            SyncSelectedAgentFromConversation(conversation);
            _ = SelectConversationCoreAsync(conversation);
        }
        ReplaceList(_filteredConversations, GetFilteredConversations());
        PublishShell(false);
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
        if (conversation.Kind != ConversationKind.Interactive ||
            conversation.Mode != ConversationMode.Programming)
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

    #region Transcript 发布

    private void PublishShell(bool autoScroll)
    {
        OnPropertyChanged(string.Empty);
        _transcriptPublisher.PublishNow(autoScroll);
    }

    public void Dispose()
    {
        _disposed = true;
        _agentSettings.Changed -= OnAgentsChanged;
        _extensionChanges.StateChanged -= OnExtensionStateChanged;
        _conversationSessions.SelectedStateChanged -= OnSelectedRuntimeChanged;
    }

    private void OnAgentsChanged() => RunOnUi(() => { ReloadAgents(); PublishShell(false); });
    private void OnExtensionStateChanged(long revision) => RunOnUi(() => UpdateCapabilityRevision(revision));
    private void OnSelectedRuntimeChanged() => RunOnUi(() => OnPropertyChanged(string.Empty));

    private void RunOnUi(Action action)
    {
        if (_disposed || _dispatcher?.HasShutdownStarted == true) return;
        if (_dispatcher is not null && !_dispatcher.CheckAccess())
            _ = _dispatcher.InvokeAsync(() => { if (!_disposed) action(); });
        else action();
    }

    /// <summary>
    /// The context handed to right-hand plugin panels, for both <c>getContext()</c> and the
    /// <c>context-changed</c> push. It is captured from the same values as
    /// <see cref="BuildTranscriptProjectionRequest"/>, so what a panel reads always matches what the
    /// shell is showing, and the workspace root it reports is the one workspace tool calls resolve
    /// against. Anything added here becomes visible to every panel holding <c>host.context.read</c>.
    /// </summary>
    PluginPanelContext IPluginPanelContextSource.CaptureContext()
    {
        var selectedAgent = ResolveSelectedAgent();
        return new PluginPanelContext(
            SelectedConversation?.Id.ToString("D"),
            selectedAgent.Id,
            selectedAgent.Name,
            ResolveComposerExecutionMode(selectedAgent.Mode).ToString().ToLowerInvariant(),
            _conversationSessions.IsSelectedRunning,
            SelectedWorkspaceRootPath,
            _selectedWorkspaceRoot?.Name);
    }

    private TranscriptProjectionRequest BuildTranscriptProjectionRequest(bool autoScroll)
    {
        var selectedAgent = ResolveSelectedAgent();
        var transcript = _conversationSessions.CaptureSelectedTranscript();
        var isBusy = _conversationSessions.IsSelectedRunning;
        var activityText = isBusy ? _conversationSessions.SelectedActivityText : null;
        return new TranscriptProjectionRequest(
            transcript.Messages,
            transcript.ToolRuns,
            GetNavigationConversations().ToArray(),
            _workspaceRoots,
            SelectedConversation?.Id,
            autoScroll,
            isBusy,
            activityText,
            ResolveComposerExecutionMode(selectedAgent.Mode).ToString().ToLowerInvariant(),
            selectedAgent.Id,
            selectedAgent.Name,
            _capabilityRevision,
            ToToolPermissionModeWire(_selectedToolPermissionMode));
    }

    Guid? SelfClaw.Desktop.Services.Activities.IActivityPanelScopeSource.CaptureActivityParent()
        => SelectedConversation is { Kind: ConversationKind.Interactive } parent ? parent.Id : null;

    private IEnumerable<ConversationRecord> GetNavigationConversations()
        => _allConversations.Where(MatchesNavigationConversation);

    private bool MatchesNavigationConversation(ConversationRecord conversation)
        => conversation.Mode == ConversationMode.Programming &&
           string.Equals(conversation.AgentId, ResolveSelectedAgent().Id, StringComparison.OrdinalIgnoreCase);

    #endregion

}






