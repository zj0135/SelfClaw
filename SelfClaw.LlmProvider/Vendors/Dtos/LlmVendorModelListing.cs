namespace SelfClaw.LlmProvider.Vendors.Dtos;

/// <summary>厂商的模型清单协议。</summary>
/// <param name="Protocol">清单线格式：<c>openai</c> 或 <c>anthropic</c>。</param>
/// <param name="Path">相对端点的路径，例如 <c>models</c>。</param>
public sealed record LlmVendorModelListing(string Protocol = "openai", string Path = "models")
{
    public const string OpenAi = "openai";
    public const string Anthropic = "anthropic";
}
