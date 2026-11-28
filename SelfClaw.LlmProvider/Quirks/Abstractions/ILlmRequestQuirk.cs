using SelfClaw.LlmProvider.Quirks.Dtos;

namespace SelfClaw.LlmProvider.Quirks.Abstractions;

/// <summary>
/// 请求形状差异的逃生口：认证放置、路径前缀、静态查询参数、响应流改写。
/// 实现为 handler 包装，因此对所有协议、以及模型清单请求统一生效，也不会泄漏任何 SDK 类型。
/// </summary>
public interface ILlmRequestQuirk
{
    /// <summary>厂商描述符里引用这个 id。</summary>
    string Id { get; }

    /// <summary>把 <paramref name="inner"/> 包一层。</summary>
    HttpMessageHandler Wrap(HttpMessageHandler inner, LlmQuirkContext context);
}
