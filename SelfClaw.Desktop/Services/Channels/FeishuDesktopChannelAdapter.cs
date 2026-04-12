using System.Text;
using Microsoft.Extensions.Logging;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Channels.Feishu;

namespace SelfClaw.Desktop.Services;

public sealed class FeishuDesktopChannelAdapter : IDesktopChannelAdapter
{
    private const string AppIdField = "appId";
    private const string AppSecretField = "appSecret";
    private const string BotDisplayNameField = "botDisplayName";

    public DesktopChannelDescriptor Descriptor { get; } = new(
        "feishu",
        "飞书",
        "长连接监听飞书消息，并把收到的消息自动交给 agent 处理。",
        "我的飞书",
        [
            new DesktopChannelFieldDefinition(
                AppIdField,
                "飞书 App ID",
                DesktopChannelFieldKind.Text,
                Required: true,
                Placeholder: "cli_xxx"),
            new DesktopChannelFieldDefinition(
                AppSecretField,
                "App Secret",
                DesktopChannelFieldKind.Secret,
                Required: true,
                Placeholder: "首次配置时必填"),
            new DesktopChannelFieldDefinition(
                BotDisplayNameField,
                "机器人显示名",
                DesktopChannelFieldKind.Text,
                Description: "用于群聊 @ 提及时识别，可留空。",
                Placeholder: "可留空")
        ]);

    public DesktopChannelConfiguration NormalizeConfiguration(DesktopChannelConfiguration? configuration)
    {
        var candidate = configuration ?? DesktopChannelConfiguration.Default;
        return candidate with
        {
            DisplayName = string.IsNullOrWhiteSpace(candidate.DisplayName)
                ? Descriptor.DefaultDisplayName
                : candidate.DisplayName.Trim(),
            Values = NormalizeDictionary(candidate.Values),
            SecretRefs = NormalizeDictionary(candidate.SecretRefs)
        };
    }

    public bool IsConfigured(DesktopChannelConfiguration configuration)
    {
        var normalized = NormalizeConfiguration(configuration);
        return !string.IsNullOrWhiteSpace(GetValue(normalized, AppIdField)) &&
               !string.IsNullOrWhiteSpace(GetSecretRef(normalized, AppSecretField)) &&
               normalized.ProfileId is Guid;
    }

    public void ValidateConfiguration(DesktopChannelConfiguration configuration)
    {
        var normalized = NormalizeConfiguration(configuration);
        if (string.IsNullOrWhiteSpace(GetValue(normalized, AppIdField)))
        {
            throw new InvalidOperationException("请先填写飞书 App ID。");
        }

        if (string.IsNullOrWhiteSpace(GetSecretRef(normalized, AppSecretField)))
        {
            throw new InvalidOperationException("请先保存飞书 App Secret。");
        }

        if (normalized.ProfileId is not Guid)
        {
            throw new InvalidOperationException("请先为飞书频道绑定一个模型配置。");
        }
    }

    public IReadOnlyList<TranscriptChannelSummaryItem> BuildSummaryItems(
        DesktopChannelConfiguration configuration,
        ProviderProfile? profile)
    {
        var normalized = NormalizeConfiguration(configuration);
        return
        [
            new("频道名称", normalized.DisplayName),
            new("绑定模型", profile?.Name ?? "未绑定"),
            new("App ID", GetValue(normalized, AppIdField) is { Length: > 0 } appId ? appId : "未设置"),
            new("机器人显示名", GetValue(normalized, BotDisplayNameField) is { Length: > 0 } botName ? botName : "未设置")
        ];
    }

    public string BuildConversationTitle(
        DesktopChannelConfiguration configuration,
        DesktopChannelIncomingMessage message)
    {
        var normalized = NormalizeConfiguration(configuration);
        var chatName = message.ConversationName?.Trim();
        if (string.IsNullOrWhiteSpace(chatName) ||
            string.Equals(normalized.DisplayName, chatName, StringComparison.OrdinalIgnoreCase))
        {
            return normalized.DisplayName;
        }

        return $"{normalized.DisplayName} · {chatName}";
    }

    public string BuildUserMessageMarkdown(DesktopChannelIncomingMessage message)
    {
        var builder = new StringBuilder();
        builder.AppendLine("> 渠道: 飞书");

        if (!string.IsNullOrWhiteSpace(message.ConversationName))
        {
            builder.AppendLine($"> 会话: {message.ConversationName.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(message.SenderName))
        {
            builder.AppendLine($"> 发送人: {message.SenderName.Trim()}");
        }

        if (message.Attachments is { Count: > 0 })
        {
            foreach (var attachment in message.Attachments)
            {
                builder.AppendLine($"> 附件: {attachment.Summary}");
            }
        }

        builder.AppendLine();
        builder.Append(string.IsNullOrWhiteSpace(message.Content) ? "[空消息]" : message.Content.Trim());
        return builder.ToString();
    }

    public async Task<IDesktopChannelConnection> CreateConnectionAsync(
        DesktopChannelAdapterContext context,
        DesktopChannelConfiguration configuration,
        Func<DesktopChannelIncomingMessage, CancellationToken, Task> incomingMessageHandler,
        Action<DesktopChannelRuntimeState, string?> runtimeStateChanged,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeConfiguration(configuration);
        var secretRef = GetSecretRef(normalized, AppSecretField);
        var appSecret = await context.SecretProtector.RetrieveSecretAsync(secretRef, cancellationToken);
        if (string.IsNullOrWhiteSpace(appSecret))
        {
            throw new InvalidOperationException("飞书 App Secret 无法读取，请重新保存频道配置。");
        }

        var logger = context.LoggerFactory.CreateLogger<FeishuDesktopChannelAdapter>();
        var service = new FeishuBotService(
            new FeishuChannelOptions
            {
                AppId = GetValue(normalized, AppIdField),
                AppSecret = appSecret,
                BotDisplayName = GetValue(normalized, BotDisplayNameField) is { Length: > 0 } botName
                    ? botName
                    : normalized.DisplayName,
                Log = message => logger.LogInformation("{Message}", message)
            },
            async (incomingMessage, token) => await incomingMessageHandler(ConvertIncomingMessage(incomingMessage), token),
            running => runtimeStateChanged(
                running ? DesktopChannelRuntimeState.Running : DesktopChannelRuntimeState.Stopped,
                running ? "飞书连接已建立。" : "飞书连接已停止。"));

        return new FeishuDesktopChannelConnection(service);
    }

    private static DesktopChannelIncomingMessage ConvertIncomingMessage(FeishuIncomingMessage incomingMessage)
    {
        var attachments = new List<DesktopChannelAttachment>();
        if (incomingMessage.Images is { Count: > 0 })
        {
            attachments.Add(new DesktopChannelAttachment("image", $"{incomingMessage.Images.Count} 张图片"));
        }

        if (incomingMessage.Audio is not null)
        {
            attachments.Add(new DesktopChannelAttachment("audio", "1 段语音"));
        }

        return new DesktopChannelIncomingMessage(
            "feishu",
            incomingMessage.ChatId,
            incomingMessage.MessageId,
            incomingMessage.SenderId,
            incomingMessage.SenderName,
            incomingMessage.Content,
            incomingMessage.ChatName,
            incomingMessage.ChatType,
            attachments);
    }

    private static IReadOnlyDictionary<string, string> NormalizeDictionary(IReadOnlyDictionary<string, string> values)
    {
        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in values)
        {
            var normalizedKey = key?.Trim();
            if (string.IsNullOrWhiteSpace(normalizedKey))
            {
                continue;
            }

            normalized[normalizedKey] = value?.Trim() ?? string.Empty;
        }

        return normalized;
    }

    private static string GetValue(DesktopChannelConfiguration configuration, string key)
        => configuration.Values.TryGetValue(key, out var value)
            ? value
            : string.Empty;

    private static string GetSecretRef(DesktopChannelConfiguration configuration, string key)
        => configuration.SecretRefs.TryGetValue(key, out var value)
            ? value
            : string.Empty;

    private sealed class FeishuDesktopChannelConnection : IDesktopChannelConnection
    {
        private readonly FeishuBotService _service;

        public FeishuDesktopChannelConnection(FeishuBotService service)
        {
            _service = service;
        }

        public Task StartAsync(CancellationToken cancellationToken = default)
            => _service.StartAsync(cancellationToken);

        public Task StopAsync(CancellationToken cancellationToken = default)
            => _service.StopAsync(cancellationToken);

        public Task ReplyAsync(
            DesktopChannelIncomingMessage incomingMessage,
            string content,
            CancellationToken cancellationToken = default)
            => _service.ReplyMessageAsync(incomingMessage.MessageId, content, cancellationToken);

        public async Task<IDesktopChannelStreamingReply?> CreateStreamingReplyAsync(
            DesktopChannelIncomingMessage incomingMessage,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var handle = await _service.SendStreamingMessageAsync(
                    incomingMessage.ConversationId,
                    "Thinking...",
                    incomingMessage.MessageId,
                    cancellationToken);
                return new FeishuDesktopChannelStreamingReply(handle);
            }
            catch
            {
                return null;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _service.DisposeAsync();
        }
    }

    private sealed class FeishuDesktopChannelStreamingReply : IDesktopChannelStreamingReply
    {
        private readonly IFeishuStreamingHandle _handle;

        public FeishuDesktopChannelStreamingReply(IFeishuStreamingHandle handle)
        {
            _handle = handle;
        }

        public Task UpdateAsync(string content, CancellationToken cancellationToken = default)
            => _handle.UpdateAsync(content, cancellationToken);

        public Task FinishAsync(string content, CancellationToken cancellationToken = default)
            => _handle.FinishAsync(content, cancellationToken);
    }
}
