using SelfClaw.Core.Models;

namespace SelfClaw.Core.Interfaces;

/// <summary>
/// Supplies the network exit for every request SelfClaw makes on its own behalf: provider calls, model
/// listing, MCP transports and the child processes SelfClaw launches. Infrastructure registers a default
/// that always answers <see cref="NetworkProxyMode.System"/>; a host that owns the settings file replaces
/// it with a settings-backed implementation.
/// </summary>
public interface INetworkProxySource
{
    /// <summary>
    /// The settings loaded so far, for callers that cannot await (HTTP handler creation, process launch).
    /// Never blocks and never throws; before the first <see cref="GetAsync"/> it answers the default,
    /// which is the pre-existing behaviour rather than a wrong exit.
    /// </summary>
    NetworkProxySettings Current { get; }

    Task<NetworkProxySettings> GetAsync(CancellationToken cancellationToken = default);
}
