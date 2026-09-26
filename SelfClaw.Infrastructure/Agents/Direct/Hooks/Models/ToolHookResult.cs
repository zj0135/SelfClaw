using System.Text.Json;
using SelfClaw.Core.Runtime.Agent;

namespace SelfClaw.Infrastructure.Agents.Direct.Hooks.Models;

internal sealed record ToolHookResult(
    ToolCallStatus Status,
    string? DeniedBy,
    string? Summary,
    JsonElement? Content,
    string? Error,
    string? EffectiveArgumentsJson,
    TimeSpan Duration)
{
    internal static ToolHookResult Threw(string message) => new(
        ToolCallStatus.Failed,
        DeniedBy: null,
        Summary: null,
        Content: null,
        Error: message,
        EffectiveArgumentsJson: null,
        Duration: TimeSpan.Zero);
}
