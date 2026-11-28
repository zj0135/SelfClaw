using System.Text.Json;

namespace SelfClaw.LlmProvider.Parameters.Dtos;

/// <summary>
/// 一条厂商专有参数：把按来源链解析出的值写到请求体的某个 JSON 路径。
/// </summary>
/// <param name="Path">JSON 指针风格路径，例如 <c>$.thinking.type</c>。</param>
/// <param name="Type">写出去的 JSON 类型。</param>
/// <param name="Sources">来源链，按序求值，第一个有值的胜出。见 <see cref="LlmOptionResolver"/>。</param>
/// <param name="Map">值→写出口径；键 <c>*</c> 表示"任意非空值"。</param>
/// <param name="Value">常量值；非 null 时忽略 <see cref="Sources"/>。</param>
/// <param name="GateLegacySources">
/// 为 true 时，自由选项袋与非类型化来源只在模型没有类型化配置时才参与求值。
/// 这复现了"界面上设过思考等级后，旧的手写线参数不许盖掉它"的行为。
/// </param>
public sealed record LlmVendorParameter(
    string Path,
    LlmVendorValueType Type,
    IReadOnlyList<string>? Sources = null,
    IReadOnlyDictionary<string, string>? Map = null,
    JsonElement? Value = null,
    bool GateLegacySources = false)
{
    public string Path { get; } = string.IsNullOrWhiteSpace(Path)
        ? throw new ArgumentException("A parameter path is required.", nameof(Path))
        : Path;
}
