
using SelfClaw.LlmProvider.Parameters.Dtos;
using SelfClaw.LlmProvider.Quirks.Dtos;

namespace SelfClaw.LlmProvider.Vendors.Dtos;

/// <summary>某个协议在这家厂商上的配置。</summary>
/// <param name="Parameters">该协议下的专有参数表。</param>
/// <param name="Quirks">该协议额外的请求形状调整。</param>
public sealed record LlmVendorProtocol(
    IReadOnlyList<LlmVendorParameter>? Parameters = null,
    IReadOnlyList<LlmQuirkRef>? Quirks = null);
