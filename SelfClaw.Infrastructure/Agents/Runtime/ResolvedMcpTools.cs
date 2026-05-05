using Microsoft.Extensions.AI;

namespace SelfClaw.Infrastructure.Agents.Runtime;

internal sealed class ResolvedMcpTools : IAsyncDisposable
{
    public static ResolvedMcpTools Empty { get; } = new(
        [],
        new Dictionary<string, ToolInvocationMetadata>(StringComparer.OrdinalIgnoreCase),
        []);

    private readonly IReadOnlyList<IAsyncDisposable> _ownedResources;

    public ResolvedMcpTools(
        IReadOnlyList<AITool> tools,
        IReadOnlyDictionary<string, ToolInvocationMetadata> metadataByToolName,
        IReadOnlyList<IAsyncDisposable> ownedResources)
    {
        Tools = tools;
        MetadataByToolName = metadataByToolName;
        _ownedResources = ownedResources;
    }

    public IReadOnlyList<AITool> Tools { get; }

    public IReadOnlyDictionary<string, ToolInvocationMetadata> MetadataByToolName { get; }

    public async ValueTask DisposeAsync()
    {
        List<Exception>? exceptions = null;

        for (var index = _ownedResources.Count - 1; index >= 0; index--)
        {
            try
            {
                await _ownedResources[index].DisposeAsync();
            }
            catch (Exception exception)
            {
                exceptions ??= [];
                exceptions.Add(exception);
            }
        }

        if (exceptions is { Count: > 0 })
        {
            throw exceptions.Count == 1
                ? exceptions[0]
                : new AggregateException(exceptions);
        }
    }
}
