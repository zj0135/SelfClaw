using System.Globalization;
using System.IO;
using System.Security;
using Microsoft.Win32;

namespace SelfClaw.Infrastructure.Network;

/// <summary>
/// Reads the Windows (WinINet, per-user) proxy configuration for the child processes SelfClaw launches.
/// Node- and Python-based CLIs do not consult the Windows proxy at all, so "system" mode has to be handed
/// to them as an environment variable, and that needs a concrete host:port - which is what the registry
/// holds. A machine configured through a PAC script only carries <c>AutoConfigURL</c> and therefore yields
/// nothing here; that is the documented limitation of the mode.
/// </summary>
internal static class SystemProxyAddressReader
{
    private const string InternetSettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    internal static string? TryReadAddress()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey);
            if (key?.GetValue("ProxyEnable") is not int enabled || enabled == 0)
            {
                return null;
            }

            return key.GetValue("ProxyServer") is string server ? Parse(server) : null;
        }
        catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException or IOException)
        {
            // A locked-down registry is no reason to fail a request: the child keeps its own exit instead.
            return null;
        }
    }

    /// <summary>
    /// WinINet stores either a plain <c>host:port</c> or a per-protocol list
    /// (<c>http=host:port;https=host:port</c>). Provider endpoints are all https, so the https entry wins
    /// and the plain form is the fallback.
    /// </summary>
    internal static string? Parse(string? server)
    {
        if (string.IsNullOrWhiteSpace(server))
        {
            return null;
        }

        var text = server.Trim();
        if (!text.Contains('=', StringComparison.Ordinal))
        {
            return IsHostPort(text) ? text : null;
        }

        string? plain = null;
        foreach (var entry in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = entry.IndexOf('=');
            if (separator <= 0 || !IsHostPort(entry[(separator + 1)..].Trim()))
            {
                continue;
            }

            var scheme = entry[..separator].Trim();
            var address = entry[(separator + 1)..].Trim();
            if (scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            {
                return address;
            }

            if (scheme.Equals("http", StringComparison.OrdinalIgnoreCase))
            {
                plain = address;
            }
        }

        return plain;
    }

    private static bool IsHostPort(string address)
    {
        var separator = address.LastIndexOf(':');
        if (separator <= 0 || separator >= address.Length - 1)
        {
            return false;
        }

        var hostPart = address[..separator];
        if (hostPart.Contains(':') && !hostPart.EndsWith(']'))
        {
            return false;
        }

        return int.TryParse(address[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port) &&
            port is >= NetworkProxyEndpoint.MinimumPort and <= NetworkProxyEndpoint.MaximumPort;
    }
}
