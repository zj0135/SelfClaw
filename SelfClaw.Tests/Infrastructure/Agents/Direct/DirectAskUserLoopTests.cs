using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Agents.Direct;
using SelfClaw.Infrastructure.Agents.Direct.Abstractions;
using SelfClaw.Infrastructure.Agents.Direct.Capabilities;
using SelfClaw.Infrastructure.Agents.Direct.Tools;
using SelfClaw.Infrastructure.Agents.Direct.Tools.Models;
using SelfClaw.Infrastructure.AiProviders.Models;

namespace SelfClaw.Tests.Infrastructure.Agents.Direct;

public sealed class DirectAskUserLoopTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Answer_or_skip_returns_to_the_model_before_any_remaining_call_can_execute(bool skip)
    {
        var provider = new QuestionProvider();
        var questions = new QuestionHandler();
        var executions = new List<string>();
        var checkpoint = new Checkpoint();
        var runtime = CreateRuntime(provider, questions, executions, checkpoint, out var request);
        var run = CollectAsync(runtime, request);
        var asked = await questions.Asked.Task.WaitAsync(Timeout);
        try
        {
            asked.ConversationId.Should().Be(request.ConversationId);
            asked.TurnId.Should().Be(request.TurnId);
            executions.Should().Equal("read");
            provider.Requests.Should().ContainSingle();
            checkpoint.Calls.Should().Be(2);
        }
        finally
        {
            questions.Answer.TrySetResult(new UserQuestionAnswer(asked.RequestId,
                skip ? [] : [new UserQuestionPromptAnswer(asked.Prompts[0].PromptId, ["Python"], null, false)], skip));
        }

        var events = await run.WaitAsync(Timeout);

        executions.Should().Equal(["read", "Python"], "the C# call predates the user's decision");
        checkpoint.Calls.Should().Be(3, "canceled calls never pass the execution checkpoint");
        var results = provider.Requests[1].SelectMany(message => message.Contents)
            .OfType<FunctionResultContent>().ToArray();
        results.Select(result => result.CallId).Should().Equal("before", "question", "stale-write", "stale-question");
        var answer = results[1].Result.Should().BeOfType<DirectToolResult>().Subject;
        answer.Content.GetProperty("skipped").GetBoolean().Should().Be(skip);
        if (!skip) answer.Content.GetRawText().Should().Contain("Python");
        AssertCanceledTail(events, results);
        events.OfType<RunCompletedEvent>().Should().ContainSingle().Which.Status.Should().Be(RunCompletionStatus.Succeeded);
    }

    [Fact]
    public async Task Invalid_questions_also_require_a_new_decision_before_the_remaining_tools()
    {
        var provider = new QuestionProvider { InvalidQuestion = true };
        var questions = new QuestionHandler();
        var executions = new List<string>();
        var runtime = CreateRuntime(provider, questions, executions, new Checkpoint(), out var request);

        var events = await CollectAsync(runtime, request).WaitAsync(Timeout);

        questions.Asked.Task.IsCompleted.Should().BeFalse();
        executions.Should().Equal("read", "Python");
        var results = provider.Requests[1].SelectMany(message => message.Contents).OfType<FunctionResultContent>().ToArray();
        results[1].Result.Should().BeOfType<DirectToolResult>().Which.Status.Should().Be(ToolCallStatus.Failed);
        AssertCanceledTail(events, results);
    }

    [Fact]
    public async Task Stopping_while_awaiting_an_answer_never_executes_the_tail()
    {
        var provider = new QuestionProvider();
        var questions = new QuestionHandler();
        var executions = new List<string>();
        var runtime = CreateRuntime(provider, questions, executions, new Checkpoint(), out var request);
        using var stop = new CancellationTokenSource();
        var run = CollectAsync(runtime, request, stop.Token);
        await questions.Asked.Task.WaitAsync(Timeout);

        stop.Cancel();

        var action = () => run.WaitAsync(Timeout);
        await action.Should().ThrowAsync<OperationCanceledException>();
        executions.Should().Equal("read");
        provider.Requests.Should().ContainSingle();
    }

    private static void AssertCanceledTail(IReadOnlyList<AgentStreamEvent> events, FunctionResultContent[] results)
    {
        foreach (var result in results.Skip(2))
        {
            result.Result.Should().BeOfType<DirectToolResult>().Which.Status.Should().Be(ToolCallStatus.Canceled);
        }
        var completed = events.OfType<ToolCallCompletedEvent>().ToArray();
        completed.Select(item => item.ToolCallId).Should().OnlyHaveUniqueItems();
        completed.Where(item => item.ToolCallId.StartsWith("stale-", StringComparison.Ordinal))
            .Should().HaveCount(2).And.OnlyContain(item => item.Status == ToolCallStatus.Canceled &&
                item.ResultSummary == DirectLoopMessages.UserQuestionBoundaryReason);
    }

    private static DirectAgentChatRuntime CreateRuntime(QuestionProvider provider, QuestionHandler questions,
        List<string> executions, Checkpoint checkpoint, out DirectChatTurnRequest request)
    {
        var factory = new DirectAgentChatRuntimeTests.FakeChatClientFactory(provider);
        request = (DirectChatTurnRequest)DirectAgentChatRuntimeTests.CreateRequest(factory.Profile.Id);
        request = request with { ToolPermissionMode = ToolPermissionMode.FullAccess, ToolExecutionCheckpoint = checkpoint };
        var tools = new AskUserToolset().CreateTools(questions, request.ConversationId, request.TurnId).ToList();
        tools.Add(DirectToolBindings.Create((Func<string, DirectToolResult>)(language =>
        {
            executions.Add(language);
            return new DirectToolResult(ToolCallStatus.Completed, language, JsonSerializer.SerializeToElement(language), language);
        }), "work", "Record an operation.", ToolCallKind.Edit, ToolSourceKind.BuiltIn));
        var lease = new DirectTurnCapabilityLease([], tools, new Dictionary<Guid, string>(), []);
        return DirectAgentChatRuntimeTests.CreateRuntime(factory, new Resolver(lease));
    }

    private static async Task<List<AgentStreamEvent>> CollectAsync(DirectAgentChatRuntime runtime,
        DirectChatTurnRequest request, CancellationToken cancellationToken = default)
    {
        var events = new List<AgentStreamEvent>();
        await foreach (var item in runtime.StreamTurnAsync(request, cancellationToken)) events.Add(item);
        return events;
    }

    private sealed class QuestionProvider : IChatClient
    {
        internal bool InvalidQuestion { get; init; }
        internal List<ChatMessage[]> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(messages.ToArray());
            await Task.CompletedTask;
            if (Requests.Count == 1)
            {
                var questions = JsonSerializer.SerializeToElement(InvalidQuestion ? [] : new[]
                {
                    new { question = "Which language?", options = new[] { new { label = "C#" }, new { label = "Python" } } }
                });
                var arguments = new Dictionary<string, object?> { ["questions"] = questions };
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [Work("before", "read"), new FunctionCallContent("question", AskUserRules.ToolName, arguments),
                     Work("stale-write", "C#"), new FunctionCallContent("stale-question", AskUserRules.ToolName, arguments)])
                    { FinishReason = ChatFinishReason.ToolCalls };
            }
            else if (Requests.Count == 2)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, [Work("revised", "Python")])
                    { FinishReason = ChatFinishReason.ToolCalls };
            }
            else
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, "done") { FinishReason = ChatFinishReason.Stop };
            }
        }

        private static FunctionCallContent Work(string id, string language)
            => new(id, "work", new Dictionary<string, object?> { ["language"] = language });

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class QuestionHandler : IUserQuestionHandler
    {
        internal TaskCompletionSource<UserQuestionRequest> Asked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<UserQuestionAnswer> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<UserQuestionAnswer> RequestAnswerAsync(UserQuestionRequest request, CancellationToken cancellationToken = default)
        {
            Asked.TrySetResult(request);
            return Answer.Task.WaitAsync(cancellationToken);
        }

        public bool TryAnswer(Guid requestId, UserQuestionAnswer answer) => Answer.TrySetResult(answer);
    }

    private sealed class Checkpoint : IToolExecutionCheckpoint
    {
        internal int Calls { get; private set; }
        public Task BeforeExecutionAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    private sealed class Resolver(DirectTurnCapabilityLease lease) : IDirectTurnCapabilityResolver
    {
        public Task<DirectTurnCapabilityLease> ResolveAsync(DirectChatTurnRequest request, ToolImageCapability imageCapability,
            CancellationToken cancellationToken = default) => Task.FromResult(lease);
    }
}
