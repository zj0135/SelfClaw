using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;

namespace SelfClaw.Infrastructure.Network;

/// <summary>
/// Default source used until a host supplies its settings-backed one: every request connects directly, the
/// same exit as a fresh install. Public because the desktop host also launches child processes of its own
/// (the CLI probe) and has to fall back the same way.
/// </summary>
public sealed class DefaultNetworkProxySource : INetworkProxySource
{
    private static readonly NetworkProxySettings DefaultSettings = new();

    public static DefaultNetworkProxySource Instance { get; } = new();

    public NetworkProxySettings Current => DefaultSettings;

    public Task<NetworkProxySettings> GetAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(DefaultSettings);
}
