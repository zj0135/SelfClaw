using System.Text.Json;
using SelfClaw.LlmProvider.Client.Dtos;
using SelfClaw.LlmProvider.Connections.Dtos;
using SelfClaw.LlmProvider.Profiles.Dtos;
using SelfClaw.LlmProvider.Vendors.Dtos;

namespace SelfClaw.LlmProvider.Protocols.Dtos;

/// <summary>造引擎对象需要的一切，聚合到一处。</summary>
/// <param name="Connection">已解析的连接。</param>
/// <param name="Model">模型与参数。</param>
/// <param name="Vendor">厂商描述符。</param>
/// <param name="Options">运行时选项。</param>
/// <param name="Parameters">该厂商在该协议下声明的专有参数，已按优先级解析出值。</param>
public sealed record LlmProtocolContext(
    LlmConnection Connection,
    LlmModel Model,
    LlmVendorDescriptor Vendor,
    LlmRequestOptions Options,
    IReadOnlyDictionary<string, JsonElement> Parameters);
