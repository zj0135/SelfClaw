using SelfClaw.Core.Runtime.Agent;

namespace SelfClaw.Desktop.Services.ProgrammingAssistant.Models;

/// <summary>
/// The resolved per-turn CLI selection handed to the runtime: the target agent plus the model and reasoning
/// effort to pass on its command line. <see cref="Model"/> / <see cref="ReasoningEffort"/> are <c>null</c>
/// when the turn should defer to the CLI's own configured default.
/// </summary>
public sealed record CliInvocationSelection(CliAgentKind Kind, string? Model, string? ReasoningEffort);
