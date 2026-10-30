using System.Text.Json;

namespace SelfClaw.Core.Runtime;

/// <summary>
/// The single source of the <c>ask_user</c> contract: the tool rejects exactly what the dock cannot render
/// and what the per-turn prompt replay cannot afford, so a questionnaire the UI would choke on (or that
/// would cost every later request) never reaches the user.
/// </summary>
public static class AskUserRules
{
    public const string ToolName = "ask_user";
    public const int MaximumPromptsPerCall = 4;
    public const int MaximumOptionsPerPrompt = 4;
    public const int MinimumOptionsPerPrompt = 2;
    public const int MaximumQuestionsPerTurn = 16;

    public const int MaximumQuestionLength = 600;
    public const int MaximumHeaderLength = 24;
    public const int MaximumOptionLabelLength = 60;
    public const int MaximumOptionDescriptionLength = 200;
    public const int MaximumOtherTextLength = 2000;

    /// <summary>The call's arguments are replayed in every later prompt, so their size is capped by the prompt budget.</summary>
    public const int MaximumArgumentsCharacters = 4 * 1024;

    // The dock always appends its own free-text "other" choice, so a model-written one would render twice
    // and make the label set ambiguous.
    private static readonly string[] ReservedOtherLabels = ["other", "其它", "其他"];

    private static readonly JsonSerializerOptions MeasurementOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string? Validate(IReadOnlyList<AskUserPromptSpec>? prompts)
    {
        if (prompts is null || prompts.Count == 0)
        {
            return "Ask at least one question.";
        }

        if (prompts.Count > MaximumPromptsPerCall)
        {
            return $"One call can ask at most {MaximumPromptsPerCall} questions.";
        }

        foreach (var prompt in prompts)
        {
            if (ValidatePrompt(prompt) is { } failure)
            {
                return failure;
            }
        }

        // Measured as the wire shape {"questions":[…]} rather than as the CLR object, because that is what
        // gets stored and replayed on every later request.
        var argumentsCharacters = JsonSerializer.Serialize(
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["questions"] = prompts },
            MeasurementOptions).Length;
        return argumentsCharacters > MaximumArgumentsCharacters
            ? $"The questions take {argumentsCharacters} characters of arguments; the limit is {MaximumArgumentsCharacters}. "
              + "Ask fewer questions or shorten the options."
            : null;
    }

    private static string? ValidatePrompt(AskUserPromptSpec prompt)
    {
        var question = (prompt.Question ?? string.Empty).Trim();
        if (question.Length == 0)
        {
            return "Every question needs non-empty text.";
        }

        if (question.Length > MaximumQuestionLength)
        {
            return $"A question must be at most {MaximumQuestionLength} characters.";
        }

        if ((prompt.Header ?? string.Empty).Trim().Length > MaximumHeaderLength)
        {
            return $"A question header must be at most {MaximumHeaderLength} characters.";
        }

        var options = prompt.Options ?? [];
        if (options.Count < MinimumOptionsPerPrompt)
        {
            return $"Every question needs at least {MinimumOptionsPerPrompt} options.";
        }

        if (options.Count > MaximumOptionsPerPrompt)
        {
            return $"Every question can offer at most {MaximumOptionsPerPrompt} options.";
        }

        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var option in options)
        {
            if (ValidateOption(option, labels) is { } failure)
            {
                return failure;
            }
        }

        return null;
    }

    private static string? ValidateOption(AskUserOptionSpec option, HashSet<string> labels)
    {
        var label = (option.Label ?? string.Empty).Trim();
        if (label.Length == 0)
        {
            return "Every option needs a non-empty label.";
        }

        if (label.Length > MaximumOptionLabelLength)
        {
            return $"An option label must be at most {MaximumOptionLabelLength} characters.";
        }

        if ((option.Description ?? string.Empty).Trim().Length > MaximumOptionDescriptionLength)
        {
            return $"An option description must be at most {MaximumOptionDescriptionLength} characters.";
        }

        if (ReservedOtherLabels.Contains(label, StringComparer.OrdinalIgnoreCase))
        {
            return $"Do not offer an \"{label}\" option: the user already gets a free-text \"Other\" choice.";
        }

        return labels.Add(label)
            ? null
            : $"Option labels must be unique within a question; '{label}' appears more than once.";
    }
}
