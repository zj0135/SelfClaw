using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Agents.Direct.Hooks.Models;
using SelfClaw.Infrastructure.Agents.Direct.Tools;
using SelfClaw.Infrastructure.Agents.Direct.Tools.Models;
using SelfClaw.Infrastructure.Extensions.Plugins.Models;
using SelfClaw.Tests.Infrastructure.Agents.Direct.Hooks.TestDoubles;

namespace SelfClaw.Tests.Infrastructure.Agents.Direct.Tools;

public sealed class DirectToolInvokerHookTests
{
    [Fact]
    public async Task Deny_returns_a_blocked_result_without_executing_or_checkpointing()
    {
        var checkpoint = new CountingCheckpoint();
        var fixture = new Fixture(
            ToolPermissionMode.FullAccess,
            approvalHandler: null,
            checkpoint: checkpoint,
            hooks: [Hook(PluginHookEvent.ToolExecuting, """{"decision":"deny","reason":"dangerous"}""")]);

        var result = await fixture.InvokeAsync();

        var blocked = result.Should().BeOfType<DirectToolResult>().Subject;
        blocked.Status.Should().Be(ToolCallStatus.Blocked);
        blocked.Summary.Should().Be("Blocked by hook 'alpha/a': dangerous");
        fixture.Executions.Should().Be(0);
        checkpoint.Calls.Should().Be(0);
        fixture.Invoker.TryTakeOutcome("call-1")!.BlockedBy.Should().Be(new HookSource("alpha", "a"));
    }

    [Fact]
    public async Task Ask_forces_approval_even_in_full_access()
    {
        var approval = new RecordingApprovalHandler(approved: true);
        var fixture = new Fixture(
            ToolPermissionMode.FullAccess,
            approval,
            checkpoint: null,
            hooks: [Hook(PluginHookEvent.ToolExecuting, """{"decision":"ask","reason":"review"}""")]);

        var result = await fixture.InvokeAsync();

        result.Should().BeOfType<DirectToolResult>().Which.Status.Should().Be(ToolCallStatus.Completed);
        var request = approval.Requests.Should().ContainSingle().Subject;
        request.ApprovalRequiredBy.Should().BeEquivalentTo([new HookSource("alpha", "a")]);
        request.ApprovalReason.Should().Be("review");
    }

    [Fact]
    public async Task A_rewritten_arguments_object_is_executed_and_reported()
    {
        var approval = new RecordingApprovalHandler(approved: true);
        var fixture = new Fixture(
            ToolPermissionMode.RequireApproval,
            approval,
            checkpoint: null,
            hooks: [Hook(PluginHookEvent.ToolExecuting, """{"updatedArguments":{"value":"rewritten"}}""")],
            requiresApproval: true);

        var result = await fixture.InvokeAsync();

        fixture.LastValue.Should().Be("rewritten");
        var request = approval.Requests.Should().ContainSingle().Subject;
        request.ArgumentsJson.Should().Contain("rewritten");
        request.ArgumentsModifiedBy.Should().BeEquivalentTo([new HookSource("alpha", "a")]);
        var completed = result.Should().BeOfType<DirectToolResult>().Subject;
        completed.HookFeedback.Should().NotBeNull();
        completed.HookFeedback![0].Source.Should().Be("selfclaw");
        completed.HookFeedback[0].Text.Should().Contain("Arguments were modified by hook 'alpha/a'");
    }

    [Fact]
    public async Task With_no_hooks_the_tool_result_is_returned_unchanged()
    {
        var fixture = new Fixture(ToolPermissionMode.FullAccess, approvalHandler: null, checkpoint: null, hooks: []);

        var result = await fixture.InvokeAsync();

        result.Should().BeSameAs(fixture.ToolResult);
        fixture.Invoker.TryTakeOutcome("call-1").Should().BeNull();
    }

    [Fact]
    public async Task ToolExecuted_feedback_is_attached_to_the_result()
    {
        var fixture = new Fixture(
            ToolPermissionMode.FullAccess,
            approvalHandler: null,
            checkpoint: null,
            hooks:
            [
                Hook(PluginHookEvent.ToolExecuting, """{"updatedArguments":{"value":"changed"}}"""),
                Hook(PluginHookEvent.ToolExecuted, """{"feedback":"keep going"}""", hookId: "b")
            ]);

        var result = await fixture.InvokeAsync();

        var completed = result.Should().BeOfType<DirectToolResult>().Subject;
        completed.HookFeedback.Should().HaveCount(2);
        completed.HookFeedback!.Select(item => item.Source).Should().Equal("selfclaw", "alpha/b");
        completed.HookFeedback[1].Text.Should().Be("keep going");
    }

    [Fact]
    public async Task A_throwing_tool_still_runs_toolExecuted_and_records_the_side_band_outcome()
    {
        var fixture = new Fixture(
            ToolPermissionMode.FullAccess,
            approvalHandler: null,
            checkpoint: null,
            hooks:
            [
                Hook(PluginHookEvent.ToolExecuting, """{"updatedArguments":{"value":"changed"}}"""),
                Hook(PluginHookEvent.ToolExecuted, """{"feedback":"note"}""", hookId: "b")
            ],
            throws: true);

        var action = () => fixture.InvokeAsync().AsTask();

        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("tool exploded");
        var outcome = fixture.Invoker.TryTakeOutcome("call-1");
        outcome.Should().NotBeNull();
        outcome!.EffectiveArgumentsJson.Should().Contain("changed");
        outcome.Feedback.Should().ContainSingle().Which.Text.Should().Be("note");
    }

    [Fact]
    public async Task Call_count_tracks_processed_calls()
    {
        var fixture = new Fixture(ToolPermissionMode.FullAccess, approvalHandler: null, checkpoint: null, hooks: []);

        await fixture.InvokeAsync();
        await fixture.InvokeAsync();

        fixture.Invoker.CallCount.Should().Be(2);
    }

    private static HookSpec Hook(PluginHookEvent hookEvent, string stdout, string hookId = "a")
        => new(
            new ResolvedPluginHook(
                "alpha",
                "1.0.0",
                Path.Combine(Path.GetTempPath(), "alpha"),
                new PluginHookContribution(
                    hookId,
                    hookEvent,
                    HookTestFactory.EmptyMatcher,
                    "hook.exe",
                    [],
                    TimeSpan.FromSeconds(10),
                    PluginHookFailurePolicy.Continue,
                    RunAsync: false,
                    IncludeRequestBody: false),
                DeclarationOrder: 0,
                Inherited: false),
            stdout);

    private sealed record HookSpec(ResolvedPluginHook Hook, string Stdout);

    private sealed class Fixture
    {
        private readonly FunctionInvocationContext _context;

        internal Fixture(
            ToolPermissionMode permissionMode,
            IToolApprovalHandler? approvalHandler,
            IToolExecutionCheckpoint? checkpoint,
            IReadOnlyList<HookSpec> hooks,
            bool requiresApproval = false,
            bool throws = false)
        {
            var function = AIFunctionFactory.Create(
                (string value) =>
                {
                    LastValue = value;
                    Executions++;
                    if (throws)
                    {
                        throw new InvalidOperationException("tool exploded");
                    }

                    ToolResult = new DirectToolResult(
                        ToolCallStatus.Completed, "ok", JsonSerializer.SerializeToElement(value), value);
                    return ToolResult;
                },
                new AIFunctionFactoryOptions
                {
                    Name = "read_file",
                    Description = "Reads a file.",
                    ExcludeResultSchema = true,
                    MarshalResult = (value, _, _) => ValueTask.FromResult<object?>(value)
                });
            var binding = new DirectToolBinding(
                function,
                new DirectToolDescriptor("read_file", ToolCallKind.Read),
                requiresApproval);
            var request = new DirectChatTurnRequest(
                Guid.NewGuid(),
                Guid.NewGuid(),
                null,
                new AgentRuntimeDefinition(
                    "build", "Build", "test", AgentExecutionMode.Direct,
                    AgentRuntimeDefinition.SystemToolPolicy, [], [], [], [], "instructions"),
                [],
                null,
                permissionMode,
                approvalHandler,
                new DirectTurnExecutionContext(DirectTurnOrigin.Interactive, null, null),
                ToolExecutionCheckpoint: checkpoint);
            var stdoutByHookId = hooks.ToDictionary(spec => spec.Hook.Contribution.Id, spec => spec.Stdout);
            var turnHooks = HookTestFactory.Create(
                hooks.Select(spec => spec.Hook).ToArray(),
                (_, payload, _, _) =>
                {
                    using var document = JsonDocument.Parse(payload);
                    var hookId = document.RootElement.GetProperty("hookId").GetString()!;
                    return Task.FromResult(HookTestFactory.Success(stdoutByHookId[hookId]));
                });
            Invoker = new DirectToolInvoker(
                request,
                new Dictionary<string, DirectToolBinding>(StringComparer.Ordinal) { [function.Name] = binding },
                turnHooks);
            var arguments = new AIFunctionArguments { ["value"] = "original" };
            _context = new FunctionInvocationContext
            {
                Function = function,
                Arguments = arguments,
                CallContent = new FunctionCallContent("call-1", function.Name, arguments)
            };
        }

        internal DirectToolInvoker Invoker { get; }

        internal DirectToolResult? ToolResult { get; private set; }

        internal int Executions { get; private set; }

        internal string? LastValue { get; private set; }

        internal ValueTask<object?> InvokeAsync()
            => Invoker.InvokeAsync(_context, CancellationToken.None);
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

    private sealed class RecordingApprovalHandler : IToolApprovalHandler
    {
        private readonly bool _approved;

        public RecordingApprovalHandler(bool approved)
        {
            _approved = approved;
        }

        public List<ToolApprovalRequest> Requests { get; } = [];

        public Task<bool> RequestApprovalAsync(ToolApprovalRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(_approved);
        }
    }
}
