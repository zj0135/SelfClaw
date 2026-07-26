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

    Task SetMcpServerEnabledAsync(string id, bool enabled, CancellationToken cancellationToken = default);

    Task DeleteMcpServerAsync(string id, CancellationToken cancellationToken = default);
}
