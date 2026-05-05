using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Infrastructure.Tools.Transcript;

namespace SelfClaw.Desktop.Services;

public sealed class DesktopChannelManager : IAsyncDisposable
{
    private readonly DesktopSettingsStore _settingsStore;
    private readonly IConversationRepository _conversationRepository;
    private readonly IProfileRepository _profileRepository;
    private readonly ISecretProtector _secretProtector;
    private readonly IAgentChatRuntime _agentChatRuntime;
    private readonly IConversationContextCompactionService _contextCompactionService;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<DesktopChannelManager> _logger;
    private readonly IReadOnlyList<IDesktopChannelAdapter> _adapters;
    private readonly IReadOnlyDictionary<string, IDesktopChannelAdapter> _adaptersById;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _conversationLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ChannelRuntimeSession> _runtimeSessions = new(StringComparer.OrdinalIgnoreCase);

    private bool _initialized;
    private int _disposeStarted;

    public DesktopChannelManager(
        DesktopSettingsStore settingsStore,
        IConversationRepository conversationRepository,
        IProfileRepository profileRepository,
        ISecretProtector secretProtector,
        IAgentChatRuntime agentChatRuntime,
        IConversationContextCompactionService contextCompactionService,
        IEnumerable<IDesktopChannelAdapter> adapters,
        ILoggerFactory loggerFactory,
        ILogger<DesktopChannelManager> logger)
    {
        _settingsStore = settingsStore;
        _conversationRepository = conversationRepository;
        _profileRepository = profileRepository;
        _secretProtector = secretProtector;
        _agentChatRuntime = agentChatRuntime;
        _contextCompactionService = contextCompactionService;
        _loggerFactory = loggerFactory;
        _logger = logger;
        _adapters = adapters.ToArray();
        _adaptersById = _adapters.ToDictionary(item => item.Descriptor.Id, StringComparer.OrdinalIgnoreCase);
    }

    public event EventHandler<DesktopChannelManagerEvent>? Changed;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var settings = DesktopSettings.Default;

        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            settings = _settingsStore.Load();

            foreach (var adapter in _adapters)
            {
                var configuration = adapter.NormalizeConfiguration(GetStoredConfiguration(settings, adapter.Descriptor.Id));
                _runtimeSessions[adapter.Descriptor.Id] = new ChannelRuntimeSession
                {
                    Configuration = configuration,
                    State = configuration.Enabled ? DesktopChannelRuntimeState.Starting : DesktopChannelRuntimeState.Stopped
                };
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }

        foreach (var adapter in _adapters)
        {
            var configuration = adapter.NormalizeConfiguration(GetStoredConfiguration(settings, adapter.Descriptor.Id));
            if (!configuration.Enabled)
            {
                UpdateRuntimeState(adapter.Descriptor.Id, DesktopChannelRuntimeState.Stopped, null);
                continue;
            }

            try
            {
                await StartChannelAsync(adapter, configuration, cancellationToken);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Failed to initialize channel runtime '{ChannelId}'.", adapter.Descriptor.Id);
                UpdateRuntimeState(adapter.Descriptor.Id, DesktopChannelRuntimeState.Error, exception.Message);
            }
        }

        NotifyChanged();
    }

    public IReadOnlyList<TranscriptChannelItem> BuildTranscriptChannels(IReadOnlyList<ProviderProfile> profiles)
    {
        var settings = _settingsStore.Load();
        return _adapters
            .Select(adapter =>
            {
                var configuration = adapter.NormalizeConfiguration(GetStoredConfiguration(settings, adapter.Descriptor.Id));
                var profile = configuration.ProfileId is Guid profileId
                    ? profiles.FirstOrDefault(item => item.Id == profileId)
                    : null;
                var runtime = GetRuntimeSession(adapter.Descriptor.Id, configuration);
                var statusLabel = runtime.State switch
                {
                    DesktopChannelRuntimeState.Running => "运行中",
                    DesktopChannelRuntimeState.Starting => "启动中",
                    DesktopChannelRuntimeState.Error => "异常",
                    _ => configuration.Enabled ? "已停止" : "未启用"
                };
                var statusDetail = !string.IsNullOrWhiteSpace(runtime.Detail)
                    ? runtime.Detail
                    : adapter.IsConfigured(configuration)
                        ? $"收到{adapter.Descriptor.Name}消息后会自动创建频道会话并交给 agent 处理。"
                        : "还没有完成频道配置。";

                return new TranscriptChannelItem(
                    adapter.Descriptor.Id,
                    adapter.Descriptor.Name,
                    adapter.Descriptor.Description,
                    configuration.Enabled,
                    adapter.IsConfigured(configuration),
                    runtime.State.ToString().ToLowerInvariant(),
                    statusLabel,
                    statusDetail,
                    string.IsNullOrWhiteSpace(configuration.DisplayName)
                        ? adapter.Descriptor.DefaultDisplayName
                        : configuration.DisplayName,
                    configuration.ProfileId?.ToString("D"),
                    profile?.Name,
                    adapter.BuildSummaryItems(configuration, profile),
                    BuildFieldItems(adapter, configuration));
            })
            .ToArray();
    }

    public string GetChannelName(string channelId)
        => _adaptersById.TryGetValue(channelId, out var adapter)
            ? adapter.Descriptor.Name
            : channelId;

    public async Task SaveChannelAsync(ChannelEditorResult result, CancellationToken cancellationToken = default)
    {
        var adapter = GetRequiredAdapter(result.ChannelId);
        var settings = _settingsStore.Load();
        var existing = adapter.NormalizeConfiguration(GetStoredConfiguration(settings, adapter.Descriptor.Id));
        var values = existing.Values.ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
        var secretRefs = existing.SecretRefs.ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);

        foreach (var field in adapter.Descriptor.Fields)
        {
            result.FieldValues.TryGetValue(field.Key, out var rawValue);
            var value = rawValue?.Trim() ?? string.Empty;
            if (field.Kind == DesktopChannelFieldKind.Secret)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    secretRefs[field.Key] = await _secretProtector.StoreSecretAsync(
                        value,
                        secretRefs.TryGetValue(field.Key, out var existingSecretRef) ? existingSecretRef : null);
                }

                continue;
            }

            values[field.Key] = value;
        }

        var updated = adapter.NormalizeConfiguration(existing with
        {
            DisplayName = string.IsNullOrWhiteSpace(result.DisplayName)
                ? adapter.Descriptor.DefaultDisplayName
                : result.DisplayName.Trim(),
            ProfileId = result.ProfileId,
            Values = values,
            SecretRefs = secretRefs
        });

        PersistConfiguration(adapter.Descriptor.Id, updated, settings);

        if (updated.Enabled)
        {
            await StartChannelAsync(adapter, updated, cancellationToken);
        }
        else
        {
            UpdateRuntimeConfiguration(adapter.Descriptor.Id, updated);
            NotifyChanged();
        }
    }

    public async Task SetChannelEnabledAsync(string channelId, bool enabled, CancellationToken cancellationToken = default)
    {
        var adapter = GetRequiredAdapter(channelId);
        var settings = _settingsStore.Load();
        var configuration = adapter.NormalizeConfiguration(GetStoredConfiguration(settings, adapter.Descriptor.Id));

        if (enabled)
        {
            await StartChannelAsync(adapter, configuration with { Enabled = true }, cancellationToken);
            PersistConfiguration(adapter.Descriptor.Id, configuration with { Enabled = true }, settings);
            return;
        }

        await StopChannelAsync(adapter.Descriptor.Id, cancellationToken);
        PersistConfiguration(adapter.Descriptor.Id, configuration with { Enabled = false }, settings);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        foreach (var channelId in _adapters.Select(item => item.Descriptor.Id))
        {
            await StopChannelAsync(channelId);
        }

        _lifecycleLock.Dispose();

        foreach (var conversationLock in _conversationLocks.Values)
        {
            conversationLock.Dispose();
        }
    }

    private async Task StartChannelAsync(
        IDesktopChannelAdapter adapter,
        DesktopChannelConfiguration configuration,
        CancellationToken cancellationToken)
    {
        adapter.ValidateConfiguration(configuration);

        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            await StopChannelCoreAsync(adapter.Descriptor.Id, cancellationToken, updateState: false);
            UpdateRuntimeConfiguration(adapter.Descriptor.Id, configuration);
            UpdateRuntimeState(adapter.Descriptor.Id, DesktopChannelRuntimeState.Starting, $"正在启动{adapter.Descriptor.Name}...");

            var connection = await adapter.CreateConnectionAsync(
                new DesktopChannelAdapterContext(_secretProtector, _loggerFactory),
                configuration,
                (incomingMessage, token) => HandleIncomingMessageAsync(adapter, incomingMessage, token),
                (state, detail) => UpdateRuntimeState(adapter.Descriptor.Id, state, detail),
                cancellationToken);

            try
            {
                await connection.StartAsync(cancellationToken);
                SetRuntimeConnection(adapter.Descriptor.Id, connection);
                UpdateRuntimeState(adapter.Descriptor.Id, DesktopChannelRuntimeState.Running, $"{adapter.Descriptor.Name}连接已建立。");
            }
            catch (Exception)
            {
                await connection.DisposeAsync();
                SetRuntimeConnection(adapter.Descriptor.Id, null);
                throw;
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }

        NotifyChanged();
    }

    private async Task StopChannelAsync(string channelId, CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            await StopChannelCoreAsync(channelId, cancellationToken, updateState: true);
        }
        finally
        {
            _lifecycleLock.Release();
        }

        NotifyChanged();
    }

    private async Task StopChannelCoreAsync(
        string channelId,
        CancellationToken cancellationToken,
        bool updateState)
    {
        var runtime = GetRuntimeSession(channelId, DesktopChannelConfiguration.Default);
        var connection = runtime.Connection;
        runtime.Connection = null;

        if (connection is not null)
        {
            try
            {
                await connection.StopAsync(cancellationToken);
            }
            catch
            {
                // ignore stop failures during shutdown or restart
            }
            finally
            {
                await connection.DisposeAsync();
            }
        }

        if (updateState)
        {
            UpdateRuntimeState(channelId, DesktopChannelRuntimeState.Stopped, $"{GetChannelName(channelId)}连接已停止。");
        }
    }

    private async Task HandleIncomingMessageAsync(
        IDesktopChannelAdapter adapter,
        DesktopChannelIncomingMessage incomingMessage,
        CancellationToken cancellationToken)
    {
        var conversationLock = _conversationLocks.GetOrAdd(
            $"{adapter.Descriptor.Id}:{incomingMessage.ConversationId}",
            _ => new SemaphoreSlim(1, 1));

        await conversationLock.WaitAsync(cancellationToken);
        try
        {
            await ProcessIncomingMessageCoreAsync(adapter, incomingMessage, cancellationToken);
        }
        finally
        {
            conversationLock.Release();
        }
    }

    private async Task ProcessIncomingMessageCoreAsync(
        IDesktopChannelAdapter adapter,
        DesktopChannelIncomingMessage incomingMessage,
        CancellationToken cancellationToken)
    {
        ConversationRecord? conversation = null;
        try
        {
            var settings = _settingsStore.Load();
            var configuration = adapter.NormalizeConfiguration(GetStoredConfiguration(settings, adapter.Descriptor.Id));
            var profileId = configuration.ProfileId
                ?? throw new InvalidOperationException($"{adapter.Descriptor.Name}频道还没有绑定模型配置。");
            var profile = await _profileRepository.GetProfileAsync(profileId, cancellationToken)
                ?? throw new InvalidOperationException($"{adapter.Descriptor.Name}频道绑定的模型配置不存在，请重新选择。");

            var apiKey = await _secretProtector.RetrieveSecretAsync(profile.SecretRef, cancellationToken);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException($"模型配置 '{profile.Name}' 的 API Key 无法读取。");
            }

            conversation = await EnsureChannelConversationAsync(adapter, configuration, incomingMessage, profile, cancellationToken);

            var userMessage = new MessageRecord(
                Guid.NewGuid(),
                conversation.Id,
                MessageRole.User,
                adapter.BuildUserMessageMarkdown(incomingMessage),
                MessageStatus.Completed,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow);
            await _conversationRepository.UpsertMessageAsync(userMessage, cancellationToken);
            NotifyChanged(conversation.Id);

            var rawRequestMessages = await _conversationRepository.ListMessagesAsync(conversation.Id, cancellationToken);
            var requestMessages = await _contextCompactionService.PrepareMessagesAsync(
                conversation.Id,
                profile,
                apiKey,
                rawRequestMessages,
                settings.ModelContextWindow,
                settings.ModelAutoCompactTokenLimit,
                cancellationToken);
            var streamingReply = default(IDesktopChannelStreamingReply);
            var streamedMarkdown = new StringBuilder();
            var connection = GetRuntimeSession(adapter.Descriptor.Id, configuration).Connection;

            await foreach (var update in _agentChatRuntime.StreamTurnAsync(
                               new ChatTurnRequest(
                                   conversation.Id,
                                   profile,
                                   apiKey,
                                   null,
                                   ConversationMode.Channel,
                                   new AgentRuntimeDefinition(
                                       DesktopAgentStore.BuildAgentId,
                                       DesktopAgentStore.BuildAgentId,
                                       "通用代理（默认）",
                                       AgentExecutionMode.Direct,
                                       AgentRuntimeDefinition.SystemToolPolicy,
                                       [],
                                       [],
                                       string.Empty),
                                   ToolPermissionMode.FullAccess,
                                   null,
                                   requestMessages),
                               cancellationToken))
            {
                switch (update)
                {
                    case AssistantMessageStartedEvent:
                        if (connection is not null && streamingReply is null)
                        {
                            streamingReply = await connection.CreateStreamingReplyAsync(incomingMessage, cancellationToken);
                        }

                        break;
                    case AssistantDeltaEvent delta:
                        streamedMarkdown.Append(delta.DeltaMarkdown);
                        if (streamingReply is not null)
                        {
                            await streamingReply.UpdateAsync(ExtractChannelReply(streamedMarkdown.ToString()), cancellationToken);
                        }

                        break;
                    case AssistantMessageCompletedEvent completed:
                        await _conversationRepository.UpsertMessageAsync(completed.Message, cancellationToken);
                        await TouchConversationAsync(conversation, completed.Message.UpdatedAtUtc, cancellationToken);

                        var replyContent = ExtractChannelReply(completed.Message.MarkdownContent);
                        if (streamingReply is not null)
                        {
                            await streamingReply.FinishAsync(replyContent, cancellationToken);
                        }
                        else if (connection is not null)
                        {
                            await connection.ReplyAsync(incomingMessage, replyContent, cancellationToken);
                        }

                        NotifyChanged(conversation.Id);
                        break;
                }
            }

            await TryCompactChannelContextAfterSuccessfulTurnAsync(
                conversation,
                profile,
                apiKey,
                settings,
                cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(exception, "Failed to process channel message for '{ChannelId}'.", adapter.Descriptor.Id);
            UpdateRuntimeState(adapter.Descriptor.Id, DesktopChannelRuntimeState.Error, exception.Message);

            if (conversation is not null)
            {
                var now = DateTimeOffset.UtcNow;
                var failureNote = new MessageRecord(
                    Guid.NewGuid(),
                    conversation.Id,
                    MessageRole.System,
                    $"频道处理失败：{exception.Message}",
                    MessageStatus.Completed,
                    now,
                    now);
                await _conversationRepository.UpsertMessageAsync(failureNote, cancellationToken);
                await TouchConversationAsync(conversation, now, cancellationToken);
                NotifyChanged(conversation.Id);
            }

            var connection = GetRuntimeSession(adapter.Descriptor.Id, DesktopChannelConfiguration.Default).Connection;
            if (connection is not null)
            {
                try
                {
                    await connection.ReplyAsync(
                        incomingMessage,
                        $"SelfClaw 暂时无法处理这条消息：{exception.Message}",
                        cancellationToken);
                }
                catch (Exception replyException)
                {
                    _logger.LogWarning(replyException, "Failed to send failure reply for '{ChannelId}'.", adapter.Descriptor.Id);
                }
            }
        }
    }

    private async Task TryCompactChannelContextAfterSuccessfulTurnAsync(
        ConversationRecord conversation,
        ProviderProfile profile,
        string apiKey,
        DesktopSettings settings,
        CancellationToken cancellationToken)
    {
        try
        {
            var rawMessages = await _conversationRepository.ListMessagesAsync(conversation.Id, cancellationToken);
            await _contextCompactionService.PrepareMessagesAsync(
                conversation.Id,
                profile,
                apiKey,
                rawMessages,
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
                "Post-turn channel context compaction failed. ConversationId={ConversationId}",
                conversation.Id);
        }
    }

    private async Task<ConversationRecord> EnsureChannelConversationAsync(
        IDesktopChannelAdapter adapter,
        DesktopChannelConfiguration configuration,
        DesktopChannelIncomingMessage incomingMessage,
        ProviderProfile profile,
        CancellationToken cancellationToken)
    {
        var existingConversation = (await _conversationRepository.ListConversationsAsync(cancellationToken))
            .FirstOrDefault(item =>
                item.Mode == ConversationMode.Channel &&
                string.Equals(item.ChannelKind, adapter.Descriptor.Id, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.ChannelConversationId, incomingMessage.ConversationId, StringComparison.Ordinal));

        var now = DateTimeOffset.UtcNow;
        var conversationTitle = adapter.BuildConversationTitle(configuration, incomingMessage);
        var conversation = existingConversation is null
            ? new ConversationRecord(
                Guid.NewGuid(),
                conversationTitle,
                profile.Id,
                null,
                ConversationMode.Channel,
                ToolPermissionMode.FullAccess,
                DesktopAgentStore.BuildAgentId,
                now,
                now,
                ChannelKind: adapter.Descriptor.Id,
                ChannelConversationId: incomingMessage.ConversationId,
                ChannelDisplayName: configuration.DisplayName)
            : existingConversation with
            {
                Title = conversationTitle,
                ProfileId = profile.Id,
                WorkspaceRootId = null,
                Mode = ConversationMode.Channel,
                ToolPermissionMode = ToolPermissionMode.FullAccess,
                AgentId = DesktopAgentStore.BuildAgentId,
                UpdatedAtUtc = now,
                ChannelKind = adapter.Descriptor.Id,
                ChannelConversationId = incomingMessage.ConversationId,
                ChannelDisplayName = configuration.DisplayName
            };

        return await _conversationRepository.UpsertConversationAsync(conversation, cancellationToken);
    }

    private async Task TouchConversationAsync(
        ConversationRecord conversation,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken)
    {
        await _conversationRepository.UpsertConversationAsync(
            conversation with
            {
                UpdatedAtUtc = updatedAtUtc
            },
            cancellationToken);
    }

    private void PersistConfiguration(
        string channelId,
        DesktopChannelConfiguration configuration,
        DesktopSettings settings)
    {
        var items = settings.Channels.Items.ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
        items[channelId] = configuration;
        _settingsStore.Save(settings with
        {
            Channels = new DesktopChannelSettings
            {
                Items = items
            }
        });

        UpdateRuntimeConfiguration(channelId, configuration);
        NotifyChanged();
    }

    private IReadOnlyList<TranscriptChannelFieldItem> BuildFieldItems(
        IDesktopChannelAdapter adapter,
        DesktopChannelConfiguration configuration)
    {
        return adapter.Descriptor.Fields
            .Select(field =>
            {
                var value = field.Kind == DesktopChannelFieldKind.Secret
                    ? string.Empty
                    : configuration.Values.TryGetValue(field.Key, out var currentValue)
                        ? currentValue
                        : string.Empty;
                var hasValue = field.Kind == DesktopChannelFieldKind.Secret
                    ? configuration.SecretRefs.ContainsKey(field.Key)
                    : !string.IsNullOrWhiteSpace(value);

                return new TranscriptChannelFieldItem(
                    field.Key,
                    field.Label,
                    FieldKindToId(field.Kind),
                    field.Required,
                    field.Description,
                    field.Placeholder,
                    value,
                    hasValue);
            })
            .ToArray();
    }

    private DesktopChannelConfiguration GetStoredConfiguration(DesktopSettings settings, string channelId)
        => settings.Channels.Items.TryGetValue(channelId, out var configuration)
            ? configuration
            : DesktopChannelConfiguration.Default;

    private IDesktopChannelAdapter GetRequiredAdapter(string channelId)
        => _adaptersById.TryGetValue(channelId, out var adapter)
            ? adapter
            : throw new InvalidOperationException($"Unsupported channel '{channelId}'.");

    private ChannelRuntimeSession GetRuntimeSession(string channelId, DesktopChannelConfiguration fallbackConfiguration)
        => _runtimeSessions.GetOrAdd(
            channelId,
            _ => new ChannelRuntimeSession
            {
                Configuration = fallbackConfiguration
            });

    private void UpdateRuntimeConfiguration(string channelId, DesktopChannelConfiguration configuration)
    {
        var runtime = GetRuntimeSession(channelId, configuration);
        runtime.Configuration = configuration;
    }

    private void SetRuntimeConnection(string channelId, IDesktopChannelConnection? connection)
    {
        var runtime = GetRuntimeSession(channelId, DesktopChannelConfiguration.Default);
        runtime.Connection = connection;
    }

    private void UpdateRuntimeState(string channelId, DesktopChannelRuntimeState state, string? detail)
    {
        var runtime = GetRuntimeSession(channelId, DesktopChannelConfiguration.Default);
        runtime.State = state;
        runtime.Detail = detail;
        NotifyChanged();
    }

    private void NotifyChanged(Guid? conversationId = null)
        => Changed?.Invoke(this, new DesktopChannelManagerEvent(conversationId));

    private static string FieldKindToId(DesktopChannelFieldKind kind)
        => kind switch
        {
            DesktopChannelFieldKind.Secret => "secret",
            DesktopChannelFieldKind.Multiline => "multiline",
            _ => "text"
        };

    private static string ExtractChannelReply(string markdown)
    {
        var content = AssistantMessageSegmenter.Split(markdown).ContentMarkdown.Trim();
        return string.IsNullOrWhiteSpace(content) ? "我已经收到消息了。" : content;
    }

    private sealed class ChannelRuntimeSession
    {
        public DesktopChannelConfiguration Configuration { get; set; } = DesktopChannelConfiguration.Default;

        public IDesktopChannelConnection? Connection { get; set; }

        public DesktopChannelRuntimeState State { get; set; }

        public string? Detail { get; set; }
    }
}
