namespace SelfClaw.LlmProvider.Connections.Dtos;

/// <summary>如何把一个已解密的凭据放到请求上。</summary>
public enum LlmCredentialKind
{
    /// <summary>Authorization: Bearer。等价的官方 SDK 凭据形态。</summary>
    Bearer = 0,

    /// <summary>自定义请求头，头名见 <see cref="LlmCredential.Name"/>。</summary>
    Header = 1,

    /// <summary>查询参数，参数名见 <see cref="LlmCredential.Name"/>。</summary>
    Query = 2
}
