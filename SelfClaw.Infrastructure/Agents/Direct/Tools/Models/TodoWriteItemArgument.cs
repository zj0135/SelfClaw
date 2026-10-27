using System.ComponentModel;

namespace SelfClaw.Infrastructure.Agents.Direct.Tools.Models;

/// <summary>
/// One item of a <c>todo_write</c> call as the model sends it. The strings stay raw here: the tool maps
/// them through <c>TodoPlanRules</c> so an invalid value reaches the model as a readable result.
/// </summary>
internal sealed record TodoWriteItemArgument(
    [property: Description("Short, specific, action-oriented description of the step (max 200 characters).")]
    string? Content,
    [property: Description("One of pending, in_progress or completed (case-insensitive).")]
    string? Status,
    [property: Description(
        "Optional present-continuous wording shown while this step is active, e.g. \"Checking the tool wiring\" " +
        "(max 100 characters). Defaults to the content.")]
    string? ActiveForm = null);
