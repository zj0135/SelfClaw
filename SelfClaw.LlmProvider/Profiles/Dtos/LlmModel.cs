
namespace SelfClaw.LlmProvider.Profiles.Dtos;

/// <summary>
/// 一个要用的模型：协议 + 模型 id + 这一回合的参数。宿主负责把所有配置来源解析到这些字段上。
/// </summary>
/// <param name="ModelId">发给 provider 的模型 id。</param>
/// <param name="Protocol">见 <c>LlmProtocols</c>；也可以是调用方自注册的引擎 id。</param>
/// <param name="Sampling">采样参数。</param>
/// <param name="Options">自由线参数袋，键名即参数表里 <c>option:&lt;key&gt;</c> 引用的键。</param>
/// <param name="ReasoningEffort">思考等级；null 表示不发送。</param>
/// <param name="Limits">模型能力上限。</param>
/// <param name="HasTypedConfiguration">
/// 调用方是否已经为这个模型存过类型化配置（如思考等级、采样、上限）。为 true 时，
/// 参数表里标记了 <c>gateLegacySources</c> 的字段不再读取自由选项袋，以免手写线参数盖掉界面设置。
/// </param>
public sealed record LlmModel(
    string ModelId,
    string Protocol,
    LlmSampling Sampling = default,
    IReadOnlyDictionary<string, System.Text.Json.JsonElement>? Options = null,
    string? ReasoningEffort = null,
    LlmModelLimits Limits = default,
    bool HasTypedConfiguration = false)
{
    public string ModelId { get; } = string.IsNullOrWhiteSpace(ModelId)
        ? throw new ArgumentException("A model id is required.", nameof(ModelId))
        : ModelId;

    public string Protocol { get; } = string.IsNullOrWhiteSpace(Protocol)
        ? throw new ArgumentException("A protocol is required.", nameof(Protocol))
        : Protocol;
}
