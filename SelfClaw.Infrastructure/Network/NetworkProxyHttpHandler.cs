using System.Net;
using SelfClaw.Core.Models;

namespace SelfClaw.Infrastructure.Network;

/// <summary>
/// Builds the primary HTTP handler for a network exit. Every request SelfClaw makes in-process goes
/// through a handler built here, so a mode that is not applied here is a request that silently ignores
/// the setting.
/// </summary>
internal static class NetworkProxyHttpHandler
{
    internal static HttpMessageHandler Create(NetworkProxySettings settings)
    {
        var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        if (settings.Mode == NetworkProxyMode.Direct)
        {
            // .NET follows the system proxy (and an inherited HTTP_PROXY) by default, so "direct" has to be
            // stated explicitly or the mode would be a no-op.
            handler.UseProxy = false;
        }
        else if (NetworkProxyEndpoint.TryGetCustomProxy(settings, out var proxyUri))
        {
            handler.UseProxy = true;
            handler.Proxy = new WebProxy(proxyUri);
        }
        else
        {
            // System, and an incomplete custom endpoint: leaving Proxy null keeps .NET resolving the exit
            // the way it always has (environment variables first, then the Windows settings).
            handler.UseProxy = true;
        }

        return handler;
    }
}
