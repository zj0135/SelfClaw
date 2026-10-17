using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using SelfClaw.Core.Runtime;
using SelfClaw.Desktop.Services.WebView;

namespace SelfClaw.Desktop.Services.Compression;

/// <summary>
/// 设置页与宿主之间的压缩策略协议。保存一律交给服务归一化，所以非法比例/阈值不会报错 ——
/// 落盘与回显都是归一化后的结果，前端以后端回显为准。
/// </summary>
internal sealed class DirectCompressionSettingsBridge : IDisposable
{
    private readonly DirectCompressionSettingsService _settingsService;
    private readonly WebViewHostChannel _channel;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<DirectCompressionSettingsBridge> _logger;
    private bool _disposed;

    public DirectCompressionSettingsBridge(
        DirectCompressionSettingsService settingsService,
        WebViewHostChannel channel,
        Dispatcher dispatcher,
        ILogger<DirectCompressionSettingsBridge> logger)
    {
        ArgumentNullException.ThrowIfNull(settingsService);
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(logger);
        _settingsService = settingsService;
        _channel = channel;
        _dispatcher = dispatcher;
        _logger = logger;
        _settingsService.Changed += OnChanged;
    }

    public void Dispose()
    {
        _disposed = true;
        _settingsService.Changed -= OnChanged;
    }

    /// <summary>
    /// 与 <see cref="Appearance.AppearanceSettingsBridge"/> 同形：返回 null 表示这条消息不归本 bridge，
    /// 由路由抛 <see cref="ArgumentException"/>；<c>Command</c> 留给将来需要宿主副作用的消息。
    /// </summary>
    public async Task<(object Response, WebViewHostCommand? Command)?> TryHandleAsync(
        string type,
        JsonElement payload,
        CancellationToken cancellationToken = default)
    {
        if (type is not ("direct-compression/get" or "direct-compression/save"))
        {
            return null;
        }

        var requestId = ReadOptionalString(payload, "requestId");
        try
        {
            var policy = type == "direct-compression/save"
                ? await _settingsService.SaveAsync(ReadPolicy(payload), cancellationToken)
                : await _settingsService.GetAsync(cancellationToken);
            return (BuildResponse(requestId, policy), null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return (
                new
                {
                    type = "direct-compression/state",
                    requestId,
                    policy = ToPayload(new DirectCompressionPolicy()),
                    error = exception.Message
                },
                null);
        }
    }

    private void OnChanged() => _ = PublishAsync();

    // 变更可能来自任何调用方，所以推送时重读一次服务，而不是复用某次保存的返回值。
    private async Task PublishAsync()
    {
        if (_disposed || _dispatcher.HasShutdownStarted)
        {
            return;
        }

        try
        {
            var policy = await _settingsService.GetAsync().ConfigureAwait(false);
            if (_dispatcher.CheckAccess())
            {
                Push(policy);
                return;
            }

            await _dispatcher.InvokeAsync(() => Push(policy));
        }
        catch (OperationCanceledException)
        {
            // 这里不持有取消令牌；真正的取消只发生在关闭流程，此时没有消费者。
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to publish direct compression state.");
        }
    }

    private void Push(DirectCompressionPolicy policy)
    {
        // shell 未 ready 时 PostPush 是 no-op（返回 false），下一次 get 会补上状态。
        if (!_disposed) _channel.PostPush(BuildResponse(null, policy));
    }

    private static object BuildResponse(string? requestId, DirectCompressionPolicy policy)
        => new
        {
            type = "direct-compression/state",
            requestId,
            policy = ToPayload(policy)
        };

    private static object ToPayload(DirectCompressionPolicy policy)
        => new
        {
            enabled = policy.Enabled,
            thresholdRatio = policy.ThresholdRatio,
            absoluteThresholdTokens = policy.AbsoluteThresholdTokens,
            modelProfileId = policy.ModelProfileId
        };

    private static DirectCompressionPolicy ReadPolicy(JsonElement payload)
    {
        var node = payload.TryGetProperty("policy", out var policy) && policy.ValueKind == JsonValueKind.Object
            ? policy
            : payload;
        var fallback = new DirectCompressionPolicy();

        return new DirectCompressionPolicy(
            Enabled: ReadBoolean(node, "enabled") ?? fallback.Enabled,
            ThresholdRatio: ReadDouble(node, "thresholdRatio") ?? fallback.ThresholdRatio,
            AbsoluteThresholdTokens: ReadInt32(node, "absoluteThresholdTokens") ?? fallback.AbsoluteThresholdTokens,
            ModelProfileId: Guid.TryParse(ReadOptionalString(node, "modelProfileId"), out var modelProfileId)
                ? modelProfileId
                : null);
    }

    private static bool? ReadBoolean(JsonElement payload, string propertyName)
        => payload.TryGetProperty(propertyName, out var element) && element.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? element.GetBoolean()
            : null;

    private static double? ReadDouble(JsonElement payload, string propertyName)
        => payload.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.Number
            ? element.GetDouble()
            : null;

    private static int? ReadInt32(JsonElement payload, string propertyName)
        => payload.TryGetProperty(propertyName, out var element)
           && element.ValueKind == JsonValueKind.Number
           && element.TryGetInt32(out var value)
            ? value
            : null;

    private static string? ReadOptionalString(JsonElement payload, string propertyName)
    {
        if (!payload.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = element.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
