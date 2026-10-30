namespace SelfClaw.Core.Runtime;

/// <summary>
/// One prompt as <see cref="AskUserRules"/> sees it: the strings stay raw so an invalid value reaches the
/// model as a readable tool result instead of an exception.
/// </summary>
public sealed record AskUserPromptSpec(
    string? Question,
    IReadOnlyList<AskUserOptionSpec>? Options,
    string? Header = null,
    bool MultiSelect = false);
