namespace SelfClaw.LlmProvider.Errors;

/// <summary>本库抛出的可读错误。调用方可以直接把它展示给用户。</summary>
public class LlmProviderException : Exception
{
    public LlmProviderException(string message) : base(message)
    {
    }

    public LlmProviderException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
