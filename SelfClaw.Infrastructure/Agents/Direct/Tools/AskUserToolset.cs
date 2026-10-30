using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Agents.Direct.Tools.Models;

namespace SelfClaw.Infrastructure.Agents.Direct.Tools;

/// <summary>
/// The <c>ask_user</c> tool. It is not an approval: the call blocks on the host's question handler whatever
/// the permission mode says, and the user's answer comes back as this call's result.
/// </summary>
internal sealed class AskUserToolset
{
    private static readonly string QuestionLimitDetail =
        $"This turn has already spent all {AskUserRules.MaximumQuestionsPerTurn} questions it is allowed. "
        + "Decide yourself and state the assumption you made in your reply.";

    private const string SkippedDetail =
        "User skipped the questions. Continue with your best judgment and state the assumption you made.";

    private const string ToolDescription =
        "Ask the user when the answer changes what you do next: an ambiguous requirement, a preference only they "
        + "know, or two designs with real trade-offs. Do not use it to ask for tool permission (approvals are a "
        + "separate channel), to ask for facts you can find in the workspace, or to confirm work after it is done. "
        + "Ask 1-4 questions in one call, each with 2-4 short options. The user always gets an extra free-text "
        + "\"Other\" choice, so never add an \"Other\"/\"其它\" option yourself. Set multiSelect true only when "
        + "several options can hold at once (for example which lint rules to enable); mutually exclusive choices "
        + "must stay false. The call blocks until the user answers, so ask everything you need to decide in one "
        + "call instead of asking again. Write the questions in the user's language.";

    public IReadOnlyList<DirectToolBinding> CreateTools(IUserQuestionHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var askedQuestions = 0;

        // The questions array is the tool's single argument rather than a property of a wrapper record: a
        // wrapper would put the whole call one level deep in the generated schema, and the stored
        // arguments_json - which the transcript card parses - must stay {"questions": [...]}.
        async Task<DirectToolResult> AskUserAsync(
            [Description(
                "The questions to ask, in the order the user should answer them: one tab per question, "
                + "submitted as one batch.")]
            IReadOnlyList<AskUserPromptArgument>? questions,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var prompts = BuildPrompts(questions);
            if (AskUserRules.Validate(ToSpecs(prompts)) is { } failure)
            {
                return Invalid(failure);
            }

            // Counted per question, not per call: a call may carry four questions and the user pays per tab.
            if (askedQuestions + prompts.Count > AskUserRules.MaximumQuestionsPerTurn)
            {
                return QuestionLimitReached();
            }

            askedQuestions += prompts.Count;
            var request = new UserQuestionRequest(Guid.NewGuid(), prompts);
            var answer = await handler.RequestAnswerAsync(request, cancellationToken).ConfigureAwait(false);
            return Describe(prompts, answer);
        }

        return
        [
            new(AIFunctionFactory.Create(AskUserAsync, new AIFunctionFactoryOptions
                {
                    Name = AskUserRules.ToolName,
                    Description = ToolDescription,
                    ExcludeResultSchema = true,
                    MarshalResult = (value, _, _) => value is DirectToolResult result
                        ? ValueTask.FromResult<object?>(result)
                        : throw new InvalidOperationException(
                            $"Tool '{AskUserRules.ToolName}' returned an invalid result.")
                }),
                new DirectToolDescriptor(ToolCallKind.Ask, ToolSourceKind.BuiltIn, DisplayName: "Ask user"))
        ];
    }

    private static IReadOnlyList<UserQuestionPrompt> BuildPrompts(IReadOnlyList<AskUserPromptArgument>? questions)
        => (questions ?? []).Select(question => new UserQuestionPrompt(
            Guid.NewGuid(),
            (question.Question ?? string.Empty).Trim(),
            string.IsNullOrWhiteSpace(question.Header) ? null : question.Header.Trim(),
            (question.Options ?? []).Select(option => new UserQuestionOption(
                (option.Label ?? string.Empty).Trim(),
                string.IsNullOrWhiteSpace(option.Description) ? null : option.Description.Trim())).ToArray(),
            question.MultiSelect)).ToArray();

    // The rules live in Core and must not depend on this assembly's wire DTO, so the already-trimmed prompts
    // are reshaped into the rule input rather than re-implementing any rule here.
    private static IReadOnlyList<AskUserPromptSpec> ToSpecs(IReadOnlyList<UserQuestionPrompt> prompts)
        => prompts.Select(prompt => new AskUserPromptSpec(
            prompt.Question,
            prompt.Options.Select(option => new AskUserOptionSpec(option.Label, option.Description)).ToArray(),
            prompt.Header,
            prompt.MultiSelect)).ToArray();

    // A skipped batch has to stay Completed: a Canceled result is replayed to the model as a tool exception,
    // which would read as "the tool broke" instead of "the user decided not to answer".
    private static DirectToolResult Describe(
        IReadOnlyList<UserQuestionPrompt> prompts,
        UserQuestionAnswer answer)
    {
        if (answer.Skipped)
        {
            return new DirectToolResult(
                ToolCallStatus.Completed,
                "Question skipped",
                JsonSerializer.SerializeToElement(new { skipped = true, answers = Array.Empty<object>() }),
                SkippedDetail);
        }

        var lines = new List<string>(prompts.Count + 1) { "User answered:" };
        var content = new List<object>(prompts.Count);
        var answered = 0;
        for (var index = 0; index < prompts.Count; index++)
        {
            var prompt = prompts[index];
            var promptAnswer = answer.Answers.FirstOrDefault(candidate => candidate.PromptId == prompt.PromptId);
            var selected = promptAnswer?.SelectedLabels ?? [];
            var other = string.IsNullOrWhiteSpace(promptAnswer?.OtherText) ? null : promptAnswer.OtherText.Trim();
            if (promptAnswer is { Skipped: false } && (selected.Count > 0 || other is not null))
            {
                answered++;
            }

            lines.Add($"{index + 1}. {prompt.Question} → {DescribeAnswer(selected, other)}");
            content.Add(new { question = prompt.Question, selected, other });
        }

        // Content is what the live model reads (Detail never reaches the provider); Detail is what later turns
        // replay. Both come out of the same loop, so they can never tell different stories.
        return new DirectToolResult(
            ToolCallStatus.Completed,
            $"Answered {answered} of {prompts.Count}",
            JsonSerializer.SerializeToElement(new { skipped = false, answers = content }),
            string.Join(Environment.NewLine, lines));
    }

    private static string DescribeAnswer(IReadOnlyList<string> selected, string? other)
    {
        var parts = new List<string>(selected.Count + 1);
        parts.AddRange(selected);
        if (other is not null)
        {
            parts.Add($"Other: {other}");
        }

        return parts.Count == 0 ? "(not answered)" : string.Join(", ", parts);
    }

    private static DirectToolResult QuestionLimitReached()
        => new(
            ToolCallStatus.Failed,
            "Question limit reached",
            JsonSerializer.SerializeToElement(new { error = QuestionLimitDetail }),
            QuestionLimitDetail);

    // A rejected questionnaire is a model error, not a runtime fault: it returns a result so the model can
    // resend a valid call without spending the turn's consecutive-fault budget.
    private static DirectToolResult Invalid(string reason)
        => new(
            ToolCallStatus.Failed,
            $"Invalid question: {reason}",
            JsonSerializer.SerializeToElement(new { error = reason }),
            $"Invalid question: {reason}");
}
