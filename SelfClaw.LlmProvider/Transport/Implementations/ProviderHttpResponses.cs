using System.Net;

namespace SelfClaw.LlmProvider.Transport.Implementations;

/// <summary>非成功状态的可读错误：带上被截断的响应体，让 401/404 的语义不至于丢失。</summary>
internal static class ProviderHttpResponses
{
    private const int MaxBodyLength = 500;

    internal static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string vendorId,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var detail = string.Empty;
        try
        {
            detail = Summarize(await response.Content.ReadAsStringAsync(cancellationToken));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // 只有状态行也仍然是一个有用的错误。
        }

        var message = $"Provider '{vendorId}' returned HTTP {(int)response.StatusCode} ({response.StatusCode})";
        throw new HttpRequestException(
            detail.Length > 0 ? $"{message}: {detail}" : $"{message}.",
            inner: null,
            response.StatusCode);
    }

    private static string Summarize(string body)
    {
        var trimmed = body.Trim();
        if (trimmed.Length > MaxBodyLength)
        {
            trimmed = $"{trimmed[..MaxBodyLength]}…";
        }

        return trimmed.ReplaceLineEndings(" ");
    }
}
