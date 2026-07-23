namespace SelfClaw.Infrastructure.Agents.Cli.Adapters.Models;

internal sealed record CliTurnPreparation(
    string Prompt,
    string? StoredSessionId,
    string? SystemPrompt,
    string? Model,
    string? ReasoningEffort);
