using System.Net.Http.Headers;

namespace SelfClaw.Infrastructure.Agents.Direct.Hooks;

/// <summary>
/// A request/response payload never carries a header value that could be a credential. Only the
/// listed operational headers and rate-limit families keep their value; every other name keeps its
/// name so the hook can still observe that it was present.
/// </summary>
internal static class HttpHeaderRedactor
{
    private const string Redacted = "[redacted]";

    private static readonly HashSet<string> ValueSafeHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "content-type",
        "content-length",
        "accept",
        "user-agent",
        "anthropic-version",
        "anthropic-beta",
        "openai-beta",
        "x-request-id",
        "request-id",
        "retry-after"
    };

    private static readonly string[] ValueSafePrefixes = ["x-ratelimit-", "anthropic-ratelimit-"];

    internal static IReadOnlyDictionary<string, string> Redact(HttpHeaders headers, HttpHeaders? contentHeaders)
    {
        ArgumentNullException.ThrowIfNull(headers);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Add(result, headers);
        Add(result, contentHeaders);
        return result;
    }

    private static void Add(Dictionary<string, string> result, HttpHeaders? headers)
    {
        if (headers is null)
        {
            return;
        }

        foreach (var header in headers)
        {
            var name = header.Key.ToLowerInvariant();
            result[name] = IsValueSafe(name)
                ? string.Join(", ", header.Value)
                : Redacted;
        }
    }

    private static bool IsValueSafe(string name)
        => ValueSafeHeaders.Contains(name) ||
           ValueSafePrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
}
