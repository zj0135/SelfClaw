using System.ComponentModel;

namespace SelfClaw.Infrastructure.Agents.Direct.Tools.Models;

/// <summary>
/// One question of an <c>ask_user</c> call as the model sends it. The dock renders one tab per entry, so
/// every string below is what the user reads verbatim.
/// </summary>
internal sealed record AskUserPromptArgument(
    [property: Description("The question to ask, in the user's language (max 600 characters).")]
    string? Question,
    [property: Description(
        "2-4 choices, each a short label plus an optional trade-off line. The user always gets an extra "
        + "free-text \"Other\" choice, so never add \"Other\"/\"其它\" here.")]
    IReadOnlyList<AskUserOptionArgument>? Options,
    [property: Description(
        "Optional 1-3 word tab label, e.g. \"Database\" (max 24 characters). Defaults to the question number.")]
    string? Header = null,
    [property: Description(
        "Set true only when several options can hold at the same time, e.g. \"which lint rules to enable\". "
        + "Mutually exclusive choices must stay false.")]
    bool MultiSelect = false);
