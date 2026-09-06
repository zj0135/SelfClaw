using SelfClaw.Core.Models;

namespace SelfClaw.Core.Interfaces;

public interface IMcpServerRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<McpServerConfigRecord>> ListMcpServersAsync(CancellationToken cancellationToken = default);

    Task<McpServerConfigRecord?> GetMcpServerAsync(string id, CancellationToken cancellationToken = default);

    Task<McpServerConfigRecord> UpsertMcpServerAsync(
        McpServerConfigRecord server,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates only the health observation fields of one server. The write is conditioned on the row
    /// still carrying <paramref name="expectedConfigRevision"/>, so an observation taken before a
    /// concurrent configuration change cannot overwrite that change. Returns <c>false</c> when the
    /// observation was stale and nothing was written.
    /// </summary>
    Task<bool> UpdateMcpServerHealthAsync(
        string serverId,
        long expectedConfigRevision,
        McpServerHealthStatus status,
        string? error,
        IReadOnlyList<string> discoveredTools,
        DateTimeOffset checkedAtUtc,
        CancellationToken cancellationToken = default);

    Task SetMcpServerEnabledAsync(string id, bool enabled, CancellationToken cancellationToken = default);

    Task DeleteMcpServerAsync(string id, CancellationToken cancellationToken = default);
}
