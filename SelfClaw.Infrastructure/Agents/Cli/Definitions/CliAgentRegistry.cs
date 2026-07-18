using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Agents.Cli.Definitions.Models;

namespace SelfClaw.Infrastructure.Agents.Cli.Definitions;

/// <summary>
/// Maps a <see cref="CliAgentKind"/> to its <see cref="CliAgentDefinition"/> (plan.md 阶段 4, T4.3).
/// Registers Claude Code, Codex and OpenCode (Codex / OpenCode added in 阶段 7, T7.4).
/// </summary>
public sealed class CliAgentRegistry
{
    private readonly IReadOnlyDictionary<CliAgentKind, CliAgentDefinition> _definitions;

    public CliAgentRegistry()
        : this(new[]
        {
            ClaudeAgentDefinition.Create(),
            CodexAgentDefinition.Create(),
            OpenCodeAgentDefinition.Create(),
        })
    {
    }

    /// <summary>Test seam: build a registry from an explicit set of definitions.</summary>
    public CliAgentRegistry(IEnumerable<CliAgentDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        _definitions = definitions.ToDictionary(d => d.Kind);
    }

    /// <summary>Returns the definition for <paramref name="kind"/>, or <c>null</c> if unregistered.</summary>
    public CliAgentDefinition? Find(CliAgentKind kind) =>
        _definitions.TryGetValue(kind, out var definition) ? definition : null;
}
