using SelfClaw.Core.Interfaces;
using SelfClaw.Infrastructure.Agents.Direct.Tools.Models;
using SelfClaw.Infrastructure.Agents.Direct.Tools;
using FluentAssertions;
using Microsoft.Extensions.AI;
using System.Text.Json;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Core.Runtime.Tooling;
using SelfClaw.Desktop.Services.Automation;
using SelfClaw.Infrastructure.Agents.Runtime;

namespace SelfClaw.Tests.Infrastructure.Agents.Direct.Tools;

public sealed class DirectToolInvokerTests
{
    [Fact]
    public async Task InvokeAsync_FullAccess_BypassesApproval()
    {
        var handler = new RecordingApprovalHandler(approved: false);
        var pipeline = CreatePipeline(ToolPermissionMode.FullAccess, handler);

        var result = await pipeline.InvokeAsync();

        result.Status.Should().Be(ToolCallStatus.Completed);
        result.Content.GetString().Should().Be("ok");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task InvokeAsync_AutomationFullAccess_executesAnApprovalGatedToolWithoutPrompting()
    {
        var handler = new AutomationToolApprovalHandler(Microsoft.Extensions.Logging.Abstractions.NullLogger<AutomationToolApprovalHandler>.Instance);
        var pipeline = CreatePipeline(
            ToolPermissionMode.FullAccess,
            handler,
            origin: DirectTurnOrigin.Automation);

        var result = await pipeline.InvokeAsync();

        result.Status.Should().Be(ToolCallStatus.Completed);
        result.Content.GetString().Should().Be("ok");
        pipeline.Order.Should().Equal("tool");
    }

    [Fact]
    public async Task InvokeAsync_RequireApproval_PropagatesSourceAndInvokesWhenApproved()
    {
        var handler = new RecordingApprovalHandler(approved: true);
        var pipeline = CreatePipeline(ToolPermissionMode.RequireApproval, handler);

        var result = await pipeline.InvokeAsync();

        result.Status.Should().Be(ToolCallStatus.Completed);
        result.Content.GetString().Should().Be("ok");
        var request = handler.Requests.Should().ContainSingle().Subject;
        request.ToolName.Should().Be("mcp__git__echo");
        request.DisplayName.Should().Be("echo");
        request.SourceKind.Should().Be(ToolSourceKind.Mcp);
        request.SourceId.Should().Be("git");
        request.TransportSummary.Should().Be("stdio: node");
        request.AnnotationsJson.Should().Be("{\"readOnlyHint\":true}");
        request.ArgumentsJson.Should().Contain("ok");
    }

    [Fact]
    public async Task InvokeAsync_WhenRejected_ReturnsCanceledWithoutWritingAnExecutionCheckpoint()
    {
        var checkpoint = new CountingCheckpoint();
        var pipeline = CreatePipeline(
            ToolPermissionMode.RequireApproval,
            new RecordingApprovalHandler(approved: false),
            _ => checkpoint);

        var result = await pipeline.InvokeAsync();

        result.Should().BeOfType<DirectToolResult>().Which.Status.Should().Be(ToolCallStatus.Canceled);
        checkpoint.Calls.Should().Be(0);
        pipeline.Order.Should().BeEmpty();
    }

    [Fact]
    public async Task InvokeAsync_WithoutApprovalHandler_DeniesWithoutWritingAnExecutionCheckpoint()
    {
        var checkpoint = new CountingCheckpoint();
        var pipeline = CreatePipeline(ToolPermissionMode.RequireApproval, approvalHandler: null, _ => checkpoint);

        var result = await pipeline.InvokeAsync();

        result.Should().BeOfType<DirectToolResult>().Which.Status.Should().Be(ToolCallStatus.Canceled);
        checkpoint.Calls.Should().Be(0);
        pipeline.Order.Should().BeEmpty();
    }

    [Fact]
    public async Task InvokeAsync_RequiresApprovalFalse_SkipsApproval()
    {
        var handler = new RecordingApprovalHandler(approved: false);
        var pipeline = CreatePipeline(
            ToolPermissionMode.RequireApproval,
            handler,
            requiresApproval: false);

        var result = await pipeline.InvokeAsync();

        result.Status.Should().Be(ToolCallStatus.Completed);
        result.Content.GetString().Should().Be("ok");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task InvokeAsync_WritesTheCheckpointBeforeExecutingTheTool()
    {
        var pipeline = CreatePipeline(
            ToolPermissionMode.RequireApproval,
            new RecordingApprovalHandler(approved: true),
            order => new RecordingCheckpoint(order));

        var result = await pipeline.InvokeAsync();

        result.Status.Should().Be(ToolCallStatus.Completed);
        result.Content.GetString().Should().Be("ok");
        pipeline.Order.Should().Equal("checkpoint", "tool");
    }

    [Fact]
    public async Task InvokeAsync_WhenTheCheckpointThrows_DoesNotExecuteTheTool()
    {
        var pipeline = CreatePipeline(
            ToolPermissionMode.RequireApproval,
            new RecordingApprovalHandler(approved: true),
            _ => new ThrowingCheckpoint());

        var action = () => pipeline.InvokeAsync().AsTask();

        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("checkpoint failed");
        pipeline.Order.Should().BeEmpty();
    }

    [Fact]
    public async Task InvokeAsync_PropagatesCancellationWithoutInvokingTheTool()
    {
        var pipeline = CreatePipeline(ToolPermissionMode.FullAccess, approvalHandler: null);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var action = () => pipeline.InvokeAsync(cancellation.Token).AsTask();

        await action.Should().ThrowAsync<OperationCanceledException>();
        pipeline.Order.Should().BeEmpty();
    }

    // A hallucinated tool name is a model-side mistake the model has to see and correct, so it comes back as
    // a Failed result instead of ending the turn.
    [Fact]
    public async Task InvokeAsync_UnboundToolName_ReturnsAFailedResultWithoutExecutingAnything()
    {
        var pipeline = CreatePipeline(ToolPermissionMode.FullAccess, approvalHandler: null);

        var result = await pipeline.Invoker.InvokeAsync(
            new FunctionCallContent("call-1", "other", new AIFunctionArguments()),
            CancellationToken.None);

        result.Status.Should().Be(ToolCallStatus.Failed);
        result.Summary.Should().Contain("'other'").And.Contain("not bound");
        result.Detail.Should().Be(result.Summary, "the model sees exactly what the transcript shows");
        pipeline.Order.Should().BeEmpty();
    }

    [Fact]
    public async Task InvokeAsync_RepeatedUnboundToolName_StillEndsTheTurnAtTheFaultLimit()
    {
        var pipeline = CreatePipeline(ToolPermissionMode.FullAccess, approvalHandler: null);
        var call = new FunctionCallContent("call-1", "other", new AIFunctionArguments());

        for (var attempt = 0; attempt < DirectToolInvoker.MaximumConsecutiveToolFaults; attempt++)
        {
            var result = await pipeline.Invoker.InvokeAsync(call, CancellationToken.None);
            result.Status.Should().Be(ToolCallStatus.Failed);
        }

        var action = () => pipeline.Invoker.InvokeAsync(call, CancellationToken.None).AsTask();

        await action.Should().ThrowAsync<DirectToolFaultLimitException>().WithMessage("*other*");
        pipeline.Order.Should().BeEmpty();
    }

    [Fact]
    public async Task InvokeAsync_UnparseableArguments_ReturnAFailedResultWithoutExecutingTheTool()
    {
        var pipeline = CreatePipeline(ToolPermissionMode.FullAccess, approvalHandler: null);
        var call = new FunctionCallContent("call-1", "mcp__git__echo", null)
        {
            Exception = new JsonException("invalid JSON")
        };

        var result = await pipeline.Invoker.InvokeAsync(call, CancellationToken.None);

        result.Status.Should().Be(ToolCallStatus.Failed);
        result.Summary.Should().Contain("could not be parsed").And.Contain("invalid JSON");
        pipeline.Order.Should().BeEmpty("the tool itself never ran");
    }

    // The proof that asking is an interruption, not an approval: FullAccess auto-approves gates, yet the call
    // still hangs on the question handler and never touches the approval handler.
    [Fact]
    public async Task InvokeAsync_FullAccess_still_waits_for_the_user_answer_on_ask_user()
    {
        var approvals = new RecordingApprovalHandler(approved: true);
        var questions = new BlockingQuestionHandler();
        var binding = new AskUserToolset().CreateTools(questions).Single();
        var request = CreateRequest(ToolPermissionMode.FullAccess, approvals, checkpoint: null) with
        {
            UserQuestionHandler = questions
        };
        var invoker = new DirectToolInvoker(
            request,
            new Dictionary<string, DirectToolBinding>(StringComparer.Ordinal) { [binding.Tool.Name] = binding });

        var invocation = invoker.InvokeAsync(
            new FunctionCallContent(
                "call-1",
                AskUserRules.ToolName,
                new AIFunctionArguments
                {
                    ["questions"] = JsonSerializer.SerializeToElement(new[]
                    {
                        new
                        {
                            question = "Which database should this service use?",
                            options = new[] { new { label = "PostgreSQL" }, new { label = "SQLite" } }
                        }
                    })
                }),
            CancellationToken.None).AsTask();

        var asked = await questions.Asked;
        invocation.IsCompleted.Should().BeFalse();
        approvals.Requests.Should().BeEmpty();

        questions.TryAnswer(asked.RequestId, new UserQuestionAnswer(asked.RequestId, [], Skipped: true)).Should().BeTrue();

        var result = await invocation;
        result.Should().BeOfType<DirectToolResult>()
            .Which.Status.Should().Be(ToolCallStatus.Completed);
        approvals.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task InvokeAsync_bounds_the_tool_result_text_before_it_leaves_the_invoker()
    {
        var huge = new string('x', ToolResultTextLimits.MaximumCharacters + 5_000);
        var marshaled = new DirectToolResult(
            ToolCallStatus.Completed,
            "huge result",
            JsonSerializer.SerializeToElement(new { length = huge.Length }),
            huge);
        var pipeline = CreatePipeline(ToolPermissionMode.FullAccess, null, marshaledResult: marshaled);

        var result = await pipeline.InvokeAsync();

        var bounded = result.Should().BeOfType<DirectToolResult>().Subject;
        bounded.Detail.Should().HaveLength(ToolResultTextLimits.MaximumCharacters)
            .And.EndWith("[SelfClaw truncated the tool result at 64 KiB.]");
        // The structured payload is small here, so it must survive untouched: the limit trims text, it does
        // not reshape what the model can read.
        bounded.Content.GetRawText().Should().Contain("length");
    }

    [Fact]
    public async Task InvokeAsync_leaves_a_result_within_the_limit_identical()
    {
        var marshaled = new DirectToolResult(
            ToolCallStatus.Completed, "small", JsonSerializer.SerializeToElement(new { ok = true }), "small");
        var pipeline = CreatePipeline(ToolPermissionMode.FullAccess, null, marshaledResult: marshaled);

        var result = await pipeline.InvokeAsync();

        result.Should().BeSameAs(marshaled);
    }

    private static TestPipeline CreatePipeline(
        ToolPermissionMode permissionMode,
        IToolApprovalHandler? approvalHandler,
        Func<List<string>, IToolExecutionCheckpoint>? checkpointFactory = null,
        bool requiresApproval = true,
        DirectTurnOrigin origin = DirectTurnOrigin.Interactive,
        DirectToolResult? marshaledResult = null)
    {
        var order = new List<string>();
        var options = new AIFunctionFactoryOptions
        {
            Name = "mcp__git__echo",
            Description = "Echo a value.",
            // The invoker's contract is one DirectToolResult per call; this probe reports the value it echoed.
            ExcludeResultSchema = true,
            MarshalResult = (value, _, _) => ValueTask.FromResult<object?>(
                marshaledResult ?? new DirectToolResult(
                    ToolCallStatus.Completed,
                    "echoed",
                    JsonSerializer.SerializeToElement(value),
                    value?.ToString()))
        };

        var function = AIFunctionFactory.Create(
            (string value) =>
            {
                order.Add("tool");
                return value;
            },
            options);
        var binding = new DirectToolBinding(
            function,
            new DirectToolDescriptor(ToolCallKind.Run, ToolSourceKind.Mcp, "git", "echo"),
            requiresApproval,
            "stdio: node",
            "{\"readOnlyHint\":true}");
        var request = CreateRequest(permissionMode, approvalHandler, checkpointFactory?.Invoke(order), origin);
        var invoker = new DirectToolInvoker(
            request,
            new Dictionary<string, DirectToolBinding>(StringComparer.Ordinal)
            {
                [function.Name] = binding
            });
        return new TestPipeline(invoker, function, order);
    }

    private static DirectChatTurnRequest CreateRequest(
        ToolPermissionMode permissionMode,
        IToolApprovalHandler? approvalHandler,
        IToolExecutionCheckpoint? checkpoint,
        DirectTurnOrigin origin = DirectTurnOrigin.Interactive)
    {
        var now = DateTimeOffset.UtcNow;
        return new DirectChatTurnRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            null,
            new AgentRuntimeDefinition(
                "direct-test", "Direct", "test", AgentExecutionMode.Direct,
                AgentRuntimeDefinition.SystemToolPolicy, [], [], [], [], "Follow project instructions."),
            [],
            [],
            null,
            permissionMode,
            approvalHandler,
            new DirectTurnExecutionContext(origin, null, null),
            ToolExecutionCheckpoint: checkpoint);
    }

    private sealed class TestPipeline(DirectToolInvoker invoker, AIFunction function, List<string> order)
    {
        internal DirectToolInvoker Invoker { get; } = invoker;
        internal List<string> Order { get; } = order;

        internal ValueTask<DirectToolResult> InvokeAsync(CancellationToken cancellationToken = default)
        {
            var arguments = new AIFunctionArguments { ["value"] = "ok" };
            return Invoker.InvokeAsync(
                new FunctionCallContent("call-1", function.Name, arguments),
                cancellationToken);
        }
    }

    private sealed class CountingCheckpoint : IToolExecutionCheckpoint
    {
        internal int Calls { get; private set; }
        public Task BeforeExecutionAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingCheckpoint(List<string> order) : IToolExecutionCheckpoint
    {
        public Task BeforeExecutionAsync(CancellationToken cancellationToken)
        {
            order.Add("checkpoint");
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingCheckpoint : IToolExecutionCheckpoint
    {
        public Task BeforeExecutionAsync(CancellationToken cancellationToken)
            => throw new InvalidOperationException("checkpoint failed");
    }

    private sealed class RecordingApprovalHandler : IToolApprovalHandler
    {
        private readonly bool _approved;

        public RecordingApprovalHandler(bool approved)
        {
            _approved = approved;
        }

        public List<ToolApprovalRequest> Requests { get; } = [];

        public Task<ToolApprovalDecision> RequestApprovalAsync(
            ToolApprovalRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(_approved ? ToolApprovalDecision.Approved : ToolApprovalDecision.Denied);
        }

        public bool TryResolve(Guid toolExecutionId, ToolApprovalDecision decision) => false;
    }

    private sealed class BlockingQuestionHandler : IUserQuestionHandler
    {
        private readonly TaskCompletionSource<UserQuestionRequest> _asked =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<UserQuestionAnswer>? _answer;

        public Task<UserQuestionRequest> Asked => _asked.Task;

        public Task<UserQuestionAnswer> RequestAnswerAsync(
            UserQuestionRequest request,
            CancellationToken cancellationToken = default)
        {
            _answer = new TaskCompletionSource<UserQuestionAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
            _asked.TrySetResult(request);
            return _answer.Task;
        }

        public bool TryAnswer(Guid requestId, UserQuestionAnswer answer)
            => _answer?.TrySetResult(answer) == true;
    }
}
