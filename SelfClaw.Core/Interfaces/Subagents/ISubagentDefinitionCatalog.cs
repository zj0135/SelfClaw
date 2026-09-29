using SelfClaw.Core.Models;

namespace SelfClaw.Core.Interfaces;

/// <summary>
/// Reads the Subagent definitions a Direct turn advertises to the model. The allowlist decides what is
/// callable; this catalog only supplies the name, description and tool policy used to describe it, so an
/// id without an entry is still callable and fails through the coordinator's durable envelope.
/// </summary>
public interface ISubagentDefinitionCatalog
{
    IReadOnlyList<SubagentCatalogEntry> GetEntries(IReadOnlyList<string> subagentIds);
}
