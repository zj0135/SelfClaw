using System.ComponentModel;

namespace SelfClaw.Infrastructure.Agents.Direct.Tools.Models;

/// <summary>
/// One option of an <c>ask_user</c> question as the model sends it. The strings stay raw here: the tool maps
/// them through <c>AskUserRules</c> so an invalid value reaches the model as a readable result.
/// </summary>
internal sealed record AskUserOptionArgument(
    [property: Description("Short label of the choice, shown on its button (max 60 characters).")]
    string? Label,
    [property: Description(
        "Optional one-line explanation of the trade-off, shown under the label (max 200 characters).")]
    string? Description = null);
