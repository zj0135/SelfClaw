using FluentAssertions;
using SelfClaw.Core.Runtime;

namespace SelfClaw.Tests.Core.Questions;

public sealed class AskUserRulesTests
{
    [Fact]
    public void A_null_or_empty_batch_is_rejected()
    {
        AskUserRules.Validate(null).Should().Be("Ask at least one question.");
        AskUserRules.Validate([]).Should().Be("Ask at least one question.");
    }

    [Fact]
    public void More_than_four_questions_are_rejected()
    {
        var prompts = Enumerable.Range(0, AskUserRules.MaximumPromptsPerCall + 1)
            .Select(index => Prompt($"Question {index}?"))
            .ToArray();

        AskUserRules.Validate(prompts).Should().Be("One call can ask at most 4 questions.");
    }

    [Fact]
    public void A_question_with_a_single_option_is_rejected()
    {
        var prompt = Prompt("Which database?") with { Options = [Option("PostgreSQL")] };

        AskUserRules.Validate([prompt]).Should().Be("Every question needs at least 2 options.");
    }

    [Fact]
    public void A_question_with_more_than_four_options_is_rejected()
    {
        var prompt = Prompt("Which database?") with
        {
            Options = [Option("One"), Option("Two"), Option("Three"), Option("Four"), Option("Five")]
        };

        AskUserRules.Validate([prompt]).Should().Be("Every question can offer at most 4 options.");
    }

    [Theory]
    [InlineData("PostgreSQL")]
    [InlineData("postgresql")]
    public void Duplicate_option_labels_inside_one_question_are_rejected(string duplicate)
    {
        var prompt = Prompt("Which database?") with { Options = [Option("PostgreSQL"), Option(duplicate)] };

        AskUserRules.Validate([prompt]).Should().Contain("unique").And.Contain(duplicate);
    }

    [Fact]
    public void The_same_label_on_different_questions_stays_valid()
    {
        AskUserRules.Validate([Prompt("First?"), Prompt("Second?")]).Should().BeNull();
    }

    [Fact]
    public void A_question_without_text_is_rejected()
    {
        AskUserRules.Validate([Prompt("   ")]).Should().Be("Every question needs non-empty text.");
    }

    [Fact]
    public void Over_long_fields_are_rejected()
    {
        AskUserRules.Validate([Prompt(new string('q', AskUserRules.MaximumQuestionLength + 1))])
            .Should().Be($"A question must be at most {AskUserRules.MaximumQuestionLength} characters.");
        AskUserRules.Validate([Prompt("Which database?") with { Header = new string('h', AskUserRules.MaximumHeaderLength + 1) }])
            .Should().Be($"A question header must be at most {AskUserRules.MaximumHeaderLength} characters.");
        AskUserRules.Validate([Prompt("Which database?") with
            {
                Options = [Option(new string('l', AskUserRules.MaximumOptionLabelLength + 1)), Option("SQLite")]
            }])
            .Should().Be($"An option label must be at most {AskUserRules.MaximumOptionLabelLength} characters.");
        AskUserRules.Validate([Prompt("Which database?") with
            {
                Options =
                [
                    new AskUserOptionSpec("PostgreSQL", new string('d', AskUserRules.MaximumOptionDescriptionLength + 1)),
                    Option("SQLite")
                ]
            }])
            .Should().Be($"An option description must be at most {AskUserRules.MaximumOptionDescriptionLength} characters.");
    }

    [Theory]
    [InlineData("Other")]
    [InlineData("other")]
    [InlineData("其它")]
    public void A_model_written_other_option_is_rejected(string label)
    {
        var prompt = Prompt("Which database?") with { Options = [Option("PostgreSQL"), Option(label)] };

        AskUserRules.Validate([prompt]).Should().Contain("Other").And.Contain(label);
    }

    [Fact]
    public void Arguments_over_the_prompt_budget_are_rejected()
    {
        // Every field is inside its own limit; only the batch as a whole exceeds the replayed argument budget.
        var prompts = Enumerable.Range(0, AskUserRules.MaximumPromptsPerCall)
            .Select(index => new AskUserPromptSpec(
                new string('q', AskUserRules.MaximumQuestionLength),
                Enumerable.Range(0, AskUserRules.MaximumOptionsPerPrompt)
                    .Select(option => new AskUserOptionSpec(
                        $"Option {index}-{option}",
                        new string('d', AskUserRules.MaximumOptionDescriptionLength)))
                    .ToArray()))
            .ToArray();

        AskUserRules.Validate(prompts).Should().Contain(AskUserRules.MaximumArgumentsCharacters.ToString());
    }

    [Fact]
    public void A_valid_questionnaire_passes()
    {
        var prompts = Enumerable.Range(0, AskUserRules.MaximumPromptsPerCall)
            .Select(index => Prompt($"Question {index}?"))
            .ToArray();

        AskUserRules.Validate(prompts).Should().BeNull();
    }

    private static AskUserPromptSpec Prompt(string question)
        => new(question,
            [Option("PostgreSQL"), Option("SQLite")],
            Header: "Database",
            MultiSelect: false);

    private static AskUserOptionSpec Option(string label) => new(label, Description: null);
}
