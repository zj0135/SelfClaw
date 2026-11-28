
namespace SelfClaw.LlmProvider.Errors;

/// <summary>厂商描述符无法解析或不被支持。</summary>
public sealed class LlmVendorCatalogException : LlmProviderException
{
    public LlmVendorCatalogException(string message) : base(message)
    {
    }

    public LlmVendorCatalogException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
