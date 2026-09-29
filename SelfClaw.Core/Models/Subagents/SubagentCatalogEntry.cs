namespace SelfClaw.Core.Models;

/// <summary>
/// The advertisement of one Subagent definition: what the model may delegate to and when. It
/// deliberately excludes instructions, model selection and bindings, which stay with the definition
/// snapshot the runtime captures per task.
/// </summary>
public sealed record SubagentCatalogEntry(
    string Id,
    string Name,
    string Description,
    string? ToolPolicy);
