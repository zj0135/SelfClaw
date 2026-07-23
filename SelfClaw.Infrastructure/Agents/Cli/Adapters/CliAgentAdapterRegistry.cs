using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Agents.Cli.Adapters.Abstractions;

namespace SelfClaw.Infrastructure.Agents.Cli.Adapters;

internal sealed class CliAgentAdapterRegistry
{
    private readonly IReadOnlyDictionary<CliAgentKind, ICliAgentAdapter> _adapters;

    public CliAgentAdapterRegistry(IEnumerable<ICliAgentAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        _adapters = adapters.ToDictionary(adapter => adapter.Kind);
    }

    public ICliAgentAdapter? Find(CliAgentKind kind) =>
        _adapters.TryGetValue(kind, out var adapter) ? adapter : null;
}
