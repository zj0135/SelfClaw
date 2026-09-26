namespace SelfClaw.Infrastructure.Agents.Direct.Hooks;

/// <summary>
/// Whitelist for request headers a hook may add. A blocklist cannot keep up with protocol headers
/// (<c>connection</c>, <c>accept-encoding</c>, <c>content-encoding</c>, <c>te</c>, …) whose corruption
/// breaks transport or body decoding, so only W3C trace headers and non-sensitive <c>x-*</c> names
/// are accepted.
/// </summary>
internal static class HookHeaderPolicy
{
    internal const int MaximumNameLength = 64;
    internal const int MaximumValueLength = 1024;

    private static readonly HashSet<string> TraceHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "traceparent",
        "tracestate",
        "baggage"
    };

    private static readonly string[] SensitiveFragments =
    [
        "key", "token", "secret", "auth", "session", "cookie", "signature", "password"
    ];

    internal static bool IsAllowed(string name, out string? rejection)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length is 0 or > MaximumNameLength || !IsToken(name))
        {
            rejection = "name is not a valid RFC 7230 token of at most 64 characters";
            return false;
        }

        if (TraceHeaders.Contains(name))
        {
            rejection = null;
            return true;
        }

        if (name.StartsWith("x-", StringComparison.OrdinalIgnoreCase) &&
            !SensitiveFragments.Any(fragment => name.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
        {
            rejection = null;
            return true;
        }

        rejection = "name is not in the allowed hook header set";
        return false;
    }

    internal static bool IsValidValue(string value, out string? rejection)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > MaximumValueLength)
        {
            rejection = "value exceeds 1 KiB";
            return false;
        }

        foreach (var character in value)
        {
            if (character is < ' ' or > '~')
            {
                rejection = "value must be printable ASCII without CR or LF";
                return false;
            }
        }

        rejection = null;
        return true;
    }

    private static bool IsToken(string name)
        => name.All(character => char.IsAsciiLetterOrDigit(character) || character is '!' or '#' or '$' or '%'
            or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~');
}
