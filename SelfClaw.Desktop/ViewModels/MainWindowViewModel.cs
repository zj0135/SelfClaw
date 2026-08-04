using System.IO;
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

public sealed partial class MainWindowViewModel : ObservableObject, IWorkspaceSelectionController
{
    #region 字段与构造函数 —— 依赖注入字段、运行时集合状态、流式发布定时器初始化

    private static readonly TimeSpan ConversationDeleteStopTimeout = TimeSpan.FromSeconds(8);
    private readonly IConversationRepository _conversationRepository;
    private readonly ConversationTurnEngine _turnEngine;
    private readonly ConversationSessionCoordinator _conversationSessions;
    private readonly AgentActivityCoordinator _agentActivityCoordinator;
    private readonly TranscriptPublisher _transcriptPublisher;
    private readonly DesktopAgentDefinitionService _desktopAgentDefinitionService;
    private readonly DesktopSettingsJsonStore _settingsStore;
    private readonly ILogger<MainWindowViewModel> _logger;

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
    private long _capabilityRevision;

    internal MainWindowViewModel(
        IConversationRepository conversationRepository,
        ConversationTurnEngine turnEngine,
        ConversationSessionCoordinator conversationSessions,
        AgentActivityCoordinator agentActivityCoordinator,
        TranscriptPublisher transcriptPublisher,
        DesktopAgentDefinitionService desktopAgentDefinitionService,
        DesktopSettingsJsonStore settingsStore,
        ILogger<MainWindowViewModel> logger)
    {
        _conversationRepository = conversationRepository;
        _turnEngine = turnEngine;
        _conversationSessions = conversationSessions;
        _agentActivityCoordinator = agentActivityCoordinator;
        _transcriptPublisher = transcriptPublisher;
        _desktopAgentDefinitionService = desktopAgentDefinitionService;
        _settingsStore = settingsStore;
        _logger = logger;
        _transcriptPublisher.Attach(BuildTranscriptProjectionRequest);
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
    /// WebView 的 "send-prompt" 消息经宿主路由后落到这里，触发一次发送回合。
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
    /// The turn engine observes the cancellation and persists the cancelled terminal state.
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

    #region 回合意图与释放

    private async Task SendAsync(PromptSubmissionSnapshot submission)
    {
        try
        {
            if (submission.SelectionVersion != _selectionVersion)
            {
                return;
            }

            var conversation = submission.Conversation;
            var preferConversationSelection = SelectedConversation is null ||
                                               conversation is not null &&
                                               _conversationSessions.IsSelected(conversation.Id);
            var runtimeAgent = ResolveRuntimeAgent(conversation?.AgentId ?? submission.AgentId);
            runtimeAgent = runtimeAgent with { Mode = submission.ExecutionModeOverride ?? runtimeAgent.Mode };
            var admission = await _turnEngine.TryAdmitAsync(new DesktopConversationTurnRequest(
                conversation,
                runtimeAgent,
                submission.Prompt,
                submission.ModelProfileId,
                submission.WorkspaceRoot,
                submission.ToolPermissionMode));
            if (admission is null)
            {
                return;
            }

            ApplyAdmittedConversation(admission.Conversation, preferConversationSelection);
            await _turnEngine.ExecuteAsync(admission);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to prepare the chat turn.");
        }
    }

    #endregion

    #region 会话导航状态与过滤

    private void ApplyAdmittedConversation(ConversationRecord conversation, bool preferSelection)
    {
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

    #region Transcript 发布

    private void PublishShell(bool autoScroll)
        => _transcriptPublisher.PublishNow(autoScroll);

    private TranscriptProjectionRequest BuildTranscriptProjectionRequest(bool autoScroll)
    {
        var selectedAgent = ResolveSelectedAgent();
        var isBusy = _conversationSessions.IsSelectedRunning;
        var activityText = isBusy ? _conversationSessions.SelectedActivityText : null;
        return new TranscriptProjectionRequest(
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
            _capabilityRevision);
    }

    private IEnumerable<ConversationRecord> GetNavigationConversations()
        => _allConversations.Where(MatchesNavigationConversation);

    private bool MatchesNavigationConversation(ConversationRecord conversation)
        => conversation.Mode == ConversationMode.Programming &&
           string.Equals(conversation.AgentId, ResolveSelectedAgent().Id, StringComparison.OrdinalIgnoreCase);

    #endregion

}






