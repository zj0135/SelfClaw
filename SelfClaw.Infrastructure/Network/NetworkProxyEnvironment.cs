using System.Diagnostics;
using SelfClaw.Core.Models;

namespace SelfClaw.Infrastructure.Network;

/// <summary>
/// Translates the network exit into the environment variables the child processes SelfClaw launches
/// understand. Node- and Python-based CLIs (Claude Code, Codex, npx-based MCP servers) ignore the Windows
/// proxy configuration entirely, so the setting only reaches them this way. Skill scripts are deliberately
/// not covered: they run with a minimal environment that is a security boundary, not an oversight.
/// </summary>
public static class NetworkProxyEnvironment
{
    private static readonly string[] ProxyVariables = ["HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY"];

    /// <summary>
    /// Applies the exit to a child process. Only these variables are touched: everything else stays exactly
    /// as the child would have inherited it.
    /// </summary>
    public static void Apply(NetworkProxySettings settings, ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(startInfo);

        var entries = Entries(settings);
        foreach (var entry in entries)
        {
            startInfo.Environment[entry.Key] = entry.Value;
        }

        // Custom mode targets a single explicit endpoint with no bypass list; any inherited NO_PROXY
        // (including a wildcard from a previous Direct turn or the parent shell) must be removed so the
        // child does not silently bypass the custom proxy.
        if (settings.Mode == NetworkProxyMode.Custom && !entries.ContainsKey("NO_PROXY"))
        {
            startInfo.Environment.Remove("NO_PROXY");
            startInfo.Environment.Remove("no_proxy");
        }
    }

    /// <summary>
    /// The variables to set for a child process. Direct mode neutralises an inherited proxy instead of
    /// merely omitting one, because SelfClaw itself may have been started from a shell that exports one.
    /// </summary>
    /// <param name="readSystemAddress">
    /// Overrides the Windows registry lookup; only tests pass one, so the assertions stay independent of
    /// the machine they run on.
    /// </param>
    internal static IReadOnlyDictionary<string, string> Entries(
        NetworkProxySettings settings,
        Func<string?>? readSystemAddress = null)
    {
        switch (settings.Mode)
        {
            case NetworkProxyMode.Direct:
                {
                    var direct = CreateVariables(string.Empty);
                    direct["NO_PROXY"] = "*";
                    return direct;
                }
            case NetworkProxyMode.Custom when NetworkProxyEndpoint.TryGetCustomProxy(settings, out var proxyUri):
                return CreateVariables($"http://{proxyUri.Authority}");
            default:
                // System, and a custom endpoint that is not filled in yet: hand the child the address the
                // Windows settings point at, or nothing at all when there is none.
                return (readSystemAddress ?? SystemProxyAddressReader.TryReadAddress)() is { } address
                    ? CreateVariables($"http://{address}")
                    : new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static Dictionary<string, string> CreateVariables(string value)
        => ProxyVariables.ToDictionary(variable => variable, _ => value, StringComparer.Ordinal);
}
