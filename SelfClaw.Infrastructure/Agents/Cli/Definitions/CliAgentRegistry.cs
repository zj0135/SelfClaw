using SelfClaw.Core.Runtime.Agent;

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

    /// <summary>Returns the definition for <paramref name="kind"/>, throwing if it is not registered.</summary>
    /// <exception cref="NotSupportedException">No definition is registered for <paramref name="kind"/>.</exception>
    public CliAgentDefinition Get(CliAgentKind kind) =>
        Find(kind) ?? throw new NotSupportedException(
            $"No CLI agent definition is registered for '{kind}'.");

    /// <summary>True when a definition is registered for <paramref name="kind"/>.</summary>
    public bool Supports(CliAgentKind kind) => _definitions.ContainsKey(kind);
}
