namespace SelfClaw.LlmProvider.Connections.Dtos;

/// <summary>
/// 一个已经解密好的凭据。本库从不解密、不存储、不记录它的值。
/// </summary>
/// <param name="Kind">放置方式。</param>
/// <param name="Value">明文凭据。</param>
/// <param name="Name">Header/Query 的参数名；Bearer 时忽略。</param>
public sealed record LlmCredential(LlmCredentialKind Kind, string Value, string? Name = null)
{
    public string Value { get; } = string.IsNullOrWhiteSpace(Value)
        ? throw new ArgumentException("A credential value is required.", nameof(Value))
        : Value;
}
