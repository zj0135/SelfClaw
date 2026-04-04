using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Desktop.Services;
using SelfClaw.Infrastructure.Tools;

namespace SelfClaw.Desktop.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private static readonly ShellSelectOption[] ToolPermissionOptions =
    [
        new("requireApproval", "默认权限", "写文件和命令执行前需要人工确认"),
        new("fullAccess", "完全访问权限", "允许 agent 直接写文件并执行 PowerShell")
    ];

    private static readonly ShellSelectOption[] ConversationModeOptions =
    [
        new("team", "团队", "主 Agent 串行组织多个子 Agent 讨论并总结"),
        new("programming", "编程", "单 assistant 的工作区分析与编码助手")
    ];

    private static readonly ShellSelectOption[] TeamRoundOptions =
    [
        new("1", "1 轮", "最多 1 轮，由主 Agent 视情况提前收束"),
        new("2", "2 轮", "最多 2 轮，默认设置"),
        new("3", "3 轮", "最多 3 轮，适合中等复杂度讨论"),
        new("4", "4 轮", "最多 4 轮，留出更多互评空间"),
        new("5", "5 轮", "最多 5 轮，最充分但耗时更高")
    ];

    private static readonly ShellSelectOption[] TeamOutputModeOptions =
    [
        new("replyOnly", "仅聊天总结", "最终只在聊天里总结，不触发文档导出"),
        new("autoDocument", "自动判断", "主 Agent 认为有必要时再建议导出文档"),
        new("alwaysDocument", "始终文档", "按文档方式总结，并在可用时建议导出")
    ];

    private readonly IConversationRepository _conversationRepository;
    private readonly IProfileRepository _profileRepository;
    private readonly ISecretProtector _secretProtector;
    private readonly IAgentChatRuntime _agentChatRuntime;
    private readonly IWorkspaceToolService _workspaceToolService;
    private readonly DesktopToolApprovalHandler _toolApprovalHandler;
    private readonly MarkdownHtmlRenderer _markdownHtmlRenderer;
    private readonly DesktopSettingsStore _desktopSettingsStore;
    private readonly ILogger<MainWindowViewModel> _logger;

    private readonly List<ConversationRecord> _allConversations = [];
    private readonly List<MessageRecord> _messages = [];
    private readonly List<TeamAgentRecord> _teamAgents = [];
    private readonly List<ToolExecutionRecord> _toolRuns = [];
    private readonly Dictionary<Guid, ToolRunAnchor> _toolRunAnchors = [];
    private CancellationTokenSource? _turnCancellationSource;
    private bool _initialized;
    private bool _isApplyingThemeSelection;
    private int _selectionVersion;
    private ConversationRecord? _selectedConversation;
    private ProviderProfile? _selectedProfile;
    private WorkspaceRoot? _selectedWorkspaceRoot;
    private string _composerText = string.Empty;
    private string _statusText = "Add a model profile to get started.";
    private bool _isBusy;
    private ThemeOption? _selectedThemeOption;
    private ThemeMode _activeThemeMode = ThemeMode.System;
    private string _effectiveTranscriptTheme = "light";
    private ConversationMode _selectedConversationMode = ConversationMode.Programming;
    private ToolPermissionMode _selectedToolPermissionMode = ToolPermissionMode.RequireApproval;
    private int _selectedTeamMaxRounds = TeamDiscussionDefaults.DefaultMaxRounds;
    private TeamOutputMode _selectedTeamOutputMode = TeamDiscussionDefaults.DefaultOutputMode;

    public MainWindowViewModel(
        IConversationRepository conversationRepository,
        IProfileRepository profileRepository,
        ISecretProtector secretProtector,
        IAgentChatRuntime agentChatRuntime,
        IWorkspaceToolService workspaceToolService,
        DesktopToolApprovalHandler toolApprovalHandler,
        MarkdownHtmlRenderer markdownHtmlRenderer,
        DesktopSettingsStore desktopSettingsStore,
        ILogger<MainWindowViewModel> logger)
    {
        _conversationRepository = conversationRepository;
        _profileRepository = profileRepository;
        _secretProtector = secretProtector;
        _agentChatRuntime = agentChatRuntime;
        _workspaceToolService = workspaceToolService;
        _toolApprovalHandler = toolApprovalHandler;
        _markdownHtmlRenderer = markdownHtmlRenderer;
        _desktopSettingsStore = desktopSettingsStore;
        _logger = logger;

        ThemeOptions.Add(new ThemeOption(AppThemePreference.System, "System"));
        ThemeOptions.Add(new ThemeOption(AppThemePreference.Light, "Light"));
        ThemeOptions.Add(new ThemeOption(AppThemePreference.Dark, "Dark"));

        LoadThemePreference();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        SendCommand = new AsyncRelayCommand(SendAsync, CanSend);
        StopCommand = new RelayCommand(Stop, () => IsBusy);
        NewConversationCommand = new AsyncRelayCommand(CreateNewConversationAsync, CanCreateConversation);
    }

    public event EventHandler<TranscriptRenderState>? TranscriptChanged;

    public ObservableCollection<ConversationRecord> Conversations { get; } = [];

    public ObservableCollection<ProviderProfile> Profiles { get; } = [];

    public ObservableCollection<WorkspaceRoot> WorkspaceRoots { get; } = [];

    public ObservableCollection<AgentActivityNode> AgentActivityNodes { get; } = [];

    public ObservableCollection<ThemeOption> ThemeOptions { get; } = [];

    public IAsyncRelayCommand SendCommand { get; }

    public IRelayCommand StopCommand { get; }

    public IAsyncRelayCommand NewConversationCommand { get; }

    public bool HasAgentActivityNodes => AgentActivityNodes.Count > 0;

    public ThemeOption? SelectedThemeOption
    {
        get => _selectedThemeOption;
        set
        {
            if (!SetProperty(ref _selectedThemeOption, value))
            {
                return;
            }

            if (_isApplyingThemeSelection)
            {
                return;
            }

            ApplyThemePreference(value?.Value ?? AppThemePreference.System, persist: true, refreshShell: true);
        }
    }

    public ThemeMode ActiveThemeMode
    {
        get => _activeThemeMode;
        private set => SetProperty(ref _activeThemeMode, value);
    }

    public string EffectiveTranscriptTheme
    {
        get => _effectiveTranscriptTheme;
        private set => SetProperty(ref _effectiveTranscriptTheme, value);
    }

    public ConversationMode SelectedConversationMode
    {
        get => _selectedConversationMode;
        private set
        {
            if (SetProperty(ref _selectedConversationMode, value))
            {
                PublishShell(false);
            }
        }
    }

    public ToolPermissionMode SelectedToolPermissionMode
    {
        get => _selectedToolPermissionMode;
        private set
        {
            if (SetProperty(ref _selectedToolPermissionMode, value))
            {
                PublishShell(false);
            }
        }
    }

    public int SelectedTeamMaxRounds
    {
        get => _selectedTeamMaxRounds;
        private set
        {
            var normalized = TeamDiscussionDefaults.ClampRounds(value);
            if (SetProperty(ref _selectedTeamMaxRounds, normalized))
            {
                PublishShell(false);
            }
        }
    }

    public TeamOutputMode SelectedTeamOutputMode
    {
        get => _selectedTeamOutputMode;
        private set
        {
            if (SetProperty(ref _selectedTeamOutputMode, value))
            {
                PublishShell(false);
            }
        }
    }

    public ConversationRecord? SelectedConversation
    {
        get => _selectedConversation;
        set
        {
            if (!SetProperty(ref _selectedConversation, value))
            {
                return;
            }

            if (value is not null)
            {
                _ = LoadConversationAsync(value);
            }
            else
            {
                _messages.Clear();
                _teamAgents.Clear();
                _toolRuns.Clear();
                _toolRunAnchors.Clear();
                PublishAgentActivities();
                PublishShell(false);
            }
        }
    }

    public ProviderProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (SetProperty(ref _selectedProfile, value))
            {
                NotifyCommandStates();
                PublishShell(false);
            }
        }
    }

    public WorkspaceRoot? SelectedWorkspaceRoot
    {
        get => _selectedWorkspaceRoot;
        set
        {
            if (SetProperty(ref _selectedWorkspaceRoot, value))
            {
                PublishShell(false);
            }
        }
    }

    public string ComposerText
    {
        get => _composerText;
        set
        {
            if (SetProperty(ref _composerText, value))
            {
                NotifyCommandStates();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (SetProperty(ref _statusText, value))
            {
                PublishShell(false);
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                NotifyCommandStates();
                PublishShell(false);
            }
        }
    }

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        await ReloadProfilesAsync();
        await ReloadWorkspaceRootsAsync();
        await ReloadConversationsAsync();

        if (SelectedProfile is null && Profiles.Count > 0)
        {
            SelectedProfile = Profiles[0];
        }

        if (SelectedConversation is null && SelectedProfile is not null)
        {
            await CreateNewConversationAsync();
        }

        PublishAgentActivities();
        PublishShell(false);
    }

    public async Task SubmitPromptAsync(string prompt)
    {
        ComposerText = prompt;
        await SendAsync();
    }

    public async Task CreateNewConversationFromUiAsync()
    {
        await CreateNewConversationAsync();
    }

    public void StopGeneration()
    {
        Stop();
    }

    public Task ApproveToolExecutionAsync(Guid toolExecutionId)
    {
        if (!_toolApprovalHandler.TryResolve(toolExecutionId, approved: true))
        {
            StatusText = "This approval request is no longer pending.";
        }

        return Task.CompletedTask;
    }

    public Task RejectToolExecutionAsync(Guid toolExecutionId)
    {
        if (!_toolApprovalHandler.TryResolve(toolExecutionId, approved: false))
        {
            StatusText = "This approval request is no longer pending.";
        }

        return Task.CompletedTask;
    }

    public Task SetThemePreferenceAsync(string? themeId)
    {
        ApplyThemePreference(ParseThemePreference(themeId), persist: true, refreshShell: true);
        return Task.CompletedTask;
    }

    public Task SetSelectedProfileAsync(Guid profileId)
    {
        SelectedProfile = Profiles.FirstOrDefault(item => item.Id == profileId) ?? SelectedProfile;
        return Task.CompletedTask;
    }

    public Task SetSelectedWorkspaceRootAsync(Guid? workspaceRootId)
    {
        SelectedWorkspaceRoot = workspaceRootId is Guid id
            ? WorkspaceRoots.FirstOrDefault(item => item.Id == id)
            : null;

        ApplyConversationFilter();
        PublishShell(false);
        return Task.CompletedTask;
    }

    public Task SetConversationModeAsync(string? modeId)
        => SetConversationModeCoreAsync(ParseConversationMode(modeId));

    public async Task DeleteConversationAsync(Guid conversationId)
    {
        var conversation = _allConversations.FirstOrDefault(item => item.Id == conversationId);
        if (conversation is null)
        {
            return;
        }

        await _conversationRepository.DeleteConversationAsync(conversationId);
        _allConversations.RemoveAll(item => item.Id == conversationId);

        if (SelectedConversation?.Id == conversationId)
        {
            SelectedConversation = null;
        }

        ApplyConversationFilter();
        StatusText = $"Deleted conversation '{conversation.Title}'.";
    }

    public async Task SetToolPermissionModeAsync(string? permissionModeId)
    {
        var nextMode = ParseToolPermissionMode(permissionModeId);
        if (SelectedToolPermissionMode == nextMode)
        {
            return;
        }

        SelectedToolPermissionMode = nextMode;

        if (SelectedConversation is not null)
        {
            await SaveConversationSelectionAsync(SelectedConversation);
        }
    }

    public async Task SaveProfileAsync(ProfileEditorResult result)
    {
        var existing = result.ProfileId is Guid profileId
            ? await _profileRepository.GetProfileAsync(profileId)
            : null;

        if (existing is null && string.IsNullOrWhiteSpace(result.ApiKey))
        {
            throw new InvalidOperationException("A new profile requires an API key.");
        }

        var secretRef = existing?.SecretRef ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(result.ApiKey))
        {
            secretRef = await _secretProtector.StoreSecretAsync(result.ApiKey.Trim(), existing?.SecretRef);
        }

        var now = DateTimeOffset.UtcNow;
        var profile = new ProviderProfile(
            existing?.Id ?? Guid.NewGuid(),
            result.Name.Trim(),
            NormalizeEndpoint(result.Endpoint),
            result.Model.Trim(),
            ApiStyle.OpenAICompatible,
            secretRef,
            existing?.CreatedAtUtc ?? now,
            now);

        await _profileRepository.UpsertProfileAsync(profile);
        await ReloadProfilesAsync();
        SelectedProfile = Profiles.FirstOrDefault(item => item.Id == profile.Id);
        StatusText = $"Saved profile '{profile.Name}'.";

        if (SelectedConversation is null && SelectedProfile is not null)
        {
            await CreateNewConversationAsync();
        }
    }

    public Task SelectWorkspaceAsync(string folderPath)
        => SaveWorkspaceRootAsync(null, folderPath, null);

    public async Task SaveWorkspaceRootAsync(Guid? workspaceRootId, string folderPath, string? workspaceName)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            throw new InvalidOperationException("Workspace path is required.");
        }

        var normalizedPath = NormalizeWorkspacePath(folderPath);
        if (!Directory.Exists(normalizedPath))
        {
            throw new DirectoryNotFoundException($"Workspace path '{normalizedPath}' does not exist.");
        }

        var existingById = workspaceRootId is Guid id
            ? WorkspaceRoots.FirstOrDefault(root => root.Id == id)
            : null;
        var existingByPath = WorkspaceRoots.FirstOrDefault(root =>
            string.Equals(NormalizeWorkspacePath(root.RootPath), normalizedPath, StringComparison.OrdinalIgnoreCase));
        var existing = existingByPath ?? existingById;

        var now = DateTimeOffset.UtcNow;
        var name = ResolveWorkspaceName(workspaceName, normalizedPath);
        var workspaceRoot = existing is null
            ? new WorkspaceRoot(Guid.NewGuid(), name, normalizedPath, now, now)
            : existing with
            {
                Name = name,
                RootPath = normalizedPath,
                UpdatedAtUtc = now
            };

        await _conversationRepository.UpsertWorkspaceRootAsync(workspaceRoot);
        await ReloadWorkspaceRootsAsync();
        SelectedWorkspaceRoot = WorkspaceRoots.FirstOrDefault(root => root.Id == workspaceRoot.Id);
        StatusText = $"Workspace set to '{workspaceRoot.Name}'.";

        ApplyConversationFilter();
    }

    public Task SelectConversationAsync(Guid conversationId)
    {
        var conversation = Conversations.FirstOrDefault(item => item.Id == conversationId);
        if (conversation is null || SelectedConversation?.Id == conversation.Id)
        {
            return Task.CompletedTask;
        }

        SelectedConversation = conversation;
        return Task.CompletedTask;
    }

    private void LoadThemePreference()
    {
        var settings = _desktopSettingsStore.Load();
        SetSelectedThemeOption(settings.ThemePreference);
        ApplyThemePreference(settings.ThemePreference, persist: false, refreshShell: false);
    }

    private void ApplyThemePreference(AppThemePreference preference, bool persist, bool refreshShell)
    {
        var themeMode = preference switch
        {
            AppThemePreference.Light => ThemeMode.Light,
            AppThemePreference.Dark => ThemeMode.Dark,
            _ => ThemeMode.System
        };

        ActiveThemeMode = themeMode;
        EffectiveTranscriptTheme = ResolveTranscriptTheme(preference);
        ApplyThemeModeToApplication(themeMode);
        SetSelectedThemeOption(preference);

        if (persist)
        {
            _desktopSettingsStore.Save(new DesktopSettings(preference));
        }

        if (_initialized || refreshShell)
        {
            PublishShell(false);
        }
    }

    private void SetSelectedThemeOption(AppThemePreference preference)
    {
        _isApplyingThemeSelection = true;
        try
        {
            SelectedThemeOption = ThemeOptions.FirstOrDefault(option => option.Value == preference) ?? ThemeOptions.FirstOrDefault();
        }
        finally
        {
            _isApplyingThemeSelection = false;
        }
    }

    private static string ResolveTranscriptTheme(AppThemePreference preference)
        => preference switch
        {
            AppThemePreference.Dark => "dark",
            AppThemePreference.Light => "light",
            _ => SystemThemeReader.IsDarkModeEnabled() ? "dark" : "light"
        };

    private static void ApplyThemeModeToApplication(ThemeMode mode)
    {
        if (Application.Current is { } app)
        {
            app.ThemeMode = mode;
        }
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if ((SelectedThemeOption?.Value ?? AppThemePreference.System) != AppThemePreference.System)
        {
            return;
        }

        Application.Current?.Dispatcher.Invoke(() =>
        {
            ApplyThemePreference(AppThemePreference.System, persist: false, refreshShell: true);
        });
    }

    private async Task ReloadProfilesAsync()
    {
        var selectedId = SelectedProfile?.Id;
        var profiles = await _profileRepository.ListProfilesAsync();
        ReplaceCollection(Profiles, profiles);
        SelectedProfile = profiles.FirstOrDefault(profile => profile.Id == selectedId) ?? profiles.FirstOrDefault();
        NotifyCommandStates();
        if (SelectedProfile is not null)
        {
            StatusText = "Ready.";
        }
    }

    private async Task ReloadWorkspaceRootsAsync()
    {
        var selectedId = SelectedWorkspaceRoot?.Id;
        var workspaceRoots = await _conversationRepository.ListWorkspaceRootsAsync();
        ReplaceCollection(WorkspaceRoots, workspaceRoots);
        SelectedWorkspaceRoot = workspaceRoots.FirstOrDefault(root => root.Id == selectedId) ?? workspaceRoots.FirstOrDefault();
    }

    private async Task ReloadConversationsAsync()
    {
        var selectedId = SelectedConversation?.Id;
        var conversations = await _conversationRepository.ListConversationsAsync();
        _allConversations.Clear();
        _allConversations.AddRange(conversations);
        ApplyConversationFilter(selectedId);
    }

    private async Task CreateNewConversationAsync()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var conversation = new ConversationRecord(
            Guid.NewGuid(),
            "New chat",
            SelectedProfile.Id,
            SelectedWorkspaceRoot?.Id,
            SelectedConversationMode,
            SelectedToolPermissionMode,
            SelectedTeamMaxRounds,
            SelectedTeamOutputMode,
            now,
            now);

        await _conversationRepository.UpsertConversationAsync(conversation);
        UpsertConversation(conversation);
        ApplyConversationFilter(conversation.Id);
        StatusText = "Started a new chat.";
        PublishShell(false);
    }

    private async Task LoadConversationAsync(ConversationRecord conversation)
    {
        var version = ++_selectionVersion;
        var messages = await _conversationRepository.ListMessagesAsync(conversation.Id);
        var teamAgents = await _conversationRepository.ListTeamAgentsAsync(conversation.Id);
        var toolRuns = await _conversationRepository.ListToolExecutionsAsync(conversation.Id);
        if (version != _selectionVersion)
        {
            return;
        }

        _messages.Clear();
        _messages.AddRange(messages);
        _teamAgents.Clear();
        _teamAgents.AddRange(teamAgents);
        _toolRuns.Clear();
        _toolRuns.AddRange(toolRuns);
        _toolRunAnchors.Clear();
        foreach (var toolRun in toolRuns)
        {
            if (toolRun.MessageId is Guid messageId && toolRun.AfterSegmentIndex is int afterSegmentIndex)
            {
                _toolRunAnchors[toolRun.Id] = new ToolRunAnchor(messageId, afterSegmentIndex);
            }
        }

        SelectedProfile = Profiles.FirstOrDefault(profile => profile.Id == conversation.ProfileId) ?? SelectedProfile;
        SelectedWorkspaceRoot = conversation.WorkspaceRootId is Guid workspaceRootId
            ? WorkspaceRoots.FirstOrDefault(root => root.Id == workspaceRootId)
            : null;
        SelectedConversationMode = conversation.Mode;
        SelectedToolPermissionMode = conversation.ToolPermissionMode;
        SelectedTeamMaxRounds = conversation.TeamMaxRounds;
        SelectedTeamOutputMode = conversation.TeamOutputMode;
        ApplyConversationFilter(conversation.Id);

        PublishAgentActivities();
        PublishShell(false);
    }

    private async Task SendAsync()
    {
        if (SelectedProfile is null)
        {
            StatusText = "Create a profile first.";
            return;
        }

        var prompt = ComposerText.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return;
        }

        IsBusy = true;
        ComposerText = string.Empty;
        StatusText = "Streaming response...";
        _turnCancellationSource = new CancellationTokenSource();
        var activeMessageIds = new HashSet<Guid>();
        TeamDocumentReadyEvent? pendingTeamDocument = null;

        try
        {
            var conversation = await EnsureConversationAsync();
            conversation = conversation with
            {
                ProfileId = SelectedProfile.Id,
                WorkspaceRootId = SelectedWorkspaceRoot?.Id,
                Mode = SelectedConversationMode,
                ToolPermissionMode = SelectedToolPermissionMode,
                TeamMaxRounds = SelectedTeamMaxRounds,
                TeamOutputMode = SelectedTeamOutputMode,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            await PersistConversationAsync(conversation);

            var userMessage = new MessageRecord(
                Guid.NewGuid(),
                conversation.Id,
                MessageRole.User,
                prompt,
                MessageStatus.Completed,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow);
            _messages.Add(userMessage);
            await _conversationRepository.UpsertMessageAsync(userMessage);

            if (conversation.Title == "New chat")
            {
                conversation = conversation with { Title = CreateConversationTitle(prompt), UpdatedAtUtc = DateTimeOffset.UtcNow };
                await PersistConversationAsync(conversation);
            }
            PublishShell(true);

            var apiKey = await _secretProtector.RetrieveSecretAsync(SelectedProfile.SecretRef, _turnCancellationSource.Token);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException("The selected profile does not have a readable API key.");
            }

            var requestMessages = _messages.ToArray();
            var requestTeamAgents = _teamAgents.ToArray();

            await foreach (var update in _agentChatRuntime.StreamTurnAsync(
                               new ChatTurnRequest(
                                   conversation.Id,
                                   SelectedProfile,
                                   apiKey,
                                   SelectedWorkspaceRoot,
                                   SelectedConversationMode,
                                   SelectedToolPermissionMode,
                                   SelectedTeamMaxRounds,
                                   SelectedTeamOutputMode,
                                   _toolApprovalHandler,
                                   requestMessages,
                                   requestTeamAgents),
                               _turnCancellationSource.Token))
            {
                switch (update)
                {
                    case AssistantMessageStartedEvent started:
                        activeMessageIds.Add(started.Message.Id);
                        ReplaceMessage(started.Message);
                        PublishShell(true);
                        break;
                    case AssistantDeltaEvent delta:
                        ApplyAssistantDelta(delta.MessageId, delta.DeltaMarkdown);
                        break;
                    case ToolExecutionStartedEvent toolStarted:
                        var startedRecord = CaptureToolRunAnchor(toolStarted.Record);
                        UpsertToolRun(startedRecord);
                        await _conversationRepository.UpsertToolExecutionAsync(startedRecord);
                        PublishAgentActivities();
                        break;
                    case ToolExecutionCompletedEvent toolCompleted:
                        var completedRecord = CaptureToolRunAnchor(toolCompleted.Record);
                        UpsertToolRun(completedRecord);
                        await _conversationRepository.UpsertToolExecutionAsync(completedRecord);
                        PublishAgentActivities();
                        break;
                    case AssistantMessageCompletedEvent completed:
                        activeMessageIds.Remove(completed.Message.Id);
                        await CompleteAssistantMessageAsync(completed.Message);
                        break;
                    case TeamAgentsPlannedEvent planned:
                        await UpsertTeamAgentsAsync(planned.Agents);
                        break;
                    case TeamAgentStatusChangedEvent statusChanged:
                        await UpdateTeamAgentStatusAsync(statusChanged.AgentId, statusChanged.Status);
                        break;
                    case TeamDocumentReadyEvent documentReady:
                        pendingTeamDocument = documentReady;
                        break;
                }
            }

            if (pendingTeamDocument is not null)
            {
                await FinalizeTeamDocumentExportAsync(conversation, pendingTeamDocument, _turnCancellationSource.Token);
            }

            StatusText = "Ready.";
            PublishShell(true);
        }
        catch (OperationCanceledException)
        {
            await FailActiveMessagesAsync(activeMessageIds, "Generation stopped.");
            StatusText = "Generation stopped.";
            PublishShell(true);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Chat turn failed.");
            await FailActiveMessagesAsync(activeMessageIds, exception.Message);
            StatusText = exception.Message;
            PublishShell(true);
        }
        finally
        {
            _turnCancellationSource?.Dispose();
            _turnCancellationSource = null;
            IsBusy = false;
        }
    }

    private void Stop() => _turnCancellationSource?.Cancel();

    private bool CanSend()
        => !IsBusy && SelectedProfile is not null && !string.IsNullOrWhiteSpace(ComposerText);

    private bool CanCreateConversation()
        => !IsBusy && SelectedProfile is not null;

    private void NotifyCommandStates()
    {
        SendCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        NewConversationCommand.NotifyCanExecuteChanged();
    }

    private async Task<ConversationRecord> EnsureConversationAsync()
    {
        if (SelectedConversation is not null)
        {
            return SelectedConversation;
        }

        await CreateNewConversationAsync();
        return SelectedConversation!;
    }

    private async Task PersistConversationAsync(ConversationRecord conversation)
    {
        await _conversationRepository.UpsertConversationAsync(conversation);
        UpsertConversation(conversation);
        _selectedConversation = conversation;
        OnPropertyChanged(nameof(SelectedConversation));
        PublishShell(false);
    }

    private void UpsertConversation(ConversationRecord conversation)
    {
        var existing = _allConversations.FirstOrDefault(item => item.Id == conversation.Id);
        if (existing is not null)
        {
            _allConversations.Remove(existing);
        }

        _allConversations.Insert(0, conversation);
        ApplyConversationFilter(conversation.Id);
    }

    private async Task SaveConversationSelectionAsync(ConversationRecord conversation)
    {
        var updated = conversation with
        {
            ProfileId = SelectedProfile?.Id ?? conversation.ProfileId,
            WorkspaceRootId = SelectedWorkspaceRoot?.Id,
            Mode = SelectedConversationMode,
            ToolPermissionMode = SelectedToolPermissionMode,
            TeamMaxRounds = SelectedTeamMaxRounds,
            TeamOutputMode = SelectedTeamOutputMode,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        await PersistConversationAsync(updated);
    }

    private void ReplaceMessage(MessageRecord message)
    {
        var index = _messages.FindIndex(item => item.Id == message.Id);
        if (index >= 0)
        {
            _messages[index] = message;
        }
        else
        {
            _messages.Add(message);
        }
    }

    private void ApplyConversationFilter(Guid? preferredConversationId = null)
    {
        var filtered = GetFilteredConversations().ToArray();
        ReplaceCollection(Conversations, filtered);

        var targetConversation = filtered.FirstOrDefault(item => item.Id == preferredConversationId)
            ?? filtered.FirstOrDefault(item => item.Id == SelectedConversation?.Id)
            ?? filtered.FirstOrDefault();

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
        if (conversation.Mode != SelectedConversationMode)
        {
            return false;
        }

        return SelectedWorkspaceRoot is null || conversation.WorkspaceRootId == SelectedWorkspaceRoot.Id;
    }

    private void UpsertToolRun(ToolExecutionRecord record)
    {
        var index = _toolRuns.FindIndex(item => item.Id == record.Id);
        if (index >= 0)
        {
            _toolRuns[index] = record;
        }
        else
        {
            _toolRuns.Add(record);
        }
    }

    private void PublishShell(bool autoScroll)
    {
        var toolRunsByMessageId = TranscriptToolRunPresenter.BuildToolRunsByMessageId(_messages, _toolRuns, _toolRunAnchors);
        var orderedItems = _messages
            .OrderBy(item => item.CreatedAtUtc)
            .Select(item => BuildMessageItem(
                item,
                toolRunsByMessageId.TryGetValue(item.Id, out var toolRuns) ? toolRuns : []))
            .ToArray();

        var conversations = Conversations
            .Select(BuildConversationItem)
            .ToArray();

        var conversationModes = ConversationModeOptions;

        var profiles = Profiles
            .Select(profile => new ShellSelectOption(profile.Id.ToString("D"), profile.Name, profile.Endpoint))
            .ToArray();

        var workspaceRoots = WorkspaceRoots
            .Select(root => new ShellSelectOption(root.Id.ToString("D"), root.Name, root.RootPath))
            .ToArray();

        var toolPermissionModes = ToolPermissionOptions;
        var teamRoundModes = TeamRoundOptions;
        var teamOutputModes = TeamOutputModeOptions;
        var teamMembers = SelectedConversationMode == ConversationMode.Team
            ? _teamAgents
                .OrderBy(item => item.SortOrder)
                .ThenBy(item => item.CreatedAtUtc)
                .Select(BuildTeamMemberActivityNode)
                .ToArray()
            : [];

        var themeOptions = ThemeOptions
            .Select(option => new ShellSelectOption(ThemePreferenceToId(option.Value), option.Label))
            .ToArray();

        TranscriptChanged?.Invoke(this, new TranscriptRenderState(
            orderedItems,
            autoScroll,
            conversations,
            SelectedConversation?.Id.ToString("D"),
            EffectiveTranscriptTheme,
            conversationModes,
            ConversationModeToId(SelectedConversationMode),
            profiles,
            SelectedProfile?.Id.ToString("D"),
            SelectedProfile?.Model,
            workspaceRoots,
            SelectedWorkspaceRoot?.Id.ToString("D"),
            toolPermissionModes,
            ToolPermissionModeToId(SelectedToolPermissionMode),
            teamRoundModes,
            SelectedTeamMaxRounds.ToString(CultureInfo.InvariantCulture),
            teamOutputModes,
            TeamOutputModeToId(SelectedTeamOutputMode),
            themeOptions,
            ThemePreferenceToId(SelectedThemeOption?.Value ?? AppThemePreference.System),
            teamMembers,
            AgentActivityNodes.ToArray(),
            StatusText,
            IsBusy));
    }

    private TranscriptConversationItem BuildConversationItem(ConversationRecord conversation)
        => new(
            conversation.Id.ToString("D"),
            conversation.Title,
            conversation.UpdatedAtUtc.LocalDateTime.ToString("yyyy-MM-dd HH:mm"),
            SelectedConversation?.Id == conversation.Id);

    private void PublishAgentActivities()
    {
        var teamAgentItems = SelectedConversationMode == ConversationMode.Team
            ? _teamAgents
                .Select(item => (Timestamp: item.UpdatedAtUtc, Node: BuildTeamAgentEventNode(item)))
            : [];

        var toolItems = _toolRuns
            .Select(item => (Timestamp: item.UpdatedAtUtc, Node: TranscriptToolRunPresenter.BuildActivityNode(item)));

        var items = teamAgentItems
            .Concat(toolItems)
            .OrderByDescending(item => item.Timestamp)
            .ThenBy(item => item.Node.Title, StringComparer.Ordinal)
            .Select(item => item.Node)
            .ToArray();

        ReplaceCollection(AgentActivityNodes, items);
        OnPropertyChanged(nameof(HasAgentActivityNodes));
        PublishShell(false);
    }

    private TranscriptRenderItem BuildMessageItem(MessageRecord message)
        => BuildMessageItem(message, []);

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

        if (message.Status == MessageStatus.Failed && !string.IsNullOrWhiteSpace(message.ErrorMessage))
        {
            var errorHtml = $"<p class=\"message-error\">{WebUtility.HtmlEncode(message.ErrorMessage)}</p>";

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
            message.CreatedAtUtc.LocalDateTime.ToString("yyyy-MM-dd HH:mm"));
    }

    private static string ThemePreferenceToId(AppThemePreference preference)
        => preference.ToString().ToLowerInvariant();

    private static AppThemePreference ParseThemePreference(string? themeId)
        => themeId?.Trim().ToLowerInvariant() switch
        {
            "light" => AppThemePreference.Light,
            "dark" => AppThemePreference.Dark,
            _ => AppThemePreference.System
        };

    private static string ToolPermissionModeToId(ToolPermissionMode mode)
        => mode switch
        {
            ToolPermissionMode.FullAccess => "fullAccess",
            _ => "requireApproval"
        };

    private static ToolPermissionMode ParseToolPermissionMode(string? permissionModeId)
        => permissionModeId?.Trim().ToLowerInvariant() switch
        {
            "fullaccess" => ToolPermissionMode.FullAccess,
            _ => ToolPermissionMode.RequireApproval
        };

    private static string CreateConversationTitle(string text)
    {
        var normalized = text.ReplaceLineEndings(" ").Trim();
        return normalized.Length > 48 ? normalized[..48] + "..." : normalized;
    }

    private static string NormalizeEndpoint(string endpoint)
    {
        var normalized = endpoint.Trim();
        if (!normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "https://" + normalized;
        }

        return normalized.TrimEnd('/');
    }

    private static string NormalizeWorkspacePath(string folderPath)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath.Trim()));

    private static string ResolveWorkspaceName(string? workspaceName, string normalizedPath)
    {
        var explicitName = workspaceName?.Trim();
        if (!string.IsNullOrWhiteSpace(explicitName))
        {
            return explicitName;
        }

        var fallbackName = Path.GetFileName(normalizedPath);
        return string.IsNullOrWhiteSpace(fallbackName) ? normalizedPath : fallbackName;
    }

    private static void ReplaceCollection<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }
    }
}



