using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Runtime;
using SelfClaw.Desktop.Services.Settings;

namespace SelfClaw.Desktop.Services.Compression;

/// <summary>
/// Direct 上下文压缩设置的读写。真值在 desktop-settings.json 的 direct_compression 节点。
/// 本服务同时是 Infrastructure 读策略的唯一入口（每回合调用一次 <see cref="GetAsync"/>），
/// 因此模型可用性校验只在加载与保存时做，运行期只读缓存。
/// </summary>
public sealed class DirectCompressionSettingsService : IDirectCompressionPolicySource
{
    private const string SettingsNodeName = "direct_compression";

    // 比例太高会让摘要永远不触发、太低会让每回合都在总结；绝对阈值是给没声明窗口的模型兜底，
    // 低于 4000 时任何提示都会越过它，高于 1 000 000 时那个模型根本装不下。
    private const double MinimumRatio = 0.10d;
    private const double MaximumRatio = 0.95d;
    private const int MinimumAbsoluteThresholdTokens = 4_000;
    private const int MaximumAbsoluteThresholdTokens = 1_000_000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly DesktopSettingsJsonStore _settingsStore;
    private readonly IAiModelCatalog _modelCatalog;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DirectCompressionPolicy? _cached;

    public DirectCompressionSettingsService(
        DesktopSettingsJsonStore settingsStore,
        IAiModelCatalog modelCatalog)
    {
        ArgumentNullException.ThrowIfNull(settingsStore);
        ArgumentNullException.ThrowIfNull(modelCatalog);
        _settingsStore = settingsStore;
        _modelCatalog = modelCatalog;
    }

    public event Action? Changed;

    public async Task<DirectCompressionPolicy> GetAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _cached ??= await LoadAsync(cancellationToken).ConfigureAwait(false);
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DirectCompressionPolicy> SaveAsync(
        DirectCompressionPolicy policy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var normalized = await NormalizeAsync(policy, cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _settingsStore
                .WriteNodeAsync(SettingsNodeName, normalized, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            _cached = normalized;
        }
        finally
        {
            _gate.Release();
        }

        // 在锁外触发：订阅者可能同步回读 GetAsync。
        Changed?.Invoke();
        return normalized;
    }

    private async Task<DirectCompressionPolicy> LoadAsync(CancellationToken cancellationToken)
    {
        DirectCompressionPolicy? stored;
        try
        {
            stored = await _settingsStore
                .ReadNodeAsync<DirectCompressionPolicy>(SettingsNodeName, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            // 设置文件不可读时压缩只能退回默认策略：它在回合路径上，抛异常会让整个回合失败。
            return new DirectCompressionPolicy();
        }

        return await NormalizeAsync(stored, cancellationToken).ConfigureAwait(false);
    }

    private async Task<DirectCompressionPolicy> NormalizeAsync(
        DirectCompressionPolicy? policy,
        CancellationToken cancellationToken)
    {
        var fallback = new DirectCompressionPolicy();
        if (policy is null)
        {
            return fallback;
        }

        var modelProfileId = policy.ModelProfileId;
        if (modelProfileId is Guid candidate
            && !await _modelCatalog.IsModelAvailableAsync(candidate, cancellationToken).ConfigureAwait(false))
        {
            // 模型被删除或停用后跟随当前会话模型，而不是让压缩一直用一个不可见的模型。
            modelProfileId = null;
        }

        return new DirectCompressionPolicy(
            policy.Enabled,
            double.IsFinite(policy.ThresholdRatio)
                ? Math.Clamp(policy.ThresholdRatio, MinimumRatio, MaximumRatio)
                : fallback.ThresholdRatio,
            Math.Clamp(policy.AbsoluteThresholdTokens, MinimumAbsoluteThresholdTokens, MaximumAbsoluteThresholdTokens),
            modelProfileId);
    }
}
