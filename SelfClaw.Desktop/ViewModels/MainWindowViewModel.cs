using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
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
    private const string AttachmentHostName = "attachments.selfclaw.local";
    private readonly IConversationRepository _conversationRepository;
    private readonly IProfileRepository _profileRepository;
    private readonly ISecretProtector _secretProtector;
    private readonly IAgentChatRuntime _agentChatRuntime;
    private readonly DesktopToolApprovalHandler _toolApprovalHandler;
    private readonly DesktopNotificationService _desktopNotificationService;
    private readonly MarkdownHtmlRenderer _markdownHtmlRenderer;
    private readonly DesktopAgentStore _desktopAgentStore;
    private readonly StoragePaths _storagePaths;
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly DispatcherTimer? _streamingPublishTimer;

    private readonly List<ConversationRecord> _allConversations = [];
    private readonly List<ConversationRecord> _filteredConversations = [];
    private readonly List<DesktopAgentDefinition> _agents = [];
    private readonly List<ProviderProfile> _profiles = [];
    private readonly List<WorkspaceRoot> _workspaceRoots = [];
    private readonly List<MessageRecord> _messages = [];
    private readonly List<ToolExecutionRecord> _toolRuns = [];
    private readonly Dictionary<Guid, ToolRunAnchor> _toolRunAnchors = [];
    private readonly Dictionary<Guid, ConversationRuntimeState> _conversationRuntimeStates = [];
    private IReadOnlyList<PromptImageAttachment> _pendingPromptImageAttachments = [];
    private bool _pendingReasoningEnabled;
    private bool _initialized;
    private int _selectionVersion;
    private ConversationRecord? _selectedConversation;
    private ProviderProfile? _selectedProfile;
    private string? _selectedProfileModelOverride;
    private WorkspaceRoot? _selectedWorkspaceRoot;
    private string _selectedAgentId = DesktopAgentStore.BuildAgentId;
    private string _composerText = string.Empty;
    private ThemeMode _activeThemeMode = ThemeMode.System;
    private string _effectiveTranscriptTheme = "light";
    private ToolPermissionMode _selectedToolPermissionMode = ToolPermissionMode.RequireApproval;
    private bool _pendingStreamingPublish;
    private bool _pendingStreamingAutoScroll;
    private string? _lastPublishedShellFingerprint;
    private DateTimeOffset _lastStreamingPublishAtUtc = DateTimeOffset.MinValue;
    private int _disposeStarted;

    public MainWindowViewModel(
        IConversationRepository conversationRepository,
        IProfileRepository profileRepository,
        ISecretProtector secretProtector,
        IAgentChatRuntime agentChatRuntime,
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

    }

    public event EventHandler<TranscriptRenderState>? TranscriptChanged;

    public ThemeMode ActiveThemeMode
    {
        get => _activeThemeMode;
        private set => SetProperty(ref _activeThemeMode, value);
    }

    public string? SelectedWorkspaceRootPath => _selectedWorkspaceRoot?.RootPath;

    public string EffectiveTranscriptTheme
    {
        get => _effectiveTranscriptTheme;
        private set => SetProperty(ref _effectiveTranscriptTheme, value);
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

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        ReloadAgents();
        await ReloadProfilesAsync();
        await ReloadWorkspaceRootsAsync();
        await ReloadConversationsAsync();

        if (_selectedProfile is null && _profiles.Count > 0)
        {
            SelectProfile(_profiles[0], publishShell: false);
        }

        PublishShell(false);
    }

    public async Task SubmitPromptAsync(
        string prompt,
        IReadOnlyList<PromptImageAttachment>? imageAttachments = null,
        bool enableReasoning = false,
        string? profileModel = null)
    {
        ApplySelectedProfileModel(profileModel, publishShell: false);
        _composerText = prompt;
        _pendingPromptImageAttachments = imageAttachments ?? [];
        _pendingReasoningEnabled = enableReasoning;
        await SendAsync();
    }

    public void StopGeneration()
    {
        Stop();
    }

    public Task ApproveToolExecutionAsync(Guid toolExecutionId)
    {
        _toolApprovalHandler.TryResolve(toolExecutionId, approved: true);

        return Task.CompletedTask;
    }

    public Task RejectToolExecutionAsync(Guid toolExecutionId)
    {
        _toolApprovalHandler.TryResolve(toolExecutionId, approved: false);

        return Task.CompletedTask;
    }

    private void ApplySelectedProfileModel(string? profileModel, bool publishShell)
    {
        var normalized = NormalizeModelValue(profileModel);
        if (_selectedProfile is null)
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
        if (_selectedProfile is null)
        {
            return null;
        }

        if (_selectedProfileModelOverride is not null)
        {
            return _selectedProfileModelOverride;
        }

        var fallback = NormalizeModelValue(_selectedProfile.Model);
        return fallback;
    }

    private void SelectProfile(ProviderProfile? profile, bool publishShell)
    {
        if (_selectedProfile?.Id == profile?.Id)
        {
            return;
        }

        _selectedProfile = profile;
        _selectedProfileModelOverride = null;
        if (publishShell)
        {
            PublishShell(false);
        }
    }

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
        var selectedId = _selectedProfile?.Id;
        var profiles = await _profileRepository.ListProfilesAsync();
        ReplaceList(_profiles, profiles);
        SelectProfile(
            profiles.FirstOrDefault(profile => profile.Id == selectedId) ?? profiles.FirstOrDefault(),
            publishShell: false);
    }

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

        SelectProfile(_profiles.FirstOrDefault(profile => profile.Id == conversation.ProfileId) ?? _selectedProfile, publishShell: false);
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

    private async Task SendAsync()
    {
        var selectedProfile = _selectedProfile;
        if (selectedProfile is null)
        {
            return;
        }

        var prompt = _composerText.Trim();
        var useReasoning = _pendingReasoningEnabled;
        var promptImageAttachments = _pendingPromptImageAttachments.ToArray();
        var selectedProfileModel = ResolveSelectedProfileModel();
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
        ProviderProfile? requestProfile = null;
        string? apiKey = null;

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
                ProfileId = selectedProfile.Id,
                WorkspaceRootId = selectedWorkspaceRoot?.Id,
                Mode = ConversationMode.Programming,
                ToolPermissionMode = selectedToolPermissionMode,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            await PersistConversationAsync(conversation, preferSelection: preferConversationSelection);

            var runtimeAgent = ResolveRuntimeAgent(conversation.AgentId);

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
                await PersistConversationAsync(conversation, preferSelection: preferConversationSelection);
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

            var requestMessages = runtimeState.Messages.ToArray();

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
                        PublishShell(false);
                        break;
                    case ToolExecutionCompletedEvent toolCompleted:
                        var completedRecord = CaptureToolRunAnchor(runtimeState, toolCompleted.Record);
                        UpsertToolRun(runtimeState, completedRecord);
                        await _conversationRepository.UpsertToolExecutionAsync(completedRecord);
                        PublishShell(false);
                        break;
                    case AssistantMessageCompletedEvent completed:
                        runtimeState.ActiveMessageIds.Remove(completed.Message.Id);
                        await CompleteAssistantMessageAsync(runtimeState, completed.Message);
                        break;
                }
            }

            PublishConversationCompletedNotification(conversation, runtimeState.Messages);
        }
        catch (OperationCanceledException) when (runtimeState?.CancellationTokenSource.IsCancellationRequested == true)
        {
            if (runtimeState is not null)
            {
                await FailActiveMessagesAsync(runtimeState, runtimeState.ActiveMessageIds, "Generation stopped.");
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Chat turn failed.");
            if (runtimeState is null)
            {
                return;
            }

            await FailActiveMessagesAsync(runtimeState, runtimeState.ActiveMessageIds, exception.Message);
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

        await Task.CompletedTask;
        return CreateConversationRecord();
    }

    private ConversationRecord CreateConversationRecord()
    {
        if (_selectedProfile is null)
        {
            throw new InvalidOperationException("Create a profile first.");
        }

        var now = DateTimeOffset.UtcNow;
        return new ConversationRecord(
            Guid.NewGuid(),
            "New chat",
            _selectedProfile.Id,
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

    private void PublishShell(bool autoScroll)
    {
        var transcriptMessages = GetSelectedTranscriptMessages();
        var transcriptToolRuns = GetSelectedTranscriptToolRuns();
        var transcriptToolRunAnchors = GetSelectedTranscriptToolRunAnchors();
        var fingerprint = BuildShellFingerprint(
            autoScroll,
            transcriptMessages,
            transcriptToolRuns,
            transcriptToolRunAnchors);
        if (string.Equals(_lastPublishedShellFingerprint, fingerprint, StringComparison.Ordinal))
        {
            return;
        }

        _lastPublishedShellFingerprint = fingerprint;

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

    private string BuildShellFingerprint(
        bool autoScroll,
        IReadOnlyList<MessageRecord> transcriptMessages,
        IReadOnlyList<ToolExecutionRecord> transcriptToolRuns,
        IReadOnlyDictionary<Guid, ToolRunAnchor> transcriptToolRunAnchors)
    {
        var builder = new StringBuilder();
        builder.Append(autoScroll ? '1' : '0')
            .Append('|')
            .Append(SelectedConversation?.Id.ToString("D"))
            .Append('|')
            .Append(EffectiveTranscriptTheme)
            .Append('|')
            .Append(IsSelectedConversationRunning() ? '1' : '0')
            .Append('|')
            .Append(_filteredConversations.Count)
            .Append('|');

        foreach (var conversation in _filteredConversations.OrderByDescending(item => item.UpdatedAtUtc).ThenBy(item => item.CreatedAtUtc))
        {
            builder.Append(conversation.Id.ToString("D"))
                .Append(':')
                .Append(conversation.UpdatedAtUtc.UtcTicks)
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
        => _filteredConversations
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

}






