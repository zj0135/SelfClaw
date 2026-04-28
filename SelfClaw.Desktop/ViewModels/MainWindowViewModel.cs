using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Desktop.Services;
using SelfClaw.Infrastructure.Options;
using SelfClaw.Infrastructure.Tools.Transcript;

namespace SelfClaw.Desktop.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan StreamingPublishInterval = TimeSpan.FromMilliseconds(75);
    private const int MaxPromptImageAttachments = 6;
    private const long MaxPromptImageBytes = 10 * 1024 * 1024;
    private const long MaxPromptImageTotalBytes = 30 * 1024 * 1024;
    private const int ContextUsageMessageOverheadTokens = 4;
    private const int ContextUsageImageMetadataTokens = 32;
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
    private readonly IConversationContextCompactionService _contextCompactionService;
    private readonly IWorkspaceToolService _workspaceToolService;
    private readonly DesktopToolApprovalHandler _toolApprovalHandler;
    private readonly DesktopNotificationService _desktopNotificationService;
    private readonly MarkdownHtmlRenderer _markdownHtmlRenderer;
    private readonly DesktopSettingsStore _desktopSettingsStore;
    private readonly DesktopChannelManager _channelManager;
    private readonly StoragePaths _storagePaths;
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly DispatcherTimer? _streamingPublishTimer;

    private readonly List<ConversationRecord> _allConversations = [];
    private readonly List<MessageRecord> _messages = [];
    private readonly List<MessageRecord> _contextMessages = [];
    private readonly List<TeamAgentRecord> _teamAgents = [];
    private readonly List<ToolExecutionRecord> _toolRuns = [];
    private readonly Dictionary<Guid, ToolRunAnchor> _toolRunAnchors = [];
    private IReadOnlyList<PromptImageAttachment> _pendingPromptImageAttachments = [];
    private bool _pendingReasoningEnabled;
    private CancellationTokenSource? _turnCancellationSource;
    private bool _initialized;
    private bool _isApplyingThemeSelection;
    private int _selectionVersion;
    private ConversationRecord? _selectedConversation;
    private ProviderProfile? _selectedProfile;
    private string? _selectedProfileModelOverride;
    private WorkspaceRoot? _selectedWorkspaceRoot;
    private string _composerText = string.Empty;
    private string _statusText = "Add a model profile to get started.";
    private bool _isBusy;
    private DesktopSettings _desktopSettings = DesktopSettings.Default;
    private ThemeOption? _selectedThemeOption;
    private ThemeMode _activeThemeMode = ThemeMode.System;
    private string _effectiveTranscriptTheme = "light";
    private bool _isPlanningModeEnabled;
    private TranscriptPlanPanel? _planPanel;
    private ConversationMode _selectedConversationMode = ConversationMode.Programming;
    private ToolPermissionMode _selectedToolPermissionMode = ToolPermissionMode.RequireApproval;
    private int _selectedTeamMaxRounds = TeamDiscussionDefaults.DefaultMaxRounds;
    private TeamOutputMode _selectedTeamOutputMode = TeamDiscussionDefaults.DefaultOutputMode;
    private TeamAgentRecord? _selectedBoundAgent;
    private bool _pendingStreamingPublish;
    private bool _pendingStreamingAutoScroll;
    private DateTimeOffset _lastStreamingPublishAtUtc = DateTimeOffset.MinValue;
    private int _disposeStarted;

    public MainWindowViewModel(
        IConversationRepository conversationRepository,
        IProfileRepository profileRepository,
        ISecretProtector secretProtector,
        IAgentChatRuntime agentChatRuntime,
        IConversationContextCompactionService contextCompactionService,
        IWorkspaceToolService workspaceToolService,
        DesktopToolApprovalHandler toolApprovalHandler,
        DesktopNotificationService desktopNotificationService,
        MarkdownHtmlRenderer markdownHtmlRenderer,
        DesktopSettingsStore desktopSettingsStore,
        DesktopChannelManager channelManager,
        StoragePaths storagePaths,
        ILogger<MainWindowViewModel> logger)
    {
        _conversationRepository = conversationRepository;
        _profileRepository = profileRepository;
        _secretProtector = secretProtector;
        _agentChatRuntime = agentChatRuntime;
        _contextCompactionService = contextCompactionService;
        _workspaceToolService = workspaceToolService;
        _toolApprovalHandler = toolApprovalHandler;
        _desktopNotificationService = desktopNotificationService;
        _markdownHtmlRenderer = markdownHtmlRenderer;
        _desktopSettingsStore = desktopSettingsStore;
        _channelManager = channelManager;
        _storagePaths = storagePaths;
        _logger = logger;
        _channelManager.Changed += OnChannelManagerChanged;
        _toolApprovalHandler.ApprovalRequested += OnToolApprovalRequested;
        if (System.Windows.Application.Current?.Dispatcher is Dispatcher dispatcher)
        {
            _streamingPublishTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
            {
                Interval = StreamingPublishInterval
            };
            _streamingPublishTimer.Tick += OnStreamingPublishTimerTick;
        }

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

    public bool IsPlanningModeEnabled
    {
        get => _isPlanningModeEnabled;
        private set
        {
            if (SetProperty(ref _isPlanningModeEnabled, value))
            {
                PublishShell(false);
            }
        }
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

            ClearPlanPanelState(publishShell: false);

            if (value is not null)
            {
                PublishShell(false);
                _ = LoadConversationAsync(value);
            }
            else
            {
                _messages.Clear();
                _contextMessages.Clear();
                _teamAgents.Clear();
                _toolRuns.Clear();
                _toolRunAnchors.Clear();
                _selectedBoundAgent = null;
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
                _selectedProfileModelOverride = null;
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
        await _channelManager.InitializeAsync();

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

    public async Task SubmitPromptAsync(
        string prompt,
        IReadOnlyList<PromptImageAttachment>? imageAttachments = null,
        bool enableReasoning = false,
        string? profileModel = null)
    {
        ApplySelectedProfileModel(profileModel, publishShell: false);
        ComposerText = prompt;
        _pendingPromptImageAttachments = imageAttachments ?? [];
        _pendingReasoningEnabled = enableReasoning;
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

    public async Task SetSelectedProfileModelAsync(string? profileModel)
    {
        if (SelectedProfile is null)
        {
            return;
        }

        var normalized = NormalizeModelValue(profileModel);
        if (normalized is null)
        {
            ApplySelectedProfileModel(null, publishShell: true);
            return;
        }

        if (string.Equals(SelectedProfile.Model, normalized, StringComparison.Ordinal))
        {
            ApplySelectedProfileModel(null, publishShell: true);
            return;
        }

        var updatedProfile = SelectedProfile with
        {
            Model = normalized,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        await _profileRepository.UpsertProfileAsync(updatedProfile);
        await ReloadProfilesAsync();
    }

    private void ApplySelectedProfileModel(string? profileModel, bool publishShell)
    {
        var normalized = NormalizeModelValue(profileModel);
        if (SelectedProfile is null)
        {
            normalized = null;
        }

        if (string.Equals(_selectedProfileModelOverride, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _selectedProfileModelOverride = normalized;
        if (publishShell)
        {
            PublishShell(false);
        }
    }

    private static string? NormalizeModelValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }

    private string[] BuildProfileModelValues(ProviderProfile profile)
    {
        var models = Profiles
            .Where(item =>
                string.Equals(item.Endpoint, profile.Endpoint, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.SecretRef, profile.SecretRef, StringComparison.Ordinal))
            .Select(item => NormalizeModelValue(item.Model))
            .Where(item => item is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (models.Length > 0)
        {
            return models;
        }

        var fallback = NormalizeModelValue(profile.Model);
        return fallback is null ? [] : [fallback];
    }

    private string? ResolveSelectedProfileModel()
    {
        if (SelectedProfile is null)
        {
            return null;
        }

        if (_selectedProfileModelOverride is not null)
        {
            return _selectedProfileModelOverride;
        }

        var fallback = NormalizeModelValue(SelectedProfile.Model);
        return fallback;
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

    public async Task<IReadOnlyList<WorkspaceFileEntry>> ListSelectedWorkspaceFilesAsync(
        string? relativePath,
        CancellationToken cancellationToken = default)
    {
        if (SelectedWorkspaceRoot is null)
        {
            return [];
        }

        return await _workspaceToolService.ListFilesAsync(
            SelectedWorkspaceRoot.RootPath,
            relativePath,
            cancellationToken);
    }

    public Task SetConversationModeAsync(string? modeId)
        => SetConversationModeCoreAsync(ParseConversationMode(modeId));

    public Task SetPlanningModeAsync(bool enabled)
    {
        if (IsBusy)
        {
            return Task.CompletedTask;
        }

        if (IsPlanningModeEnabled == enabled)
        {
            return Task.CompletedTask;
        }

        if (!enabled)
        {
            ClearPlanPanelState(publishShell: false);
        }

        IsPlanningModeEnabled = enabled;
        return Task.CompletedTask;
    }

    public async Task DeleteConversationAsync(Guid conversationId)
    {
        var conversation = _allConversations.FirstOrDefault(item => item.Id == conversationId);
        if (conversation is null)
        {
            return;
        }

        await _conversationRepository.DeleteConversationAsync(conversationId);
        var removedConversationIds = _allConversations
            .Where(item => item.Id == conversationId || item.ParentConversationId == conversationId || item.RootConversationId == conversationId)
            .Select(item => item.Id)
            .ToHashSet();
        _allConversations.RemoveAll(item => removedConversationIds.Contains(item.Id));

        if (SelectedConversation is not null && removedConversationIds.Contains(SelectedConversation.Id))
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
            result.TemperatureEnabled,
            NormalizeSamplingParameter(result.Temperature, 2),
            result.TopPEnabled,
            NormalizeSamplingParameter(result.TopP, 1),
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

    public async Task<IReadOnlyList<string>> FetchProfileModelsAsync(
        Guid? profileId,
        string endpoint,
        string? apiKey,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(BuildModelsEndpoint(endpoint), UriKind.Absolute, out var requestUri))
        {
            throw new InvalidOperationException("The endpoint is not a valid absolute URL.");
        }

        var resolvedApiKey = apiKey?.Trim();
        if (string.IsNullOrWhiteSpace(resolvedApiKey) && profileId is Guid existingProfileId)
        {
            var existing = await _profileRepository.GetProfileAsync(existingProfileId, cancellationToken);
            if (existing is not null && !string.IsNullOrWhiteSpace(existing.SecretRef))
            {
                resolvedApiKey = await _secretProtector.RetrieveSecretAsync(existing.SecretRef, cancellationToken);
            }
        }

        using var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(resolvedApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", resolvedApiKey);
        }

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        using var document = JsonDocument.Parse(responseBody);
        if (!document.RootElement.TryGetProperty("data", out var dataElement) || dataElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var models = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in dataElement.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var modelId = idElement.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(modelId) || !seen.Add(modelId))
            {
                continue;
            }

            models.Add(modelId);
        }

        return models;
    }

    public async Task DeleteProfileAsync(Guid profileId)
    {
        var profile = Profiles.FirstOrDefault(item => item.Id == profileId);
        if (profile is null)
        {
            return;
        }

        if (_allConversations.Any(item => item.ProfileId == profileId))
        {
            throw new InvalidOperationException($"Profile '{profile.Name}' is still used by one or more conversations.");
        }

        await _profileRepository.DeleteProfileAsync(profileId);
        if (!string.IsNullOrWhiteSpace(profile.SecretRef))
        {
            await _secretProtector.DeleteSecretAsync(profile.SecretRef);
        }

        await ReloadProfilesAsync();
        if (SelectedProfile?.Id == profileId)
        {
            SelectedProfile = Profiles.FirstOrDefault();
        }

        StatusText = $"Deleted profile '{profile.Name}'.";
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

    public async Task DeleteWorkspaceRootAsync(Guid workspaceRootId)
    {
        var workspaceRoot = WorkspaceRoots.FirstOrDefault(root => root.Id == workspaceRootId);
        if (workspaceRoot is null)
        {
            return;
        }

        await _conversationRepository.DeleteWorkspaceRootAsync(workspaceRootId);
        await ReloadWorkspaceRootsAsync();
        await ReloadConversationsAsync();

        if (SelectedWorkspaceRoot?.Id == workspaceRootId)
        {
            SelectedWorkspaceRoot = WorkspaceRoots.FirstOrDefault();
        }

        ApplyConversationFilter();
        StatusText = $"Deleted workspace '{workspaceRoot.Name}'.";
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
        _desktopSettings = settings;
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
            var settings = _desktopSettingsStore.Load();
            settings = settings with { ThemePreference = preference };
            _desktopSettingsStore.Save(settings);
            _desktopSettings = settings;
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
        if (System.Windows.Application.Current is { } app)
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

        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            ApplyThemePreference(AppThemePreference.System, persist: false, refreshShell: true);
        });
    }

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
        if (SelectedConversationMode == ConversationMode.Channel)
        {
            StatusText = "频道会话会在收到外部消息后自动创建。";
            PublishShell(false);
            return;
        }

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
        var rootConversationId = conversation.EffectiveRootConversationId;
        var messagesTask = _conversationRepository.ListMessagesAsync(conversation.Id);
        var teamAgentsTask =
            conversation.Mode == ConversationMode.Team || conversation.IsAgentConversation
                ? _conversationRepository.ListTeamAgentsAsync(rootConversationId)
                : Task.FromResult<IReadOnlyList<TeamAgentRecord>>([]);
        var toolRunsTask = _conversationRepository.ListToolExecutionsAsync(conversation.Id);
        var contextMessagesTask =
            conversation.IsTopLevelConversation || rootConversationId == conversation.Id
                ? Task.FromResult<IReadOnlyList<MessageRecord>>([])
                : _conversationRepository.ListMessagesAsync(rootConversationId);

        var messages = await messagesTask;
        var teamAgents = await teamAgentsTask;
        var toolRuns = await toolRunsTask;
        var contextMessages = await contextMessagesTask;
        if (version != _selectionVersion)
        {
            return;
        }

        _messages.Clear();
        _messages.AddRange(messages);
        _contextMessages.Clear();
        _contextMessages.AddRange(contextMessages);
        _teamAgents.Clear();
        _teamAgents.AddRange(teamAgents);
        _toolRuns.Clear();
        _toolRuns.AddRange(toolRuns);
        _toolRunAnchors.Clear();
        _selectedBoundAgent = ResolveBoundAgent(conversation, teamAgents);
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
        if (SelectedConversationMode == ConversationMode.Channel)
        {
            StatusText = "频道会话由外部消息驱动，不能在这里手动发送。";
            PublishShell(false);
            return;
        }

        if (SelectedProfile is null)
        {
            StatusText = "Create a profile first.";
            return;
        }

        var prompt = ComposerText.Trim();
        var useReasoning = _pendingReasoningEnabled;
        var promptImageAttachments = _pendingPromptImageAttachments.ToArray();
        _pendingPromptImageAttachments = [];

        if (string.IsNullOrWhiteSpace(prompt) && promptImageAttachments.Length == 0)
        {
            return;
        }

        var usePlanningMode = SelectedConversationMode == ConversationMode.Programming && IsPlanningModeEnabled;
        IsBusy = true;
        ComposerText = string.Empty;
        ClearPlanPanelState(publishShell: false);
        if (usePlanningMode)
        {
            StatusText = "Processing request...";
        }
        else
        {
            StatusText = "Streaming response...";
        }

        _turnCancellationSource = new CancellationTokenSource();
        var activeMessageIds = new HashSet<Guid>();
        TeamDocumentReadyEvent? pendingTeamDocument = null;
        TeamAgentRecord? runningBoundAgent = null;

        try
        {
            var conversation = await EnsureConversationAsync();
            if (TryMatchAgentMention(prompt, out var mentionedAgent, out var branchPrompt))
            {
                if (string.IsNullOrWhiteSpace(branchPrompt) && promptImageAttachments.Length == 0)
                {
                    StatusText = $"Enter a message for {mentionedAgent.Name}.";
                    PublishShell(false);
                    return;
                }

                conversation = await CreateAgentConversationAsync(conversation, mentionedAgent, branchPrompt);
                prompt = branchPrompt;
            }

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

            var userMessageId = Guid.NewGuid();
            var userAttachments = await PersistPromptImageAttachmentsAsync(
                conversation.Id,
                userMessageId,
                promptImageAttachments,
                _turnCancellationSource.Token);

            var userMessage = new MessageRecord(
                userMessageId,
                conversation.Id,
                MessageRole.User,
                prompt,
                MessageStatus.Completed,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                Attachments: userAttachments);
            _messages.Add(userMessage);
            await _conversationRepository.UpsertMessageAsync(userMessage);

            if (conversation.Title == "New chat")
            {
                conversation = conversation with
                {
                    Title = CreateConversationTitle(prompt, userAttachments),
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                };
                await PersistConversationAsync(conversation);
            }
            PublishShell(true);

            var apiKey = await _secretProtector.RetrieveSecretAsync(SelectedProfile.SecretRef, _turnCancellationSource.Token);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException("The selected profile does not have a readable API key.");
            }

            var requestTeamAgents = _teamAgents.ToArray();
            runningBoundAgent = _selectedBoundAgent;
            var selectedProfileModel = ResolveSelectedProfileModel();
            var requestProfile = selectedProfileModel is null
                ? SelectedProfile
                : SelectedProfile with { Model = selectedProfileModel };
            if (requestProfile is null)
            {
                throw new InvalidOperationException("The selected profile is required to submit a prompt.");
            }

            var settings = _desktopSettingsStore.Load();
            _desktopSettings = settings;
            var requestMessages = await _contextCompactionService.PrepareMessagesAsync(
                conversation.Id,
                requestProfile,
                apiKey,
                _messages.ToArray(),
                settings.ModelContextWindow,
                settings.ModelAutoCompactTokenLimit,
                _turnCancellationSource.Token);
            var rawContextMessages = _contextMessages.ToArray();
            var requestContextMessages = rawContextMessages.Length == 0
                ? rawContextMessages
                : await _contextCompactionService.PrepareMessagesAsync(
                    conversation.EffectiveRootConversationId,
                    requestProfile,
                    apiKey,
                    rawContextMessages,
                    settings.ModelContextWindow,
                    settings.ModelAutoCompactTokenLimit,
                    _turnCancellationSource.Token);

            if (runningBoundAgent is not null)
            {
                await UpdateTeamAgentStatusAsync(runningBoundAgent.Id, TeamAgentStatus.Running);
            }

            await foreach (var update in _agentChatRuntime.StreamTurnAsync(
                               new ChatTurnRequest(
                                   conversation.Id,
                                   requestProfile,
                                   apiKey,
                                   SelectedWorkspaceRoot,
                                   SelectedConversationMode,
                                   SelectedToolPermissionMode,
                                   SelectedTeamMaxRounds,
                                   SelectedTeamOutputMode,
                                   _toolApprovalHandler,
                                   requestMessages,
                                   requestTeamAgents,
                                   requestContextMessages,
                                   runningBoundAgent,
                                   usePlanningMode,
                                   useReasoning),
                               _turnCancellationSource.Token))
            {
                switch (update)
                {
                    case ExecutionPlanDraftingStartedEvent:
                        BeginPlanPanelDrafting();
                        StatusText = "Drafting plan...";
                        break;
                    case AssistantMessageStartedEvent started:
                        activeMessageIds.Add(started.Message.Id);
                        ReplaceMessage(started.Message);
                        if (usePlanningMode && _planPanel is null)
                        {
                            StatusText = "Streaming response...";
                        }

                        RequestStreamingShellPublish(true);
                        break;
                    case AssistantDeltaEvent delta:
                        ApplyAssistantDelta(delta.MessageId, delta.DeltaMarkdown);
                        break;
                    case ExecutionPlanPreparedEvent prepared:
                        ApplyPreparedExecutionPlan(prepared.Plan);
                        break;
                    case ExecutionPlanStepStatusChangedEvent planStepStatus:
                        ApplyExecutionPlanStepStatus(planStepStatus.StepId, planStepStatus.Status);
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

            if (usePlanningMode)
            {
                FinalizePlanPanelAfterSuccessfulTurn();
            }

            if (runningBoundAgent is not null)
            {
                await UpdateTeamAgentStatusAsync(runningBoundAgent.Id, TeamAgentStatus.Completed);
            }

            await TryCompactConversationContextAfterSuccessfulTurnAsync(
                conversation,
                requestProfile,
                apiKey,
                settings,
                _turnCancellationSource.Token);

            StatusText = "Ready.";
            PublishShellNow(true);
            PublishConversationCompletedNotification(conversation);
        }
        catch (OperationCanceledException)
        {
            await FailActiveMessagesAsync(activeMessageIds, "Generation stopped.");
            if (usePlanningMode && _planPanel is not null)
            {
                MarkPlanPanelCancelled();
            }

            if (runningBoundAgent is not null)
            {
                await UpdateTeamAgentStatusAsync(runningBoundAgent.Id, TeamAgentStatus.Failed);
            }

            StatusText = "Generation stopped.";
            PublishShellNow(true);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Chat turn failed.");
            await FailActiveMessagesAsync(activeMessageIds, exception.Message);
            if (usePlanningMode && _planPanel is not null)
            {
                MarkPlanPanelFailed(exception.Message);
            }

            if (runningBoundAgent is not null)
            {
                await UpdateTeamAgentStatusAsync(runningBoundAgent.Id, TeamAgentStatus.Failed);
            }

            StatusText = exception.Message;
            PublishShellNow(true);
        }
        finally
        {
            _turnCancellationSource?.Dispose();
            _turnCancellationSource = null;
            IsBusy = false;
        }
    }

    private async Task TryCompactConversationContextAfterSuccessfulTurnAsync(
        ConversationRecord conversation,
        ProviderProfile profile,
        string apiKey,
        DesktopSettings settings,
        CancellationToken cancellationToken)
    {
        try
        {
            await _contextCompactionService.PrepareMessagesAsync(
                conversation.Id,
                profile,
                apiKey,
                _messages.ToArray(),
                settings.ModelContextWindow,
                settings.ModelAutoCompactTokenLimit,
                cancellationToken);

            var rawContextMessages = _contextMessages.ToArray();
            if (rawContextMessages.Length == 0)
            {
                return;
            }

            await _contextCompactionService.PrepareMessagesAsync(
                conversation.EffectiveRootConversationId,
                profile,
                apiKey,
                rawContextMessages,
                settings.ModelContextWindow,
                settings.ModelAutoCompactTokenLimit,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Post-turn context compaction failed. ConversationId={ConversationId}",
                conversation.Id);
        }
    }

    private void BeginPlanPanelDrafting()
    {
        _planPanel = new TranscriptPlanPanel(
            true,
            "planning",
            "计划模式",
            null,
            "正在梳理计划",
            []);
        PublishShell(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _channelManager.Changed -= OnChannelManagerChanged;
        _toolApprovalHandler.ApprovalRequested -= OnToolApprovalRequested;

        if (_streamingPublishTimer is not null)
        {
            _streamingPublishTimer.Stop();
            _streamingPublishTimer.Tick -= OnStreamingPublishTimerTick;
        }

        _turnCancellationSource?.Cancel();
        _turnCancellationSource?.Dispose();
        _turnCancellationSource = null;
    }

    private void ApplyPreparedExecutionPlan(ExecutionPlan plan)
    {
        _planPanel = new TranscriptPlanPanel(
            true,
            "executing",
            "计划模式",
            string.IsNullOrWhiteSpace(plan.Summary) ? null : plan.Summary,
            "准备执行",
            plan.Steps
                .Select(step => new TranscriptPlanStep(
                    step.Id,
                    step.Title,
                    step.Status.ToString().ToLowerInvariant()))
                .ToArray());
        PublishShell(false);
    }

    private void ApplyExecutionPlanStepStatus(string stepId, ExecutionPlanStepStatus status)
    {
        if (_planPanel is null || _planPanel.Steps.Count == 0)
        {
            return;
        }

        var nextStatus = status.ToString().ToLowerInvariant();
        var changed = false;
        var nextSteps = _planPanel.Steps
            .Select(step =>
            {
                if (!string.Equals(step.Id, stepId, StringComparison.OrdinalIgnoreCase))
                {
                    return step;
                }

                changed = true;
                return step with { Status = nextStatus };
            })
            .ToArray();
        if (!changed)
        {
            return;
        }

        var nextState = ResolvePlanPanelState(nextSteps);
        _planPanel = _planPanel with
        {
            State = nextState,
            StatusText = BuildPlanPanelStatusText(nextSteps, nextState),
            Steps = nextSteps
        };

        StatusText = status switch
        {
            ExecutionPlanStepStatus.Running => BuildPlanExecutionStatusText(nextSteps),
            ExecutionPlanStepStatus.Failed => "Plan execution failed.",
            ExecutionPlanStepStatus.Cancelled => "Generation stopped.",
            _ when nextState == "completed" => "Plan steps completed.",
            _ => StatusText
        };

        PublishShell(false);
    }

    private void FinalizePlanPanelAfterSuccessfulTurn()
    {
        if (_planPanel is null || _planPanel.State is "failed" or "cancelled")
        {
            return;
        }

        var steps = _planPanel.Steps.ToArray();
        if (steps.Length == 0)
        {
            _planPanel = _planPanel with
            {
                State = "completed",
                StatusText = "已完成"
            };
        }
        else if (steps.All(step => string.Equals(step.Status, "completed", StringComparison.OrdinalIgnoreCase)))
        {
            _planPanel = _planPanel with
            {
                State = "completed",
                StatusText = "全部步骤已完成"
            };
        }

        PublishShell(false);
    }

    private void MarkPlanPanelFailed(string errorMessage)
    {
        if (_planPanel is null)
        {
            _planPanel = new TranscriptPlanPanel(
                true,
                "failed",
                "计划模式",
                string.IsNullOrWhiteSpace(errorMessage) ? null : errorMessage,
                "计划执行失败",
                []);
            PublishShell(false);
            return;
        }

        if (string.Equals(_planPanel.State, "completed", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var nextSteps = _planPanel.Steps
            .Select(step => string.Equals(step.Status, "running", StringComparison.OrdinalIgnoreCase)
                ? step with { Status = "failed" }
                : step)
            .ToArray();

        _planPanel = _planPanel with
        {
            State = "failed",
            Summary = string.IsNullOrWhiteSpace(errorMessage) ? _planPanel.Summary : errorMessage,
            StatusText = "计划执行失败",
            Steps = nextSteps
        };
        PublishShell(false);
    }

    private void MarkPlanPanelCancelled()
    {
        if (_planPanel is null)
        {
            _planPanel = new TranscriptPlanPanel(
                true,
                "cancelled",
                "计划模式",
                null,
                "已停止执行",
                []);
            PublishShell(false);
            return;
        }

        if (string.Equals(_planPanel.State, "completed", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var nextSteps = _planPanel.Steps
            .Select(step => string.Equals(step.Status, "running", StringComparison.OrdinalIgnoreCase)
                ? step with { Status = "cancelled" }
                : step)
            .ToArray();

        _planPanel = _planPanel with
        {
            State = "cancelled",
            StatusText = "已停止执行",
            Steps = nextSteps
        };
        PublishShell(false);
    }

    private void ClearPlanPanelState(bool publishShell)
    {
        _planPanel = TranscriptPlanPanel.Hidden;
        if (publishShell)
        {
            PublishShell(false);
        }
    }

    private static string ResolvePlanPanelState(IReadOnlyList<TranscriptPlanStep> steps)
    {
        if (steps.Any(step => string.Equals(step.Status, "failed", StringComparison.OrdinalIgnoreCase)))
        {
            return "failed";
        }

        if (steps.Any(step => string.Equals(step.Status, "cancelled", StringComparison.OrdinalIgnoreCase)))
        {
            return "cancelled";
        }

        if (steps.Count > 0 && steps.All(step => string.Equals(step.Status, "completed", StringComparison.OrdinalIgnoreCase)))
        {
            return "completed";
        }

        return "executing";
    }

    private static string BuildPlanPanelStatusText(IReadOnlyList<TranscriptPlanStep> steps, string panelState)
    {
        if (string.Equals(panelState, "failed", StringComparison.OrdinalIgnoreCase))
        {
            return "有步骤执行失败";
        }

        if (string.Equals(panelState, "cancelled", StringComparison.OrdinalIgnoreCase))
        {
            return "已停止执行";
        }

        if (string.Equals(panelState, "completed", StringComparison.OrdinalIgnoreCase))
        {
            return "全部步骤已完成";
        }

        var runningEntry = steps
            .Select((step, index) => (Step: step, Index: index))
            .FirstOrDefault(entry => string.Equals(entry.Step.Status, "running", StringComparison.OrdinalIgnoreCase));
        var runningIndex = runningEntry.Step is null ? -1 : runningEntry.Index;

        return runningIndex >= 0
            ? $"执行第 {runningIndex + 1}/{steps.Count} 步"
            : "准备执行";
    }

    private static string BuildPlanExecutionStatusText(IReadOnlyList<TranscriptPlanStep> steps)
    {
        var runningEntry = steps
            .Select((step, index) => (Step: step, Index: index))
            .FirstOrDefault(entry => string.Equals(entry.Step.Status, "running", StringComparison.OrdinalIgnoreCase));
        var runningIndex = runningEntry.Step is null ? -1 : runningEntry.Index;

        return runningIndex >= 0
            ? $"Executing step {runningIndex + 1}/{steps.Count}..."
            : "Executing plan...";
    }

    private void Stop() => _turnCancellationSource?.Cancel();

    private bool CanSend()
        => !IsBusy &&
           SelectedConversationMode != ConversationMode.Channel &&
           SelectedProfile is not null &&
           !string.IsNullOrWhiteSpace(ComposerText);

    private bool CanCreateConversation()
        => !IsBusy &&
           SelectedConversationMode != ConversationMode.Channel &&
           SelectedProfile is not null;

    private void NotifyCommandStates()
    {
        SendCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        NewConversationCommand.NotifyCanExecuteChanged();
    }

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

    private async Task<ConversationRecord> EnsureConversationAsync()
    {
        if (SelectedConversation is not null)
        {
            return SelectedConversation;
        }

        await CreateNewConversationAsync();
        return SelectedConversation!;
    }

    private TeamAgentRecord? ResolveBoundAgent(
        ConversationRecord conversation,
        IReadOnlyList<TeamAgentRecord> teamAgents)
    {
        if (!conversation.IsAgentConversation)
        {
            return null;
        }

        var boundAgent = teamAgents.FirstOrDefault(agent =>
            (conversation.BoundAgentId is Guid boundAgentId && agent.Id == boundAgentId) ||
            (string.Equals(agent.Name, conversation.BoundAgentName, StringComparison.OrdinalIgnoreCase) &&
             string.Equals(agent.Role, conversation.BoundAgentRole, StringComparison.OrdinalIgnoreCase)));
        if (boundAgent is not null)
        {
            return boundAgent;
        }

        if (!string.IsNullOrWhiteSpace(conversation.BoundAgentName) &&
            !string.IsNullOrWhiteSpace(conversation.BoundAgentRole))
        {
            var now = DateTimeOffset.UtcNow;
            return new TeamAgentRecord(
                conversation.BoundAgentId ?? Guid.NewGuid(),
                conversation.EffectiveRootConversationId,
                conversation.BoundAgentName,
                conversation.BoundAgentRole,
                $"{conversation.BoundAgentName} handles follow-up questions as {conversation.BoundAgentRole}.",
                TeamAgentStatus.Ready,
                int.MaxValue,
                now,
                now);
        }

        return null;
    }

    private bool TryMatchAgentMention(
        string prompt,
        out TeamAgentRecord agent,
        out string branchPrompt)
    {
        agent = null!;
        branchPrompt = prompt;

        if (SelectedConversationMode != ConversationMode.Team || _teamAgents.Count == 0)
        {
            return false;
        }

        var trimmedPrompt = prompt.TrimStart();
        if (!trimmedPrompt.StartsWith("@", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var candidate in _teamAgents
                     .OrderByDescending(item => item.Name.Length)
                     .ThenBy(item => item.SortOrder)
                     .ThenBy(item => item.CreatedAtUtc))
        {
            foreach (var mentionToken in new[] { "@{" + candidate.Name.Trim() + "}", "@" + candidate.Name.Trim() })
            {
                if (!trimmedPrompt.StartsWith(mentionToken, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (trimmedPrompt.Length > mentionToken.Length &&
                    !IsMentionBoundary(trimmedPrompt[mentionToken.Length]))
                {
                    continue;
                }

                agent = candidate;
                branchPrompt = trimmedPrompt[mentionToken.Length..].TrimStart(' ', '\t', '\r', '\n', ':', '-', '>');
                return true;
            }
        }

        return false;
    }

    private async Task<ConversationRecord> CreateAgentConversationAsync(
        ConversationRecord sourceConversation,
        TeamAgentRecord agent,
        string prompt)
    {
        var rootConversation = _allConversations.FirstOrDefault(item => item.Id == sourceConversation.EffectiveRootConversationId)
            ?? sourceConversation;
        var now = DateTimeOffset.UtcNow;
        rootConversation = rootConversation with { UpdatedAtUtc = now };
        await _conversationRepository.UpsertConversationAsync(rootConversation);
        UpsertConversation(rootConversation);

        var conversation = new ConversationRecord(
            Guid.NewGuid(),
            CreateConversationTitle(prompt),
            SelectedProfile?.Id ?? sourceConversation.ProfileId,
            SelectedWorkspaceRoot?.Id ?? sourceConversation.WorkspaceRootId,
            ConversationMode.Team,
            SelectedToolPermissionMode,
            SelectedTeamMaxRounds,
            SelectedTeamOutputMode,
            now,
            now,
            rootConversation.Id,
            rootConversation.Id,
            agent.Id,
            agent.Name,
            agent.Role);

        var persistedConversation = await _conversationRepository.UpsertConversationAsync(conversation);
        UpsertConversation(persistedConversation);
        ApplyConversationFilter(persistedConversation.Id);
        await LoadConversationAsync(persistedConversation);
        StatusText = persistedConversation.Id == conversation.Id
            ? $"Started a direct session with {agent.Name}."
            : $"Continued direct session with {agent.Name}.";
        PublishShell(false);
        return persistedConversation;
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

        if (conversation.Mode == ConversationMode.Channel)
        {
            return true;
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

        var conversations = BuildConversationItems();

        var conversationModes = BuildConversationModeOptions();

        var profiles = Profiles
            .Select(profile => new ShellSelectOption(
                profile.Id.ToString("D"),
                profile.Name,
                profile.Endpoint,
                profile.TemperatureEnabled,
                profile.Temperature,
                profile.TopPEnabled,
                profile.TopP,
                profile.Model))
            .ToArray();
        var profileModelValues = SelectedProfile is null ? [] : BuildProfileModelValues(SelectedProfile);
        var profileModels = profileModelValues
            .Select(model => new ShellSelectOption(model, model))
            .ToArray();
        var selectedProfileModel = ResolveSelectedProfileModel();

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
        var planPanel = SelectedConversationMode == ConversationMode.Programming
            ? _planPanel ?? TranscriptPlanPanel.Hidden
            : TranscriptPlanPanel.Hidden;
        var contextUsage = BuildContextUsage();

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
            profileModels,
            selectedProfileModel,
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
            _channelManager.BuildTranscriptChannels(Profiles.ToArray()),
            teamMembers,
            AgentActivityNodes.ToArray(),
            IsPlanningModeEnabled,
            planPanel,
            contextUsage,
            StatusText,
            IsBusy));
    }

    private TranscriptContextUsage BuildContextUsage()
    {
        var contextWindow = Math.Max(1, _desktopSettings.ModelContextWindow);
        var autoCompactLimit = _desktopSettings.ModelAutoCompactTokenLimit < 0
            ? 0
            : Math.Min(_desktopSettings.ModelAutoCompactTokenLimit, contextWindow);
        var usedTokens = ResolveContextUsageTokens(_contextMessages.Concat(_messages), out var isMeasured);

        return new TranscriptContextUsage(usedTokens, contextWindow, autoCompactLimit, isMeasured);
    }

    private static long ResolveContextUsageTokens(IEnumerable<MessageRecord> messages, out bool isMeasured)
    {
        var promptMessages = messages
            .Where(ShouldIncludeInContextUsage)
            .OrderBy(message => message.CreatedAtUtc)
            .ThenBy(message => message.Id)
            .ToArray();
        var measuredIndex = Array.FindLastIndex(
            promptMessages,
            message => message.Role == MessageRole.Assistant && message.InputTokens is > 0);

        if (measuredIndex < 0)
        {
            isMeasured = false;
            return EstimateContextUsageTokens(promptMessages);
        }

        isMeasured = true;
        var measuredMessage = promptMessages[measuredIndex];
        var total = Math.Max(0L, measuredMessage.InputTokens!.Value);

        // The latest input count is the provider-reported prompt size for that turn.
        // Add the provider-reported assistant output because it becomes part of the next raw context.
        total += measuredMessage.OutputTokens is > 0
            ? measuredMessage.OutputTokens.Value
            : EstimateContextUsageMessageTokens(measuredMessage);

        for (var index = measuredIndex + 1; index < promptMessages.Length; index++)
        {
            total += EstimateContextUsageMessageTokens(promptMessages[index]);
        }

        return Math.Max(0, total);
    }

    private static long EstimateContextUsageTokens(IEnumerable<MessageRecord> messages)
    {
        var total = 0L;
        var seenMessageIds = new HashSet<Guid>();
        foreach (var message in messages)
        {
            if (!seenMessageIds.Add(message.Id) || !ShouldIncludeInContextUsage(message))
            {
                continue;
            }

            total += EstimateContextUsageMessageTokens(message);
        }

        return Math.Max(0L, total);
    }

    private static long EstimateContextUsageMessageTokens(MessageRecord message)
    {
        var content = message.Role == MessageRole.Assistant
            ? AssistantMessageSegmenter.Split(message.MarkdownContent).ContentMarkdown
            : message.MarkdownContent;
        var attachmentTokens = (message.Attachments?.Count ?? 0) * ContextUsageImageMetadataTokens;
        var speakerTokens = EstimateContextUsageTextTokens(message.AgentName) + EstimateContextUsageTextTokens(message.AgentRole);

        return ContextUsageMessageOverheadTokens + EstimateContextUsageTextTokens(content) + attachmentTokens + speakerTokens;
    }

    private static long EstimateContextUsageTextTokens(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0L;
        }

        return Math.Max(1L, (long)Math.Ceiling(text.Length / 4d));
    }

    private static bool ShouldIncludeInContextUsage(MessageRecord message)
    {
        if (message.Status == MessageStatus.Failed)
        {
            return false;
        }

        if (message.Role == MessageRole.Assistant)
        {
            var segments = AssistantMessageSegmenter.Split(message.MarkdownContent);
            return !string.IsNullOrWhiteSpace(segments.ContentMarkdown);
        }

        return true;
    }

    private TranscriptConversationItem[] BuildConversationItems()
    {
        var filteredConversations = Conversations.ToArray();
        var childrenByParent = filteredConversations
            .Where(item => item.ParentConversationId is Guid)
            .GroupBy(item => item.ParentConversationId!.Value)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(item => item.UpdatedAtUtc)
                    .ThenBy(item => item.CreatedAtUtc)
                    .ToArray());
        var items = new List<TranscriptConversationItem>(filteredConversations.Length);
        var includedIds = new HashSet<Guid>();

        foreach (var conversation in filteredConversations
                     .Where(item => item.IsTopLevelConversation)
                     .OrderByDescending(item => item.UpdatedAtUtc)
                     .ThenBy(item => item.CreatedAtUtc))
        {
            items.Add(BuildConversationItem(conversation, null, 0));
            includedIds.Add(conversation.Id);

            if (!childrenByParent.TryGetValue(conversation.Id, out var children))
            {
                continue;
            }

            foreach (var child in children)
            {
                items.Add(BuildConversationItem(child, conversation.Id, 1));
                includedIds.Add(child.Id);
            }
        }

        foreach (var conversation in filteredConversations
                     .Where(item => !includedIds.Contains(item.Id))
                     .OrderByDescending(item => item.UpdatedAtUtc)
                     .ThenBy(item => item.CreatedAtUtc))
        {
            items.Add(BuildConversationItem(
                conversation,
                conversation.ParentConversationId,
                conversation.ParentConversationId is null ? 0 : 1));
        }

        return items.ToArray();
    }

    private TranscriptConversationItem BuildConversationItem(
        ConversationRecord conversation,
        Guid? parentConversationId,
        int depth)
        => new(
            conversation.Id.ToString("D"),
            conversation.Title,
            conversation.UpdatedAtUtc.LocalDateTime.ToString("yyyy-MM-dd HH:mm"),
            SelectedConversation?.Id == conversation.Id,
            parentConversationId?.ToString("D"),
            depth,
            conversation.IsAgentConversation,
            conversation.IsAgentConversation
                ? conversation.BoundAgentName
                : ResolveConversationBadge(conversation),
            conversation.IsAgentConversation ? conversation.BoundAgentRole : null,
            conversation.BoundAgentId?.ToString("D"));

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

    private static IReadOnlyList<TranscriptImageAttachment> BuildImageAttachments(MessageRecord message)
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
                TryCreateAttachmentDataUrl(item)))
            .ToArray();
    }

    private static string? TryCreateAttachmentDataUrl(MessageAttachmentRecord attachment)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(attachment.StoragePath) ||
                string.IsNullOrWhiteSpace(attachment.MediaType) ||
                !File.Exists(attachment.StoragePath))
            {
                return null;
            }

            var bytes = File.ReadAllBytes(attachment.StoragePath);
            return $"data:{attachment.MediaType};base64,{Convert.ToBase64String(bytes)}";
        }
        catch
        {
            return null;
        }
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
            message.CreatedAtUtc.LocalDateTime.ToString("yyyy-MM-dd HH:mm"),
            message.AgentId?.ToString("D"),
            BuildImageAttachments(message));
    }

    private static string ThemePreferenceToId(AppThemePreference preference)
        => preference.ToString().ToLowerInvariant();

    private static ShellSelectOption[] BuildConversationModeOptions()
    {
        return
        [
            new("programming", "编程", "面向工作区分析、问答与编码协作"),
            new("team", "团队", "多 Agent 讨论后再由协调者总结输出"),
            new("channel", "频道", "查看并接收来自外部频道的会话消息")
        ];
    }

    private string? ResolveConversationBadge(ConversationRecord conversation)
        => string.IsNullOrWhiteSpace(conversation.ChannelKind)
            ? null
            : _channelManager.GetChannelName(conversation.ChannelKind);

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

    private static bool IsMentionBoundary(char value)
        => char.IsWhiteSpace(value) || char.IsPunctuation(value) || char.IsSymbol(value);

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

    private static string BuildModelsEndpoint(string endpoint)
        => NormalizeEndpoint(endpoint) + "/models";

    private static double NormalizeSamplingParameter(double value, double maxValue)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 0.7;
        }

        return Math.Clamp(Math.Round(value, 2), 0, maxValue);
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



