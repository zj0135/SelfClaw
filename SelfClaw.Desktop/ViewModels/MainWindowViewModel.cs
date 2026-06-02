using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
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
    private const int DefaultModelContextWindow = 256_000;
    private const int DefaultModelAutoCompactTokenLimit = 200_000;
    private const int MaxPromptImageAttachments = 6;
    private const long MaxPromptImageBytes = 10 * 1024 * 1024;
    private const long MaxPromptImageTotalBytes = 30 * 1024 * 1024;
    private readonly IConversationRepository _conversationRepository;
    private readonly IProfileRepository _profileRepository;
    private readonly ISecretProtector _secretProtector;
    private readonly IAgentChatRuntime _agentChatRuntime;
    private readonly IConversationContextCompactionService _contextCompactionService;
    private readonly DesktopToolApprovalHandler _toolApprovalHandler;
    private readonly DesktopNotificationService _desktopNotificationService;
    private readonly MarkdownHtmlRenderer _markdownHtmlRenderer;
    private readonly DesktopAgentStore _desktopAgentStore;
    private readonly StoragePaths _storagePaths;
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly DispatcherTimer? _streamingPublishTimer;

    private readonly List<ConversationRecord> _allConversations = [];
    private readonly List<DesktopAgentDefinition> _agents = [];
    private readonly List<MessageRecord> _messages = [];
    private readonly List<ToolExecutionRecord> _toolRuns = [];
    private readonly Dictionary<Guid, ToolRunAnchor> _toolRunAnchors = [];
    private readonly Dictionary<Guid, ConversationRuntimeState> _conversationRuntimeStates = [];
    private readonly Dictionary<Guid, string> _conversationStatusTexts = [];
    private readonly HashSet<Guid> _expandedSidebarWorkspaceRootIds = [];
    private IReadOnlyList<PromptImageAttachment> _pendingPromptImageAttachments = [];
    private bool _pendingReasoningEnabled;
    private bool _initialized;
    private int _selectionVersion;
    private bool _isSidebarProjectsExpanded = true;
    private ConversationRecord? _selectedConversation;
    private ProviderProfile? _selectedProfile;
    private string? _selectedProfileModelOverride;
    private WorkspaceRoot? _selectedWorkspaceRoot;
    private string _selectedAgentId = DesktopAgentStore.BuildAgentId;
    private string _composerText = string.Empty;
    private string _statusText = "Add a model profile to get started.";
    private bool _isBusy;
    private ThemeMode _activeThemeMode = ThemeMode.System;
    private string _effectiveTranscriptTheme = "light";
    private bool _isSidebarStandaloneConversationsExpanded = true;
    private ToolPermissionMode _selectedToolPermissionMode = ToolPermissionMode.RequireApproval;
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
        DesktopToolApprovalHandler toolApprovalHandler,
        DesktopNotificationService desktopNotificationService,
        MarkdownHtmlRenderer markdownHtmlRenderer,
        DesktopAgentStore desktopAgentStore,
        StoragePaths storagePaths,
        ILogger<MainWindowViewModel> logger)
    {
        _conversationRepository = conversationRepository;
        _profileRepository = profileRepository;
        _secretProtector = secretProtector;
        _agentChatRuntime = agentChatRuntime;
        _contextCompactionService = contextCompactionService;
        _toolApprovalHandler = toolApprovalHandler;
        _desktopNotificationService = desktopNotificationService;
        _markdownHtmlRenderer = markdownHtmlRenderer;
        _desktopAgentStore = desktopAgentStore;
        _storagePaths = storagePaths;
        _logger = logger;
        _toolApprovalHandler.ApprovalRequested += OnToolApprovalRequested;
        if (System.Windows.Application.Current?.Dispatcher is Dispatcher dispatcher)
        {
            _streamingPublishTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
            {
                Interval = StreamingPublishInterval
            };
            _streamingPublishTimer.Tick += OnStreamingPublishTimerTick;
        }

        ApplySystemTheme(refreshShell: false);
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        SendCommand = new AsyncRelayCommand(SendAsync, CanSend);
        StopCommand = new RelayCommand(Stop, () => IsBusy);
        NewConversationCommand = new AsyncRelayCommand(CreateNewConversationAsync, CanCreateConversation);
    }

    public event EventHandler<TranscriptRenderState>? TranscriptChanged;

    public ObservableCollection<ConversationRecord> Conversations { get; } = [];

    public ObservableCollection<SidebarProjectItem> SidebarProjects { get; } = [];

    public ObservableCollection<SidebarConversationItem> SidebarStandaloneConversations { get; } = [];

    public ObservableCollection<ProviderProfile> Profiles { get; } = [];

    public ObservableCollection<WorkspaceRoot> WorkspaceRoots { get; } = [];

    public ObservableCollection<AgentActivityNode> AgentActivityNodes { get; } = [];

    public IAsyncRelayCommand SendCommand { get; }

    public IRelayCommand StopCommand { get; }

    public IAsyncRelayCommand NewConversationCommand { get; }

    public int SidebarProjectCount => SidebarProjects.Count;

    public bool IsSidebarProjectsExpanded
    {
        get => _isSidebarProjectsExpanded;
        private set => SetProperty(ref _isSidebarProjectsExpanded, value);
    }

    public int SidebarStandaloneConversationCount => SidebarStandaloneConversations.Count;

    public bool HasSidebarStandaloneConversations => SidebarStandaloneConversations.Count > 0;

    public bool IsSidebarStandaloneConversationsExpanded
    {
        get => _isSidebarStandaloneConversationsExpanded;
        private set
        {
            if (SetProperty(ref _isSidebarStandaloneConversationsExpanded, value))
            {
                RefreshSidebarHistory();
            }
        }
    }

    public bool HasAgentActivityNodes => AgentActivityNodes.Count > 0;

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
                ProjectSelectedRuntimeState(publishShell: false);
                PublishShell(false);
                _ = LoadConversationAsync(value);
            }
            else
            {
                _selectionVersion++;
                _messages.Clear();
                _toolRuns.Clear();
                _toolRunAnchors.Clear();
                ProjectSelectedRuntimeState(publishShell: false);
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
        private set => SetStatusTextForSelectedConversation(value, publishShell: true);
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
        await ReloadAgentsAsync();
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

    public async Task CreateStandaloneConversationFromUiAsync()
    {
        SelectedWorkspaceRoot = null;
        ApplyConversationFilter();
        await CreateNewConversationAsync();
    }

    public void ToggleSidebarProjects()
    {
        IsSidebarProjectsExpanded = !IsSidebarProjectsExpanded;
    }

    public void ToggleSidebarStandaloneConversations()
    {
        IsSidebarStandaloneConversationsExpanded = !IsSidebarStandaloneConversationsExpanded;
    }

    public Task ToggleSidebarWorkspaceRootAsync(Guid workspaceRootId)
    {
        if (!_expandedSidebarWorkspaceRootIds.Add(workspaceRootId))
        {
            _expandedSidebarWorkspaceRootIds.Remove(workspaceRootId);
        }

        RefreshSidebarHistory();
        return SetSelectedWorkspaceRootAsync(workspaceRootId);
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

    public Task SelectConversationAsync(Guid conversationId)
    {
        var conversation = _allConversations.FirstOrDefault(item => item.Id == conversationId);
        if (conversation is null || SelectedConversation?.Id == conversation.Id)
        {
            return Task.CompletedTask;
        }

        SelectedConversation = conversation;
        return Task.CompletedTask;
    }

    private void ApplySystemTheme(bool refreshShell)
    {
        ActiveThemeMode = ThemeMode.System;
        EffectiveTranscriptTheme = ResolveTranscriptTheme();
        ApplyThemeModeToApplication(ThemeMode.System);

        if (_initialized || refreshShell)
        {
            PublishShell(false);
        }
    }

    private static string ResolveTranscriptTheme()
        => SystemThemeReader.IsDarkModeEnabled() ? "dark" : "light";

    private static void ApplyThemeModeToApplication(ThemeMode mode)
    {
        if (System.Windows.Application.Current is { } app)
        {
            app.ThemeMode = mode;
        }
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            ApplySystemTheme(refreshShell: true);
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
        SelectedWorkspaceRoot = selectedId is Guid id
            ? workspaceRoots.FirstOrDefault(root => root.Id == id)
            : null;
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
            ConversationMode.Programming,
            SelectedToolPermissionMode,
            ResolveSelectedAgent().Id,
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

        SelectedProfile = Profiles.FirstOrDefault(profile => profile.Id == conversation.ProfileId) ?? SelectedProfile;
        SelectedWorkspaceRoot = conversation.WorkspaceRootId is Guid workspaceRootId
            ? WorkspaceRoots.FirstOrDefault(root => root.Id == workspaceRootId)
            : null;
        SelectedToolPermissionMode = conversation.ToolPermissionMode;
        SyncSelectedAgentFromConversation(conversation, publishShell: false);
        ProjectSelectedRuntimeState(publishShell: false);
        ApplyConversationFilter(conversation.Id);

        PublishAgentActivities();
        PublishShell(false);
    }

    private async Task SendAsync()
    {
        var selectedProfile = SelectedProfile;
        if (selectedProfile is null)
        {
            StatusText = "Create a profile first.";
            return;
        }

        var prompt = ComposerText.Trim();
        var useReasoning = _pendingReasoningEnabled;
        var promptImageAttachments = _pendingPromptImageAttachments.ToArray();
        var selectedProfileModel = ResolveSelectedProfileModel();
        var selectedWorkspaceRoot = SelectedWorkspaceRoot;
        var selectedToolPermissionMode = SelectedToolPermissionMode;
        var baseMessages = _messages.ToArray();
        var baseToolRuns = _toolRuns.ToArray();
        var baseToolRunAnchors = new Dictionary<Guid, ToolRunAnchor>(_toolRunAnchors);

        _pendingPromptImageAttachments = [];

        if (string.IsNullOrWhiteSpace(prompt) && promptImageAttachments.Length == 0)
        {
            return;
        }

        ComposerText = string.Empty;

        ConversationRuntimeState? runtimeState = null;
        ProviderProfile? requestProfile = null;
        string? apiKey = null;

        try
        {
            var conversation = await EnsureConversationAsync();
            if (IsConversationRunning(conversation.Id))
            {
                StatusText = "This conversation is already running.";
                return;
            }

            conversation = conversation with
            {
                ProfileId = selectedProfile.Id,
                WorkspaceRootId = selectedWorkspaceRoot?.Id,
                Mode = ConversationMode.Programming,
                ToolPermissionMode = selectedToolPermissionMode,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            await PersistConversationAsync(conversation, preferSelection: IsSelectedConversation(conversation.Id));

            var runtimeAgent = ResolveRuntimeAgent(conversation.AgentId);

            runtimeState = StartConversationRuntimeState(
                conversation,
                "Streaming response...",
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
            ReplaceMessage(runtimeState, userMessage);
            await _conversationRepository.UpsertMessageAsync(userMessage);

            if (conversation.Title == "New chat")
            {
                conversation = conversation with
                {
                    Title = CreateConversationTitle(prompt, userAttachments),
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                };
                runtimeState.Conversation = conversation;
                await PersistConversationAsync(conversation, preferSelection: IsSelectedConversation(conversation.Id));
            }
            PublishRuntimeState(runtimeState, true);

            requestProfile = selectedProfileModel is null
                ? selectedProfile
                : selectedProfile with { Model = selectedProfileModel };
            apiKey = await _secretProtector.RetrieveSecretAsync(requestProfile.SecretRef, cancellationToken);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException("The selected profile does not have a readable API key.");
            }

            var requestMessages = await _contextCompactionService.PrepareMessagesAsync(
                conversation.Id,
                requestProfile,
                apiKey,
                runtimeState.Messages.ToArray(),
                DefaultModelContextWindow,
                DefaultModelAutoCompactTokenLimit,
                cancellationToken);

            await foreach (var update in _agentChatRuntime.StreamTurnAsync(
                               new ChatTurnRequest(
                                   conversation.Id,
                                   requestProfile,
                                   apiKey,
                                   selectedWorkspaceRoot,
                                   ConversationMode.Programming,
                                   runtimeAgent,
                                   selectedToolPermissionMode,
                                   _toolApprovalHandler,
                                   requestMessages,
                                   useReasoning),
                               cancellationToken))
            {
                switch (update)
                {
                    case AssistantMessageStartedEvent started:
                        runtimeState.ActiveMessageIds.Add(started.Message.Id);
                        ReplaceMessage(runtimeState, started.Message);
                        PublishRuntimeState(runtimeState, true);
                        break;
                    case AssistantDeltaEvent delta:
                        ApplyAssistantDelta(runtimeState, delta.MessageId, delta.DeltaMarkdown);
                        break;
                    case ToolExecutionStartedEvent toolStarted:
                        var startedRecord = CaptureToolRunAnchor(runtimeState, toolStarted.Record);
                        UpsertToolRun(runtimeState, startedRecord);
                        await _conversationRepository.UpsertToolExecutionAsync(startedRecord);
                        PublishAgentActivities();
                        break;
                    case ToolExecutionCompletedEvent toolCompleted:
                        var completedRecord = CaptureToolRunAnchor(runtimeState, toolCompleted.Record);
                        UpsertToolRun(runtimeState, completedRecord);
                        await _conversationRepository.UpsertToolExecutionAsync(completedRecord);
                        PublishAgentActivities();
                        break;
                    case AssistantMessageCompletedEvent completed:
                        runtimeState.ActiveMessageIds.Remove(completed.Message.Id);
                        await CompleteAssistantMessageAsync(runtimeState, completed.Message);
                        break;
                }
            }

            await TryCompactConversationContextAfterSuccessfulTurnAsync(
                runtimeState,
                conversation,
                requestProfile,
                apiKey,
                cancellationToken);

            SetStatusTextForConversation(runtimeState, "Ready.", publishShell: false);
            PublishConversationCompletedNotification(conversation, runtimeState.Messages);
        }
        catch (OperationCanceledException) when (runtimeState?.CancellationTokenSource.IsCancellationRequested == true)
        {
            if (runtimeState is not null)
            {
                await FailActiveMessagesAsync(runtimeState, runtimeState.ActiveMessageIds, "Generation stopped.");
                SetStatusTextForConversation(runtimeState, "Generation stopped.", publishShell: false);
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Chat turn failed.");
            if (runtimeState is null)
            {
                StatusText = exception.Message;
                return;
            }

            await FailActiveMessagesAsync(runtimeState, runtimeState.ActiveMessageIds, exception.Message);
            SetStatusTextForConversation(runtimeState, exception.Message, publishShell: false);
        }
        finally
        {
            if (runtimeState is not null)
            {
                CompleteConversationRuntimeState(runtimeState);
                runtimeState.CancellationTokenSource.Dispose();
            }
        }
    }

    private async Task TryCompactConversationContextAfterSuccessfulTurnAsync(
        ConversationRuntimeState runtimeState,
        ConversationRecord conversation,
        ProviderProfile profile,
        string apiKey,
        CancellationToken cancellationToken)
    {
        try
        {
            await _contextCompactionService.PrepareMessagesAsync(
                conversation.Id,
                profile,
                apiKey,
                runtimeState.Messages.ToArray(),
                DefaultModelContextWindow,
                DefaultModelAutoCompactTokenLimit,
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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _toolApprovalHandler.ApprovalRequested -= OnToolApprovalRequested;

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

    private void Stop()
    {
        var runtimeState = GetSelectedRuntimeState();
        if (runtimeState?.IsRunning == true)
        {
            runtimeState.CancellationTokenSource.Cancel();
        }
    }

    private bool CanSend()
        => !IsBusy &&
           SelectedProfile is not null &&
           !string.IsNullOrWhiteSpace(ComposerText);

    private bool CanCreateConversation()
        => !IsBusy &&
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

    private async Task PersistConversationAsync(ConversationRecord conversation, bool preferSelection = true)
    {
        await _conversationRepository.UpsertConversationAsync(conversation);
        UpsertConversation(conversation, preferSelection);
        if (preferSelection || SelectedConversation?.Id == conversation.Id)
        {
            _selectedConversation = conversation;
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

    private async Task SaveConversationSelectionAsync(ConversationRecord conversation)
    {
        var updated = conversation with
        {
            ProfileId = SelectedProfile?.Id ?? conversation.ProfileId,
            WorkspaceRootId = SelectedWorkspaceRoot?.Id,
            Mode = ConversationMode.Programming,
            ToolPermissionMode = SelectedToolPermissionMode,
            AgentId = ResolveSelectedAgent().Id,
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
        ReplaceCollection(Conversations, filtered);
        RefreshSidebarHistory();

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
        if (conversation.Mode != ConversationMode.Programming)
        {
            return false;
        }

        if (!string.Equals(conversation.AgentId, ResolveSelectedAgent().Id, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return SelectedWorkspaceRoot is null
            ? conversation.WorkspaceRootId is null
            : conversation.WorkspaceRootId == SelectedWorkspaceRoot.Id;
    }

    private void RefreshSidebarHistory()
    {
        var programmingConversations = _allConversations
            .Where(item => item.Mode == ConversationMode.Programming)
            .OrderByDescending(item => item.UpdatedAtUtc)
            .ThenBy(item => item.CreatedAtUtc)
            .ToArray();

        var conversationsByWorkspaceId = programmingConversations
            .Where(item => item.WorkspaceRootId is Guid)
            .GroupBy(item => item.WorkspaceRootId!.Value)
            .ToDictionary(item => item.Key, item => item.ToArray());

        var projects = WorkspaceRoots
            .Select(root =>
            {
                conversationsByWorkspaceId.TryGetValue(root.Id, out var conversations);
                conversations ??= [];
                return new SidebarProjectItem(
                    root.Id,
                    root.Name,
                    root.RootPath,
                    _expandedSidebarWorkspaceRootIds.Contains(root.Id),
                    new ObservableCollection<SidebarConversationItem>(
                        conversations.Select(BuildSidebarConversationItem)));
            })
            .ToArray();

        var standaloneConversations = programmingConversations
            .Where(item => item.WorkspaceRootId is null)
            .Select(BuildSidebarConversationItem)
            .ToArray();

        ReplaceCollection(SidebarProjects, projects);
        ReplaceCollection(SidebarStandaloneConversations, standaloneConversations);
        OnPropertyChanged(nameof(SidebarProjectCount));
        OnPropertyChanged(nameof(SidebarStandaloneConversationCount));
        OnPropertyChanged(nameof(HasSidebarStandaloneConversations));
        OnPropertyChanged(nameof(IsSidebarStandaloneConversationsExpanded));
    }

    private SidebarConversationItem BuildSidebarConversationItem(ConversationRecord conversation)
        => new(
            conversation.Id,
            conversation.Title,
            conversation.UpdatedAtUtc.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture),
            SelectedConversation?.Id == conversation.Id);

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

    private void PublishShell(bool autoScroll)
    {
        var transcriptMessages = GetSelectedTranscriptMessages();
        var transcriptToolRuns = GetSelectedTranscriptToolRuns();
        var transcriptToolRunAnchors = GetSelectedTranscriptToolRunAnchors();
        var agentActivities = BuildAgentActivities(transcriptToolRuns);
        var toolRunsByMessageId = TranscriptToolRunPresenter.BuildToolRunsByMessageId(
            transcriptMessages,
            transcriptToolRuns,
            transcriptToolRunAnchors);
        var orderedItems = transcriptMessages
            .OrderBy(item => item.CreatedAtUtc)
            .Select(item => BuildMessageItem(
                item,
                toolRunsByMessageId.TryGetValue(item.Id, out var toolRuns) ? toolRuns : []))
            .ToArray();

        var conversations = BuildConversationItems();

        var isBusy = IsSelectedConversationRunning();

        TranscriptChanged?.Invoke(this, new TranscriptRenderState(
            orderedItems,
            autoScroll,
            conversations,
            SelectedConversation?.Id.ToString("D"),
            EffectiveTranscriptTheme,
            isBusy));
    }

    private TranscriptConversationItem[] BuildConversationItems()
        => Conversations
            .OrderByDescending(item => item.UpdatedAtUtc)
            .ThenBy(item => item.CreatedAtUtc)
            .Select(BuildConversationItem)
            .ToArray();

    private TranscriptConversationItem BuildConversationItem(ConversationRecord conversation)
        => new(
            conversation.Id.ToString("D"),
            conversation.Title,
            conversation.UpdatedAtUtc.LocalDateTime.ToString("yyyy-MM-dd HH:mm"),
            SelectedConversation?.Id == conversation.Id,
            null,
            ResolveConversationAgentName(conversation),
            conversation.AgentId,
            ResolveConversationAgentName(conversation));

    private void PublishAgentActivities()
    {
        var items = BuildAgentActivities(GetSelectedTranscriptToolRuns());
        ReplaceCollection(AgentActivityNodes, items);
        OnPropertyChanged(nameof(HasAgentActivityNodes));
        PublishShell(false);
    }

    private static AgentActivityNode[] BuildAgentActivities(IReadOnlyList<ToolExecutionRecord> toolRuns)
    {
        var toolItems = toolRuns
            .Select(item => (Timestamp: item.UpdatedAtUtc, Node: TranscriptToolRunPresenter.BuildActivityNode(item)));

        return toolItems
            .OrderByDescending(item => item.Timestamp)
            .ThenBy(item => item.Node.Title, StringComparer.Ordinal)
            .Select(item => item.Node)
            .ToArray();
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

    private static void ReplaceCollection<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }
    }
}






