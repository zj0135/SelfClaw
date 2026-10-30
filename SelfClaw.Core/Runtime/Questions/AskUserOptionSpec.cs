namespace SelfClaw.Core.Runtime;

/// <summary>
/// One option as <see cref="AskUserRules"/> sees it. The rules own this shape instead of the tool's wire
/// argument so the contract stays in Core while the tool keeps its own <c>[Description]</c> DTO.
/// </summary>
public sealed record AskUserOptionSpec(string? Label, string? Description);
