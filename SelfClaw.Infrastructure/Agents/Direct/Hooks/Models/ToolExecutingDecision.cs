using System.Text.Json;

namespace SelfClaw.Infrastructure.Agents.Direct.Hooks.Models;

internal sealed record ToolExecutingDecision(string? Decision, string? Reason, JsonElement? UpdatedArguments);
