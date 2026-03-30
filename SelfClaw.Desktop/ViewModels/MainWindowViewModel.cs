using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
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

public sealed class MainWindowViewModel : ObservableObject
{
    private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };
    private static readonly ShellSelectOption[] ToolPermissionOptions =
    [
        new("requireApproval", "默认权限", "写文件和命令执行前需要人工确认"),
        new("fullAccess", "完全访问权限", "允许 agent 直接写文件并执行 PowerShell")
    ];

    private readonly IConversationRepository _conversationRepository;
    private readonly IProfileRepository _profileRepository;
    private readonly ISecretProtector _secretProtector;
    private readonly IAgentChatRuntime _agentChatRuntime;
    private readonly DesktopToolApprovalHandler _toolApprovalHandler;
    private readonly MarkdownHtmlRenderer _markdownHtmlRenderer;
    private readonly DesktopSettingsStore _desktopSettingsStore;
    private readonly ILogger<MainWindowViewModel> _logger;

    private readonly List<ConversationRecord> _allConversations = [];
    private readonly List<MessageRecord> _messages = [];
    private readonly List<ToolExecutionRecord> _toolRuns = [];
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
    private ToolPermissionMode _selectedToolPermissionMode = ToolPermissionMode.RequireApproval;

    public MainWindowViewModel(
        IConversationRepository conversationRepository,
        IProfileRepository profileRepository,
        ISecretProtector secretProtector,
        IAgentChatRuntime agentChatRuntime,
        DesktopToolApprovalHandler toolApprovalHandler,
        MarkdownHtmlRenderer markdownHtmlRenderer,
        DesktopSettingsStore desktopSettingsStore,
        ILogger<MainWindowViewModel> logger)
    {
        _conversationRepository = conversationRepository;
        _profileRepository = profileRepository;
        _secretProtector = secretProtector;
        _agentChatRuntime = agentChatRuntime;
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
                _ = LoadConversationAsync(value);
            }
            else
            {
                _messages.Clear();
                _toolRuns.Clear();
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
            SelectedToolPermissionMode,
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
        var toolRuns = await _conversationRepository.ListToolExecutionsAsync(conversation.Id);
        if (version != _selectionVersion)
        {
            return;
        }

        _messages.Clear();
        _messages.AddRange(messages);
        _toolRuns.Clear();
        _toolRuns.AddRange(toolRuns);

        SelectedProfile = Profiles.FirstOrDefault(profile => profile.Id == conversation.ProfileId) ?? SelectedProfile;
        SelectedWorkspaceRoot = conversation.WorkspaceRootId is Guid workspaceRootId
            ? WorkspaceRoots.FirstOrDefault(root => root.Id == workspaceRootId)
            : null;
        SelectedToolPermissionMode = conversation.ToolPermissionMode;
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

        MessageRecord? assistantMessage = null;
        var accumulatedResponse = new StringBuilder();
        var pendingBuffer = new StringBuilder();
        var flushWatch = Stopwatch.StartNew();

        try
        {
            var conversation = await EnsureConversationAsync();
            conversation = conversation with
            {
                ProfileId = SelectedProfile.Id,
                WorkspaceRootId = SelectedWorkspaceRoot?.Id,
                ToolPermissionMode = SelectedToolPermissionMode,
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

            assistantMessage = new MessageRecord(
                Guid.NewGuid(),
                conversation.Id,
                MessageRole.Assistant,
                string.Empty,
                MessageStatus.Streaming,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow);
            _messages.Add(assistantMessage);
            await _conversationRepository.UpsertMessageAsync(assistantMessage);
            PublishShell(true);

            var apiKey = await _secretProtector.RetrieveSecretAsync(SelectedProfile.SecretRef, _turnCancellationSource.Token);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException("The selected profile does not have a readable API key.");
            }

            var requestMessages = _messages.Where(message => message.Id != assistantMessage.Id).ToArray();
            ChatRuntimeCompletedEvent? completion = null;

            await foreach (var update in _agentChatRuntime.StreamTurnAsync(
                               new ChatTurnRequest(
                                   conversation.Id,
                                   SelectedProfile,
                                   apiKey,
                                   SelectedWorkspaceRoot,
                                   SelectedToolPermissionMode,
                                   _toolApprovalHandler,
                                   requestMessages),
                               _turnCancellationSource.Token))
            {
                switch (update)
                {
                    case AssistantDeltaEvent delta:
                        pendingBuffer.Append(delta.DeltaMarkdown);
                        if (flushWatch.ElapsedMilliseconds >= 33)
                        {
                            FlushAssistantDelta();
                        }
                        break;
                    case ToolExecutionStartedEvent toolStarted:
                        UpsertToolRun(toolStarted.Record);
                        await _conversationRepository.UpsertToolExecutionAsync(toolStarted.Record);
                        PublishAgentActivities();
                        break;
                    case ToolExecutionCompletedEvent toolCompleted:
                        UpsertToolRun(toolCompleted.Record);
                        await _conversationRepository.UpsertToolExecutionAsync(toolCompleted.Record);
                        PublishAgentActivities();
                        break;
                    case ChatRuntimeCompletedEvent completed:
                        completion = completed;
                        break;
                }
            }

            FlushAssistantDelta();
            var finalMarkdown = accumulatedResponse.ToString();
            if (!string.IsNullOrWhiteSpace(completion?.FinalMarkdown) &&
                (string.IsNullOrWhiteSpace(finalMarkdown) || completion.FinalMarkdown.Length > finalMarkdown.Length))
            {
                finalMarkdown = completion.FinalMarkdown;
            }

            assistantMessage = assistantMessage with
            {
                MarkdownContent = finalMarkdown,
                Status = MessageStatus.Completed,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                DurationMs = completion?.Duration.TotalMilliseconds,
                InputTokens = completion?.InputTokens,
                OutputTokens = completion?.OutputTokens,
                ErrorMessage = null
            };
            ReplaceMessage(assistantMessage);
            await _conversationRepository.UpsertMessageAsync(assistantMessage);
            StatusText = "Ready.";
            PublishShell(true);
        }
        catch (OperationCanceledException)
        {
            if (assistantMessage is not null)
            {
                FlushAssistantDelta();
                assistantMessage = assistantMessage with
                {
                    MarkdownContent = accumulatedResponse.ToString(),
                    Status = MessageStatus.Failed,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                    ErrorMessage = "Generation stopped."
                };
                ReplaceMessage(assistantMessage);
                await _conversationRepository.UpsertMessageAsync(assistantMessage);
            }

            StatusText = "Generation stopped.";
            PublishShell(true);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Chat turn failed.");
            if (assistantMessage is not null)
            {
                FlushAssistantDelta();
                assistantMessage = assistantMessage with
                {
                    MarkdownContent = accumulatedResponse.ToString(),
                    Status = MessageStatus.Failed,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                    ErrorMessage = exception.Message
                };
                ReplaceMessage(assistantMessage);
                await _conversationRepository.UpsertMessageAsync(assistantMessage);
            }

            StatusText = exception.Message;
            PublishShell(true);
        }
        finally
        {
            _turnCancellationSource?.Dispose();
            _turnCancellationSource = null;
            IsBusy = false;
        }

        void FlushAssistantDelta()
        {
            if (pendingBuffer.Length == 0 || assistantMessage is null)
            {
                return;
            }

            var delta = pendingBuffer.ToString();
            pendingBuffer.Clear();
            flushWatch.Restart();
            accumulatedResponse.Append(delta);
            assistantMessage = assistantMessage with
            {
                MarkdownContent = accumulatedResponse.ToString(),
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            ReplaceMessage(assistantMessage);
            PublishShell(true);
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
            ToolPermissionMode = SelectedToolPermissionMode,
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
        => SelectedWorkspaceRoot is null
            ? _allConversations
            : _allConversations.Where(item => item.WorkspaceRootId == SelectedWorkspaceRoot.Id);

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
        var orderedItems = _messages
            .OrderBy(item => item.CreatedAtUtc)
            .Select(BuildMessageItem)
            .ToArray();

        var conversations = Conversations
            .Select(BuildConversationItem)
            .ToArray();

        var profiles = Profiles
            .Select(profile => new ShellSelectOption(profile.Id.ToString("D"), profile.Name, profile.Endpoint))
            .ToArray();

        var workspaceRoots = WorkspaceRoots
            .Select(root => new ShellSelectOption(root.Id.ToString("D"), root.Name, root.RootPath))
            .ToArray();

        var toolPermissionModes = ToolPermissionOptions;

        var themeOptions = ThemeOptions
            .Select(option => new ShellSelectOption(ThemePreferenceToId(option.Value), option.Label))
            .ToArray();

        TranscriptChanged?.Invoke(this, new TranscriptRenderState(
            orderedItems,
            autoScroll,
            conversations,
            SelectedConversation?.Id.ToString("D"),
            EffectiveTranscriptTheme,
            profiles,
            SelectedProfile?.Id.ToString("D"),
            SelectedProfile?.Model,
            workspaceRoots,
            SelectedWorkspaceRoot?.Id.ToString("D"),
            toolPermissionModes,
            ToolPermissionModeToId(SelectedToolPermissionMode),
            themeOptions,
            ThemePreferenceToId(SelectedThemeOption?.Value ?? AppThemePreference.System),
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
        var items = _toolRuns
            .OrderByDescending(item => item.CreatedAtUtc)
            .Select(BuildToolActivityNode)
            .ToArray();

        ReplaceCollection(AgentActivityNodes, items);
        OnPropertyChanged(nameof(HasAgentActivityNodes));
        PublishShell(false);
    }

    private TranscriptRenderItem BuildMessageItem(MessageRecord message)
    {
        var contentMarkdown = message.MarkdownContent;
        string? thinkingHtml = null;

        if (message.Role == MessageRole.Assistant)
        {
            var segments = AssistantMessageSegmenter.Split(message.MarkdownContent);
            contentMarkdown = segments.ContentMarkdown;
            if (segments.HasThinking)
            {
                thinkingHtml = _markdownHtmlRenderer.ToHtml(segments.ThinkingMarkdown!);
            }
        }

        var rendered = string.IsNullOrWhiteSpace(contentMarkdown)
            ? string.Empty
            : _markdownHtmlRenderer.ToHtml(contentMarkdown);

        if (message.Status == MessageStatus.Failed && !string.IsNullOrWhiteSpace(message.ErrorMessage))
        {
            rendered += $"<p class=\"message-error\">{WebUtility.HtmlEncode(message.ErrorMessage)}</p>";
        }

        return new TranscriptRenderItem(
            message.Id.ToString("D"),
            "message",
            message.Role.ToString().ToLowerInvariant(),
            message.Status.ToString().ToLowerInvariant(),
            message.Role switch
            {
                MessageRole.User => string.Empty,
                MessageRole.Assistant => string.Empty,
                _ => "System"
            },
            rendered,
            thinkingHtml,
            message.Role == MessageRole.Assistant && message.Status == MessageStatus.Streaming,
            null,
            message.ErrorMessage,
            message.DurationMs,
            message.CreatedAtUtc.LocalDateTime.ToString("yyyy-MM-dd HH:mm"));
    }

    private static AgentActivityNode BuildToolActivityNode(ToolExecutionRecord toolRun)
        => new(
            toolRun.Id.ToString("D"),
            "tool",
            "Tool call",
            toolRun.Status.ToString().ToLowerInvariant(),
            toolRun.Status switch
            {
                ToolExecutionStatus.Running => "Running",
                ToolExecutionStatus.AwaitingApproval => "Awaiting approval",
                ToolExecutionStatus.Completed => "Completed",
                ToolExecutionStatus.Failed => "Failed",
                ToolExecutionStatus.Cancelled => "Cancelled",
                _ => "Updated"
            },
            HumanizeToolName(toolRun.ToolName),
            BuildToolSummary(toolRun),
            toolRun.CreatedAtUtc.LocalDateTime.ToString("yyyy-MM-dd HH:mm"),
            [
                new AgentActivityDetail("Tool", toolRun.ToolName),
                new AgentActivityDetail("Arguments", PrettyPrintJson(toolRun.ArgumentsJson), true),
                new AgentActivityDetail("Result", BuildToolResultText(toolRun)),
                new AgentActivityDetail("Duration", FormatToolDuration(toolRun))
            ]);

    private static string BuildToolSummary(ToolExecutionRecord toolRun)
        => toolRun.Status switch
        {
            ToolExecutionStatus.Running => "Waiting for the tool result.",
            ToolExecutionStatus.AwaitingApproval => toolRun.ResultSummary ?? "Waiting for your confirmation.",
            ToolExecutionStatus.Completed => toolRun.ResultSummary ?? "Tool call completed.",
            ToolExecutionStatus.Failed => toolRun.ResultSummary ?? "Tool call failed.",
            ToolExecutionStatus.Cancelled => toolRun.ResultSummary ?? "Tool call was cancelled.",
            _ => toolRun.ResultSummary ?? "Tool activity updated."
        };

    private static string BuildToolResultText(ToolExecutionRecord toolRun)
        => toolRun.Status switch
        {
            ToolExecutionStatus.Running => "Pending result.",
            ToolExecutionStatus.AwaitingApproval => toolRun.ResultSummary ?? "Waiting for approval.",
            ToolExecutionStatus.Completed => toolRun.ResultSummary ?? "Completed without a stored summary.",
            ToolExecutionStatus.Failed => toolRun.ResultSummary ?? "Failed without an error summary.",
            ToolExecutionStatus.Cancelled => toolRun.ResultSummary ?? "Cancelled without a stored summary.",
            _ => toolRun.ResultSummary ?? "No result captured."
        };

    private static string FormatToolDuration(ToolExecutionRecord toolRun)
        => toolRun.DurationMs is null
            ? toolRun.Status is ToolExecutionStatus.Running or ToolExecutionStatus.AwaitingApproval ? "In progress" : "n/a"
            : $"{Math.Round(toolRun.DurationMs.Value)} ms";

    private static string PrettyPrintJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return "{}";
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement, IndentedJsonOptions);
        }
        catch
        {
            return json;
        }
    }

    private static string HumanizeToolName(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return "Tool call";
        }

        var spaced = toolName.Replace('_', ' ').Trim().ToLowerInvariant();
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(spaced);
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



