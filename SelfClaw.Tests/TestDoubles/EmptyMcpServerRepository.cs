using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;

namespace SelfClaw.Tests.TestDoubles;

internal sealed class EmptyMcpServerRepository : IMcpServerRepository
{
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IReadOnlyList<McpServerConfigRecord>> ListMcpServersAsync(
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<McpServerConfigRecord>>([]);

    public Task<McpServerConfigRecord?> GetMcpServerAsync(
        string id,
        CancellationToken cancellationToken = default)
        => Task.FromResult<McpServerConfigRecord?>(null);

    public Task<McpServerConfigRecord> UpsertMcpServerAsync(
        McpServerConfigRecord server,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task SetMcpServerEnabledAsync(
        string id,
        bool enabled,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task DeleteMcpServerAsync(string id, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}
