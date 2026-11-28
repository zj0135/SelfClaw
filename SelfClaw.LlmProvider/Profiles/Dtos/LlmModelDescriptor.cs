namespace SelfClaw.LlmProvider.Profiles.Dtos;

/// <summary>模型清单里的一条。</summary>
/// <param name="ModelId">发给 provider 的模型 id。</param>
/// <param name="DisplayName">展示名，provider 未提供时为 null。</param>
/// <param name="ContextWindowTokens">声明的上下文窗口。</param>
/// <param name="MaxOutputTokens">声明的输出上限。</param>
/// <param name="PriceInPerMTok">输入价，美元/百万 token。</param>
/// <param name="PriceOutPerMTok">输出价。</param>
/// <param name="PriceCacheWritePerMTok">缓存写入价。</param>
/// <param name="PriceCacheReadPerMTok">缓存读取价。</param>
public sealed record LlmModelDescriptor(
    string ModelId,
    string? DisplayName = null,
    long? ContextWindowTokens = null,
    long? MaxOutputTokens = null,
    decimal? PriceInPerMTok = null,
    decimal? PriceOutPerMTok = null,
    decimal? PriceCacheWritePerMTok = null,
    decimal? PriceCacheReadPerMTok = null);
