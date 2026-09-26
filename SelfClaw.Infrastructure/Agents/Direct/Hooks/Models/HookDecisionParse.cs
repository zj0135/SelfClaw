namespace SelfClaw.Infrastructure.Agents.Direct.Hooks.Models;

/// <summary>
/// Either a parsed decision or the failure kind (<c>invalidOutput</c> / <c>invalidDecision</c>)
/// that <see cref="HookProtocol.Parse{TDecision}"/> reported for the hook's stdout.
/// </summary>
internal sealed record HookDecisionParse<TDecision>(
    TDecision? Decision,
    string? FailureKind,
    string? FailureDetail)
    where TDecision : class;
