using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Infrastructure.Channels.Feishu;
using SelfClaw.Infrastructure.Tools;

namespace SelfClaw.Desktop.Services;

public sealed class DesktopChannelManager : IAsyncDisposable
{
    private const string FeishuChannelId = "feishu";

    private readonly DesktopSettingsStore _settingsStore;
    private readonly IConversationRepository _conversationRepository;
    private readonly IProfileRepository _profileRepository;
    private readonly ISecretProtector _secretProtector;
    private readonly IAgentChatRuntime _agentChatRuntime;
    private readonly ILogger<DesktopChannelManager> _logger;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _conversationLocks = new(StringComparer.OrdinalIgnoreCase);

    private FeishuBotService? _feishuBotService;
    private DesktopChannelRuntimeState _feishuRuntimeState = DesktopChannelRuntimeState.Stopped;
    private string? _feishuRuntimeDetail;
    private bool _initialized;

    public DesktopChannelManager(
        DesktopSettingsStore settingsStore,
        IConversationRepository conversationRepository,
        IProfileRepository profileRepository,
        ISecretProtector secretProtector,
        IAgentChatRuntime agentChatRuntime,
        ILogger<DesktopChannelManager> logger)
    {
        _settingsStore = settingsStore;
        _conversationRepository = conversationRepository;
        _profileRepository = profileRepository;
        _secretProtector = secretProtector;
        _agentChatRuntime = agentChatRuntime;
        _logger = logger;
    }

    public event EventHandler<DesktopChannelManagerEvent>? Changed;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var shouldStart = false;
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
            shouldStart = settings.Channels.Feishu.Enabled;
        }
        finally
        {
            _lifecycleLock.Release();
        }

        if (shouldStart)
        {
            try
            {
                await RestartFeishuAsync(settings.Channels.Feishu, cancellationToken);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Failed to initialize Feishu channel runtime.");
                UpdateFeishuRuntimeState(DesktopChannelRuntimeState.Error, exception.Message);
            }
        }

        NotifyChanged();
    }

    public IReadOnlyList<TranscriptChannelItem> BuildTranscriptChannels(IReadOnlyList<ProviderProfile> profiles)
    {
        var settings = _settingsStore.Load().Channels.Feishu;
        var isConfigured = IsFeishuConfigured(settings);
        var profile = settings.ProfileId is Guid profileId
            ? profiles.FirstOrDefault(item => item.Id == profileId)
            : null;
        var statusLabel = _feishuRuntimeState switch
        {
            DesktopChannelRuntimeState.Running => "运行中",
            DesktopChannelRuntimeState.Starting => "启动中",
            DesktopChannelRuntimeState.Error => "异常",
            _ => settings.Enabled ? "已停止" : "未启用"
        };
        var statusDetail = !string.IsNullOrWhiteSpace(_feishuRuntimeDetail)
            ? _feishuRuntimeDetail
            : isConfigured
                ? "收到飞书消息后会自动创建频道会话并交给 agent 处理。"
                : "需要填写 App ID、App Secret 和绑定模型后才能启用。";

        return
        [
            new TranscriptChannelItem(
                FeishuChannelId,
                "飞书",
                "长连接监听飞书消息，并把收到的消息自动交给 agent 处理。",
                settings.Enabled,
                isConfigured,
                _feishuRuntimeState.ToString().ToLowerInvariant(),
                statusLabel,
                statusDetail,
                settings.DisplayName,
                settings.AppId,
                settings.BotDisplayName,
                !string.IsNullOrWhiteSpace(settings.SecretRef),
                settings.ProfileId?.ToString("D"),
                profile?.Name)
        ];
    }

    public async Task SaveChannelAsync(ChannelEditorResult result, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(result.ChannelId, FeishuChannelId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Unsupported channel '{result.ChannelId}'.");
        }

        var settings = _settingsStore.Load();
        var existing = settings.Channels.Feishu;
        var secretRef = existing.SecretRef;
        if (!string.IsNullOrWhiteSpace(result.AppSecret))
        {
            secretRef = await _secretProtector.StoreSecretAsync(result.AppSecret.Trim(), existing.SecretRef);
        }

        var updatedFeishu = existing with
        {
            DisplayName = string.IsNullOrWhiteSpace(result.DisplayName)
                ? FeishuDesktopChannelSettings.Default.DisplayName
                : result.DisplayName.Trim(),
            AppId = result.AppId.Trim(),
            BotDisplayName = result.BotDisplayName.Trim(),
            ProfileId = result.ProfileId,
            SecretRef = secretRef
        };

        _settingsStore.Save(settings with
        {
            Channels = settings.Channels with
            {
                Feishu = updatedFeishu
            }
        });

        if (updatedFeishu.Enabled)
        {
            await RestartFeishuAsync(updatedFeishu, cancellationToken);
        }
        else
        {
            NotifyChanged();
        }
    }

    public async Task SetChannelEnabledAsync(string channelId, bool enabled, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(channelId, FeishuChannelId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Unsupported channel '{channelId}'.");
        }

        var settings = _settingsStore.Load();
        var existing = settings.Channels.Feishu;
        if (enabled)
        {
            ValidateFeishuSettings(existing);
            await RestartFeishuAsync(existing, cancellationToken);
        }
        else
        {
            await StopFeishuAsync(cancellationToken);
        }

        _settingsStore.Save(settings with
        {
            Channels = settings.Channels with
            {
                Feishu = existing with { Enabled = enabled }
            }
        });

        NotifyChanged();
    }

    public async ValueTask DisposeAsync()
    {
        await StopFeishuAsync();
        _lifecycleLock.Dispose();

        foreach (var conversationLock in _conversationLocks.Values)
        {
            conversationLock.Dispose();
        }
    }

    private async Task RestartFeishuAsync(
        FeishuDesktopChannelSettings settings,
        CancellationToken cancellationToken)
    {
        ValidateFeishuSettings(settings);

        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            await StopFeishuCoreAsync(cancellationToken);
            UpdateFeishuRuntimeState(DesktopChannelRuntimeState.Starting, "正在建立飞书长连接...");

            var appSecret = await _secretProtector.RetrieveSecretAsync(settings.SecretRef, cancellationToken);
            if (string.IsNullOrWhiteSpace(appSecret))
            {
                throw new InvalidOperationException("飞书 App Secret 无法读取，请重新保存频道配置。");
            }

            var botService = new FeishuBotService(
                new FeishuChannelOptions
                {
                    AppId = settings.AppId,
                    AppSecret = appSecret,
                    BotDisplayName = string.IsNullOrWhiteSpace(settings.BotDisplayName)
                        ? settings.DisplayName
                        : settings.BotDisplayName,
                    Log = message => _logger.LogInformation("{Message}", message)
                },
                HandleFeishuIncomingMessageAsync,
                running => UpdateFeishuRuntimeState(
                    running ? DesktopChannelRuntimeState.Running : DesktopChannelRuntimeState.Stopped,
                    running ? "飞书长连接已建立。" : "飞书长连接已停止。"));

            try
            {
                await botService.StartAsync(cancellationToken);
                _feishuBotService = botService;
                UpdateFeishuRuntimeState(DesktopChannelRuntimeState.Running, "飞书长连接已建立。");
            }
            catch (Exception exception)
            {
                await botService.DisposeAsync();
                UpdateFeishuRuntimeState(DesktopChannelRuntimeState.Error, exception.Message);
                throw;
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task StopFeishuAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            await StopFeishuCoreAsync(cancellationToken);
            UpdateFeishuRuntimeState(DesktopChannelRuntimeState.Stopped, "飞书长连接已停止。");
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task StopFeishuCoreAsync(CancellationToken cancellationToken)
    {
        var botService = _feishuBotService;
        _feishuBotService = null;
        if (botService is null)
        {
            return;
        }

        try
        {
            await botService.StopAsync(cancellationToken);
        }
        finally
        {
            await botService.DisposeAsync();
        }
    }

    private async Task HandleFeishuIncomingMessageAsync(
        FeishuIncomingMessage incomingMessage,
        CancellationToken cancellationToken)
    {
        var conversationLock = _conversationLocks.GetOrAdd(
            $"{FeishuChannelId}:{incomingMessage.ChatId}",
            _ => new SemaphoreSlim(1, 1));
        await conversationLock.WaitAsync(cancellationToken);

        try
        {
            await ProcessFeishuMessageCoreAsync(incomingMessage, cancellationToken);
        }
        finally
        {
            conversationLock.Release();
        }
    }

    private async Task ProcessFeishuMessageCoreAsync(
        FeishuIncomingMessage incomingMessage,
        CancellationToken cancellationToken)
    {
        ConversationRecord? conversation = null;

        try
        {
            var channelSettings = _settingsStore.Load().Channels.Feishu;
            var profileId = channelSettings.ProfileId
                ?? throw new InvalidOperationException("飞书频道还没有绑定模型配置。");
            var profile = await _profileRepository.GetProfileAsync(profileId, cancellationToken)
                ?? throw new InvalidOperationException("飞书频道绑定的模型配置不存在，请重新选择。");

            var apiKey = await _secretProtector.RetrieveSecretAsync(profile.SecretRef, cancellationToken);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException($"模型配置 '{profile.Name}' 的 API Key 无法读取。");
            }

            conversation = await EnsureFeishuConversationAsync(channelSettings, incomingMessage, profile, cancellationToken);

            var userMessage = new MessageRecord(
                Guid.NewGuid(),
                conversation.Id,
                MessageRole.User,
                BuildFeishuUserMessageMarkdown(incomingMessage),
                MessageStatus.Completed,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow);
            await _conversationRepository.UpsertMessageAsync(userMessage, cancellationToken);
            NotifyChanged(conversation.Id);

            var requestMessages = await _conversationRepository.ListMessagesAsync(conversation.Id, cancellationToken);
            IFeishuStreamingHandle? streamingHandle = null;
            var streamedMarkdown = new StringBuilder();

            await foreach (var update in _agentChatRuntime.StreamTurnAsync(
                               new ChatTurnRequest(
                                   conversation.Id,
                                   profile,
                                   apiKey,
                                   null,
                                   ConversationMode.Channel,
                                   ToolPermissionMode.FullAccess,
                                   TeamDiscussionDefaults.DefaultMaxRounds,
                                   TeamOutputMode.ReplyOnly,
                                   null,
                                   requestMessages,
                                   []),
                               cancellationToken))
            {
                switch (update)
                {
                    case AssistantMessageStartedEvent:
                        streamingHandle ??= await CreateStreamingReplyHandleAsync(incomingMessage, cancellationToken);
                        break;
                    case AssistantDeltaEvent delta:
                        streamedMarkdown.Append(delta.DeltaMarkdown);
                        if (streamingHandle is not null)
                        {
                            await streamingHandle.UpdateAsync(ExtractChannelReply(streamedMarkdown.ToString()), cancellationToken);
                        }
                        break;
                    case AssistantMessageCompletedEvent completed:
                        await _conversationRepository.UpsertMessageAsync(completed.Message, cancellationToken);
                        await TouchConversationAsync(conversation, completed.Message.UpdatedAtUtc, cancellationToken);

                        var replyContent = ExtractChannelReply(completed.Message.MarkdownContent);
                        if (streamingHandle is not null)
                        {
                            await streamingHandle.FinishAsync(replyContent, cancellationToken);
                        }
                        else if (_feishuBotService is not null)
                        {
                            await _feishuBotService.ReplyMessageAsync(
                                incomingMessage.MessageId,
                                replyContent,
                                cancellationToken);
                        }

                        NotifyChanged(conversation.Id);
                        break;
                }
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(exception, "Failed to process Feishu channel message.");
            UpdateFeishuRuntimeState(DesktopChannelRuntimeState.Error, exception.Message);

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

            if (_feishuBotService is not null)
            {
                try
                {
                    await _feishuBotService.ReplyMessageAsync(
                        incomingMessage.MessageId,
                        $"SelfClaw 暂时无法处理这条消息：{exception.Message}",
                        cancellationToken);
                }
                catch (Exception replyException)
                {
                    _logger.LogWarning(replyException, "Failed to send Feishu failure reply.");
                }
            }
        }
    }

    private async Task<ConversationRecord> EnsureFeishuConversationAsync(
        FeishuDesktopChannelSettings settings,
        FeishuIncomingMessage incomingMessage,
        ProviderProfile profile,
        CancellationToken cancellationToken)
    {
        var existingConversation = (await _conversationRepository.ListConversationsAsync(cancellationToken))
            .FirstOrDefault(item =>
                item.Mode == ConversationMode.Channel &&
                string.Equals(item.ChannelKind, FeishuChannelId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.ChannelConversationId, incomingMessage.ChatId, StringComparison.Ordinal));

        var now = DateTimeOffset.UtcNow;
        var conversationTitle = BuildFeishuConversationTitle(settings.DisplayName, incomingMessage.ChatName);
        var conversation = existingConversation is null
            ? new ConversationRecord(
                Guid.NewGuid(),
                conversationTitle,
                profile.Id,
                null,
                ConversationMode.Channel,
                ToolPermissionMode.FullAccess,
                TeamDiscussionDefaults.DefaultMaxRounds,
                TeamOutputMode.ReplyOnly,
                now,
                now,
                ChannelKind: FeishuChannelId,
                ChannelConversationId: incomingMessage.ChatId,
                ChannelDisplayName: settings.DisplayName)
            : existingConversation with
            {
                Title = conversationTitle,
                ProfileId = profile.Id,
                WorkspaceRootId = null,
                Mode = ConversationMode.Channel,
                ToolPermissionMode = ToolPermissionMode.FullAccess,
                TeamMaxRounds = TeamDiscussionDefaults.DefaultMaxRounds,
                TeamOutputMode = TeamOutputMode.ReplyOnly,
                UpdatedAtUtc = now,
                ChannelKind = FeishuChannelId,
                ChannelConversationId = incomingMessage.ChatId,
                ChannelDisplayName = settings.DisplayName
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

    private async Task<IFeishuStreamingHandle?> CreateStreamingReplyHandleAsync(
        FeishuIncomingMessage incomingMessage,
        CancellationToken cancellationToken)
    {
        var botService = _feishuBotService;
        if (botService is null)
        {
            return null;
        }

        try
        {
            return await botService.SendStreamingMessageAsync(
                incomingMessage.ChatId,
                "Thinking...",
                incomingMessage.MessageId,
                cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to create Feishu streaming reply handle. Falling back to final reply only.");
            return null;
        }
    }

    private void UpdateFeishuRuntimeState(
        DesktopChannelRuntimeState state,
        string? detail)
    {
        _feishuRuntimeState = state;
        _feishuRuntimeDetail = detail;
        NotifyChanged();
    }

    private void NotifyChanged(Guid? conversationId = null)
        => Changed?.Invoke(this, new DesktopChannelManagerEvent(conversationId));

    private static bool IsFeishuConfigured(FeishuDesktopChannelSettings settings)
        => !string.IsNullOrWhiteSpace(settings.AppId) &&
           !string.IsNullOrWhiteSpace(settings.SecretRef) &&
           settings.ProfileId is Guid;

    private static void ValidateFeishuSettings(FeishuDesktopChannelSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.AppId))
        {
            throw new InvalidOperationException("请先填写飞书 App ID。");
        }

        if (string.IsNullOrWhiteSpace(settings.SecretRef))
        {
            throw new InvalidOperationException("请先保存飞书 App Secret。");
        }

        if (settings.ProfileId is not Guid)
        {
            throw new InvalidOperationException("请先为飞书频道绑定一个模型配置。");
        }
    }

    private static string BuildFeishuConversationTitle(string displayName, string? chatName)
    {
        var resolvedDisplayName = string.IsNullOrWhiteSpace(displayName)
            ? FeishuDesktopChannelSettings.Default.DisplayName
            : displayName.Trim();
        var resolvedChatName = chatName?.Trim();
        if (string.IsNullOrWhiteSpace(resolvedChatName) ||
            string.Equals(resolvedDisplayName, resolvedChatName, StringComparison.OrdinalIgnoreCase))
        {
            return resolvedDisplayName;
        }

        return $"{resolvedDisplayName} · {resolvedChatName}";
    }

    private static string BuildFeishuUserMessageMarkdown(FeishuIncomingMessage incomingMessage)
    {
        var builder = new StringBuilder();
        builder.AppendLine("> 渠道: 飞书");

        if (!string.IsNullOrWhiteSpace(incomingMessage.ChatName))
        {
            builder.AppendLine($"> 会话: {incomingMessage.ChatName.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(incomingMessage.SenderName))
        {
            builder.AppendLine($"> 发送人: {incomingMessage.SenderName.Trim()}");
        }

        if (incomingMessage.Images?.Count > 0)
        {
            builder.AppendLine($"> 附件: {incomingMessage.Images.Count} 张图片");
        }

        if (incomingMessage.Audio is not null)
        {
            builder.AppendLine("> 附件: 1 段语音");
        }

        builder.AppendLine();

        var content = string.IsNullOrWhiteSpace(incomingMessage.Content)
            ? "[空消息]"
            : incomingMessage.Content.Trim();
        builder.Append(content);
        return builder.ToString();
    }

    private static string ExtractChannelReply(string markdown)
    {
        var content = AssistantMessageSegmenter.Split(markdown).ContentMarkdown.Trim();
        return string.IsNullOrWhiteSpace(content) ? "我已经收到消息了。" : content;
    }
}
