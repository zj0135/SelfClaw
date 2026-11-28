
using SelfClaw.LlmProvider.Connections.Dtos;
using SelfClaw.LlmProvider.Quirks.Dtos;

namespace SelfClaw.LlmProvider.Vendors.Dtos;

/// <summary>
/// 一家厂商：它支持哪些协议、怎么认证、模型从哪列、以及每个协议下的专有参数与请求调整。
/// 由 JSON 描述符提供，加厂商不需要写代码。
/// </summary>
/// <param name="VendorId">唯一标识，连接行引用它。</param>
/// <param name="DisplayName">展示名。</param>
/// <param name="DefaultEndpoint">新建连接时的默认端点。</param>
/// <param name="Protocols">协议 id → 该协议的配置。键即"这家厂商支持哪些接口"。</param>
/// <param name="AuthKind">凭据放置方式。</param>
/// <param name="AuthName">Header/Query 方式的参数名。</param>
/// <param name="ModelListing">模型清单方式；null 表示不支持列出。</param>
/// <param name="Quirks">厂商级请求调整，先于协议级应用。</param>
/// <param name="Subtitle">可选展示副标题。</param>
/// <param name="AccentColor">可选展示色，纯数据，本库不消费。</param>
public sealed record LlmVendorDescriptor(
    string VendorId,
    string DisplayName,
    Uri DefaultEndpoint,
    IReadOnlyDictionary<string, LlmVendorProtocol> Protocols,
    LlmCredentialKind AuthKind = LlmCredentialKind.Bearer,
    string? AuthName = null,
    LlmVendorModelListing? ModelListing = null,
    IReadOnlyList<LlmQuirkRef>? Quirks = null,
    string? Subtitle = null,
    string? AccentColor = null)
{
    public string VendorId { get; } = string.IsNullOrWhiteSpace(VendorId)
        ? throw new ArgumentException("A vendor id is required.", nameof(VendorId))
        : VendorId;

    public IReadOnlyDictionary<string, LlmVendorProtocol> Protocols { get; } = Protocols
        ?? throw new ArgumentNullException(nameof(Protocols));

    public bool Supports(string protocol) => Protocols.ContainsKey(protocol);

    public LlmVendorProtocol? ProtocolOrNull(string protocol) =>
        Protocols.TryGetValue(protocol, out var entry) ? entry : null;
}
