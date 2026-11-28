using System.Text.Json;


namespace SelfClaw.LlmProvider.Connections.Dtos;

/// <summary>
/// 一个已解析好的连接：厂商 + 端点 + 已解密凭据 + 连接级选项。本库不关心这些值从哪来。
/// </summary>
/// <param name="VendorId">厂商 id，决定用哪份 <see cref="LlmVendorDescriptor"/>。</param>
/// <param name="Endpoint">基地址。</param>
/// <param name="Credential">已解密的凭据。</param>
/// <param name="Options">连接级自由选项，供参数表以 <c>connection:&lt;key&gt;</c> 引用。</param>
/// <param name="Timeout">单次请求超时；null 表示由调用方自行控制（流式推荐）。</param>
public sealed record LlmConnection(
    string VendorId,
    Uri Endpoint,
    LlmCredential Credential,
    IReadOnlyDictionary<string, JsonElement>? Options = null,
    TimeSpan? Timeout = null)
{
    public string VendorId { get; } = string.IsNullOrWhiteSpace(VendorId)
        ? throw new ArgumentException("A vendor id is required.", nameof(VendorId))
        : VendorId;

    public Uri Endpoint { get; } = Endpoint is { IsAbsoluteUri: true }
        ? Endpoint
        : throw new ArgumentException("A provider endpoint must be an absolute URI.", nameof(Endpoint));

    public LlmCredential Credential { get; } = Credential
        ?? throw new ArgumentNullException(nameof(Credential));
}
