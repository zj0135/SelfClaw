using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;

namespace SelfClaw.Tests.TestDoubles;

internal sealed class StubSubagentDefinitionCatalog(params SubagentCatalogEntry[] entries) : ISubagentDefinitionCatalog
{
    public IReadOnlyList<SubagentCatalogEntry> GetEntries(IReadOnlyList<string> subagentIds)
        => entries
            .Where(entry => subagentIds.Contains(entry.Id, StringComparer.OrdinalIgnoreCase))
            .ToArray();
}
