using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Extensions.Plugins.Models;

namespace SelfClaw.Infrastructure.Agents.Direct.Hooks;

internal static class HookMatcher
{
    /// <summary>
    /// <c>*</c> matches any sequence including the empty one. The greedy backtracking scan keeps the
    /// cost linear in practice and avoids the catastrophic backtracking a regex would allow.
    /// </summary>
    internal static bool MatchesWildcard(string pattern, string value, StringComparison comparison)
    {
        var patternIndex = 0;
        var valueIndex = 0;
        var starIndex = -1;
        var starValueIndex = 0;
        while (valueIndex < value.Length)
        {
            if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            {
                starIndex = patternIndex++;
                starValueIndex = valueIndex;
                continue;
            }

            if (patternIndex < pattern.Length &&
                CharactersEqual(pattern[patternIndex], value[valueIndex], comparison))
            {
                patternIndex++;
                valueIndex++;
                continue;
            }

            if (starIndex < 0)
            {
                return false;
            }

            patternIndex = starIndex + 1;
            valueIndex = ++starValueIndex;
        }

        while (patternIndex < pattern.Length && pattern[patternIndex] == '*')
        {
            patternIndex++;
        }

        return patternIndex == pattern.Length;
    }

    /// <summary>
    /// A pattern only ever matches the host form <see cref="Uri.Host"/> produces, so a wildcard rule
    /// requires at least one more label ("*.example.com" does not match "example.com") and an IP
    /// literal is compared exactly.
    /// </summary>
    internal static bool MatchesHost(string pattern, string host)
    {
        var normalizedHost = NormalizeHost(host);
        if (pattern.StartsWith("*.", StringComparison.Ordinal))
        {
            var domain = pattern[2..];
            return normalizedHost.Length > domain.Length &&
                   normalizedHost.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(pattern, normalizedHost, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool MatchesTurn(PluginHookMatcher matcher, DirectTurnOrigin origin)
        => matcher.Origins.Count == 0 || matcher.Origins.Contains(origin);

    internal static bool MatchesTool(
        PluginHookMatcher matcher,
        DirectTurnOrigin origin,
        string toolName,
        string? sourceId,
        ToolCallKind kind,
        ToolSourceKind sourceKind)
    {
        if (!MatchesTurn(matcher, origin))
        {
            return false;
        }

        if (matcher.ToolPatterns.Count > 0 &&
            !matcher.ToolPatterns.Any(pattern => MatchesWildcard(pattern, toolName, StringComparison.Ordinal)))
        {
            return false;
        }

        if (matcher.SourceIdPatterns.Count > 0 &&
            (sourceId is null ||
             !matcher.SourceIdPatterns.Any(pattern =>
                 MatchesWildcard(pattern, sourceId, StringComparison.OrdinalIgnoreCase))))
        {
            return false;
        }

        if (matcher.Kinds.Count > 0 && !matcher.Kinds.Contains(kind))
        {
            return false;
        }

        return matcher.Sources.Count == 0 || matcher.Sources.Contains(sourceKind);
    }

    internal static bool MatchesHttp(PluginHookMatcher matcher, DirectTurnOrigin origin, string host)
        => MatchesTurn(matcher, origin) &&
           (matcher.HostPatterns.Count == 0 || matcher.HostPatterns.Any(pattern => MatchesHost(pattern, host)));

    private static string NormalizeHost(string host)
        => host.Length > 1 && host[0] == '[' && host[^1] == ']' ? host[1..^1] : host;

    private static bool CharactersEqual(char left, char right, StringComparison comparison)
        => comparison == StringComparison.Ordinal
            ? left == right
            : char.ToUpperInvariant(left) == char.ToUpperInvariant(right);
}
