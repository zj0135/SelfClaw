using SelfClaw.Infrastructure.Agents.Direct.Tools;
using SelfClaw.Infrastructure.Agents.Direct.Capabilities;
using SelfClaw.Infrastructure.Agents.Direct.Tools.Models;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;

namespace SelfClaw.Tests.Infrastructure.Agents.Direct.Tools;

public sealed class AskUserToolsetTests
{
    [Fact]
    public void CreateTools_exposes_one_unapproved_ask_tool()
    {
        var binding = new AskUserToolset().CreateTools(new ScriptedQuestionHandler(Skip))
            .Should().ContainSingle().Subject;

        binding.Tool.Name.Should().Be(AskUserRules.ToolName);
        binding.Descriptor.Kind.Should().Be(ToolCallKind.Ask);
        binding.Descriptor.SourceKind.Should().Be(ToolSourceKind.BuiltIn);
        binding.Descriptor.DisplayName.Should().Be("Ask user");
        binding.RequiresApproval.Should().BeFalse();
        binding.Tool.Description.Should().Contain("Ask 1-4 questions in one call");
        binding.Tool.Description.Should().Contain("Do not use it to ask for tool permission");
        binding.Tool.Description.Should().Contain("never add an \"Other\"/\"其它\" option");
        binding.Tool.Description.Should().Contain("blocks until the user answers");
    }

    [Fact]
    public void The_generated_schema_keeps_the_questions_array_at_the_top_level()
    {
        // The stored arguments_json is what the transcript card parses, so the batch must not sit behind a
        // wrapper property.
        var schema = new AskUserToolset().CreateTools(new ScriptedQuestionHandler(Skip)).Single().Tool.JsonSchema;

        var questions = schema.GetProperty("properties").GetProperty("questions");
        questions.GetProperty("type")[0].GetString().Should().Be("array");
        var promptProperties = questions.GetProperty("items").GetProperty("properties");
        promptProperties.TryGetProperty("question", out _).Should().BeTrue();
        promptProperties.TryGetProperty("options", out _).Should().BeTrue();
        promptProperties.TryGetProperty("multiSelect", out _).Should().BeTrue();
    }

    [Fact]
    public async Task A_full_batch_reports_every_answer_in_both_the_content_and_the_detail()
    {
        var handler = new ScriptedQuestionHandler(request => new UserQuestionAnswer(request.RequestId, [
            new UserQuestionPromptAnswer(request.Prompts[0].PromptId, ["PostgreSQL"], null, Skipped: false),
            new UserQuestionPromptAnswer(request.Prompts[1].PromptId, [], "Prisma", Skipped: false)
        ], Skipped: false));

        var result = await InvokeAsync(handler, TwoQuestions());

        result.Status.Should().Be(ToolCallStatus.Completed);
        result.Summary.Should().Be("Answered 2 of 2");
        result.Detail.Should().Be(
            "User answered:" + Environment.NewLine
            + "1. Which database should this service use? → PostgreSQL" + Environment.NewLine
            + "2. Which ORM? → Other: Prisma");
        var answers = result.Content.GetProperty("answers");
        answers.GetArrayLength().Should().Be(2);
        answers[0].GetProperty("question").GetString().Should().Be("Which database should this service use?");
        answers[0].GetProperty("selected")[0].GetString().Should().Be("PostgreSQL");
        answers[1].GetProperty("other").GetString().Should().Be("Prisma");
        result.Content.GetProperty("skipped").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task A_partial_answer_marks_the_unanswered_prompt_and_still_completes()
    {
        var handler = new ScriptedQuestionHandler(request => new UserQuestionAnswer(request.RequestId, [
            new UserQuestionPromptAnswer(request.Prompts[0].PromptId, ["PostgreSQL"], null, Skipped: false),
            new UserQuestionPromptAnswer(request.Prompts[1].PromptId, [], null, Skipped: true)
        ], Skipped: false));

        var result = await InvokeAsync(handler, TwoQuestions());

        result.Status.Should().Be(ToolCallStatus.Completed);
        result.Summary.Should().Be("Answered 1 of 2");
        result.Detail.Should().Contain("→ (not answered)");
        result.Content.GetProperty("answers")[1].GetProperty("selected").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task A_skipped_batch_completes_instead_of_cancelling()
    {
        var handler = new ScriptedQuestionHandler(Skip);

        var result = await InvokeAsync(handler, TwoQuestions());

        // Canceled would be replayed to the model as a tool exception, so a user skip must stay Completed.
        result.Status.Should().Be(ToolCallStatus.Completed);
        result.Summary.Should().Be("Question skipped");
        result.Detail.Should().Be(
            "User skipped the questions. Continue with your best judgment and state the assumption you made.");
        result.Content.GetProperty("skipped").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task An_invalid_questionnaire_fails_without_asking_the_user()
    {
        var handler = new ScriptedQuestionHandler(Skip);

        var result = await InvokeAsync(handler, Questions(new
        {
            question = "Which database?",
            options = new[] { new { label = "PostgreSQL" } }
        }));

        result.Status.Should().Be(ToolCallStatus.Failed);
        result.Summary.Should().Be("Invalid question: Every question needs at least 2 options.");
        result.Detail.Should().Be(result.Summary);
        result.Content.GetProperty("error").GetString().Should().Be("Every question needs at least 2 options.");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failure_never_carries_a_stack_trace_or_a_local_path()
    {
        var result = await InvokeAsync(new ScriptedQuestionHandler(Skip), Questions(new
        {
            question = "Which database?",
            options = new[] { new { label = "Other" }, new { label = "SQLite" } }
        }));

        result.Status.Should().Be(ToolCallStatus.Failed);
        result.Detail.Should().NotContain("   at ").And.NotContain(".cs:line").And.NotContain(":\\");
    }

    [Fact]
    public async Task The_per_turn_quota_counts_questions_across_calls()
    {
        var handler = new ScriptedQuestionHandler(Skip);
        var invoke = CreateInvoker(handler);
        var perCall = AskUserRules.MaximumPromptsPerCall;
        var calls = AskUserRules.MaximumQuestionsPerTurn / perCall;

        for (var call = 0; call < calls; call++)
        {
            (await invoke(FourQuestions())).Status.Should().Be(ToolCallStatus.Completed);
        }

        var rejected = await invoke(FourQuestions());

        rejected.Status.Should().Be(ToolCallStatus.Failed);
        rejected.Summary.Should().Be("Question limit reached");
        rejected.Detail.Should().Contain(AskUserRules.MaximumQuestionsPerTurn.ToString());
        handler.Requests.Should().HaveCount(calls);
    }

    private static UserQuestionAnswer Skip(UserQuestionRequest request)
        => new(request.RequestId, [], Skipped: true);

    private static Task<DirectToolResult> InvokeAsync(ScriptedQuestionHandler handler, Dictionary<string, object?> arguments)
        => CreateInvoker(handler)(arguments);

    private static Func<Dictionary<string, object?>, Task<DirectToolResult>> CreateInvoker(ScriptedQuestionHandler handler)
    {
        var request = (DirectChatTurnRequest)DirectAgentChatRuntimeTests.CreateRequest(Guid.NewGuid());
        var bindings = DirectTurnCapabilityResolver
            .BindTools(request, new AskUserToolset().CreateTools(handler))
            .ToDictionary(binding => binding.Tool.Name, StringComparer.Ordinal);
        var invoker = new DirectToolInvoker(request, bindings);
        return async arguments =>
        {
            var raw = await invoker.InvokeAsync(
                new FunctionCallContent("call-1", AskUserRules.ToolName, arguments), CancellationToken.None);
            return raw.Should().BeOfType<DirectToolResult>().Subject;
        };
    }

    private static Dictionary<string, object?> TwoQuestions()
        => Questions(
            new
            {
                question = "Which database should this service use?",
                header = "Database",
                options = new[]
                {
                    new { label = "PostgreSQL", description = (string?)"Relational" },
                    new { label = "SQLite", description = (string?)null }
                }
            },
            new
            {
                question = "Which ORM?",
                options = new[] { new { label = "Entity Framework" }, new { label = "Dapper" } }
            });

    private static Dictionary<string, object?> FourQuestions()
        => Questions(Enumerable.Range(0, AskUserRules.MaximumPromptsPerCall)
            .Select(index => (object)new
            {
                question = $"Question {index}?",
                options = new[] { new { label = "Yes" }, new { label = "No" } }
            })
            .ToArray());

    private static Dictionary<string, object?> Questions(params object[] questions)
        => new(StringComparer.Ordinal) { ["questions"] = JsonSerializer.SerializeToElement(questions) };

    private sealed class ScriptedQuestionHandler(Func<UserQuestionRequest, UserQuestionAnswer> answer) : IUserQuestionHandler
    {
        public List<UserQuestionRequest> Requests { get; } = [];

        public Task<UserQuestionAnswer> RequestAnswerAsync(
            UserQuestionRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(answer(request));
        }

        public bool TryAnswer(Guid requestId, UserQuestionAnswer requestAnswer) => false;
    }
}
