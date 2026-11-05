using SelfClaw.Core.Models;

namespace SelfClaw.Infrastructure.Network;

/// <summary>
/// Resolves the settings into something a transport can use. One rule is shared by the HTTP handler and
/// the child-process environment: a custom endpoint that is not fully filled in behaves like
/// <see cref="NetworkProxyMode.System"/> instead of failing, because the settings page saves while the
/// user is still typing the port, and a hand-edited file can hold anything at all.
/// </summary>
internal static class NetworkProxyEndpoint
{
    internal const int MinimumPort = 1;
    internal const int MaximumPort = 65_535;

    /// <summary>True when the settings describe an explicit, usable HTTP proxy.</summary>
    internal static bool TryGetCustomProxy(NetworkProxySettings settings, out Uri proxyUri)
    {
        proxyUri = null!;
        if (settings.Mode != NetworkProxyMode.Custom ||
            string.IsNullOrWhiteSpace(settings.Host) ||
            settings.Port is < MinimumPort or > MaximumPort)
        {
            return false;
        }

        try
        {
            proxyUri = new UriBuilder(Uri.UriSchemeHttp, settings.Host.Trim(), settings.Port).Uri;
            return true;
        }
        catch (Exception exception) when (exception is UriFormatException or ArgumentException)
        {
            return false;
        }
    }
}
