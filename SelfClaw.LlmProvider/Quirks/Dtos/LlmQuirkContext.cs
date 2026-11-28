using SelfClaw.LlmProvider.Connections.Dtos;

namespace SelfClaw.LlmProvider.Quirks.Dtos;

/// <summary>一次 quirk 调用需要的一切。</summary>
/// <param name="Connection">当前连接，quirk 可从它取凭据与选项。</param>
/// <param name="Parameters">
/// 厂商描述符在该 quirk 条目上声明的参数（如 <c>api-version</c>、<c>prefix</c>）。
/// </param>
public sealed record LlmQuirkContext(
    LlmConnection Connection,
    IReadOnlyDictionary<string, string> Parameters);
