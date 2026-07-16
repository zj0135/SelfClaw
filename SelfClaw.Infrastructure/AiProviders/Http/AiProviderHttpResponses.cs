using System.Net.Http;

namespace SelfClaw.Infrastructure.AiProviders.Http;

/// <summary>
/// Shared non-success handling for provider REST calls. Unlike
/// <see cref="HttpResponseMessage.EnsureSuccessStatusCode"/>, this includes a
/// trimmed response-body snippet so provider errors (401/404 payloads) stay readable.
/// </summary>
internal static class AiProviderHttpResponses
{
    private const int MaxBodyLength = 500;

    internal static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string connectionName,
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
            // The status line alone is still a useful error.
        }

        var message = $"Provider '{connectionName}' returned HTTP {(int)response.StatusCode} ({response.StatusCode})";
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
