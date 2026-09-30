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
    public async Task A_faulting_tool_returns_a_failed_result_with_the_real_error_and_hook_feedback()
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

        var result = await fixture.InvokeAsync();

        var failed = result.Should().BeOfType<DirectToolResult>().Subject;
        failed.Status.Should().Be(ToolCallStatus.Failed);
        failed.Summary.Should().Be("tool exploded");
        failed.Detail.Should().Be("tool exploded", "a fault's display content must not carry a stack trace into later turns");
        failed.Content.GetProperty("message").GetString().Should().Be("tool exploded");
        failed.Content.GetProperty("type").GetString().Should().Be("InvalidOperationException");
        failed.HookFeedback.Should().HaveCount(2);
        failed.HookFeedback![0].Source.Should().Be("selfclaw");
        failed.HookFeedback[1].Text.Should().Be("note");
        fixture.Executions.Should().Be(1);

        var outcome = fixture.Invoker.TryTakeOutcome("call-1");
        outcome.Should().NotBeNull();
        outcome!.EffectiveArgumentsJson.Should().Contain("changed");
        outcome.Feedback.Should().ContainSingle().Which.Text.Should().Be("note");
    }

    [Fact]
    public async Task Consecutive_faults_are_converted_until_the_budget_is_spent()
    {
        var fixture = new Fixture(
            ToolPermissionMode.FullAccess,
            approvalHandler: null,
            checkpoint: null,
            hooks: [],
            throws: true);

        for (var attempt = 1; attempt <= DirectToolInvoker.MaximumConsecutiveToolFaults; attempt++)
        {
            var converted = await fixture.InvokeAsync($"call-{attempt}");

            converted.Should().BeOfType<DirectToolResult>().Which.Status.Should().Be(ToolCallStatus.Failed);
        }

        var action = () => fixture
            .InvokeAsync($"call-{DirectToolInvoker.MaximumConsecutiveToolFaults + 1}")
            .AsTask();

        await action.Should().ThrowAsync<DirectToolFaultLimitException>();
        fixture.Executions.Should().Be(DirectToolInvoker.MaximumConsecutiveToolFaults + 1);
    }

    [Fact]
    public async Task A_call_without_a_fault_resets_the_consecutive_budget()
    {
        var fixture = new Fixture(
            ToolPermissionMode.FullAccess,
            approvalHandler: null,
            checkpoint: null,
            hooks: [],
            throws: true);

        await fixture.InvokeAsync("call-1");
        fixture.Explode = false;
        await fixture.InvokeAsync("call-2");
        fixture.Explode = true;

        var result = await fixture.InvokeAsync("call-3");

        result.Should().BeOfType<DirectToolResult>().Which.Status.Should().Be(ToolCallStatus.Failed);
        fixture.Executions.Should().Be(3);
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
        private readonly AIFunction _function;
        private readonly AIFunctionArguments _arguments;

        internal Fixture(
            ToolPermissionMode permissionMode,
            IToolApprovalHandler? approvalHandler,
            IToolExecutionCheckpoint? checkpoint,
            IReadOnlyList<HookSpec> hooks,
            bool requiresApproval = false,
            bool throws = false)
        {
            Explode = throws;
            var function = AIFunctionFactory.Create(
                (string value) =>
                {
                    LastValue = value;
                    Executions++;
                    if (Explode)
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
            _function = function;
            _arguments = arguments;
        }

        internal DirectToolInvoker Invoker { get; }

        internal bool Explode { get; set; }

        internal DirectToolResult? ToolResult { get; private set; }

        internal int Executions { get; private set; }

        internal string? LastValue { get; private set; }

        internal ValueTask<object?> InvokeAsync(string callId = "call-1")
            => Invoker.InvokeAsync(
                new FunctionInvocationContext
                {
                    Function = _function,
                    Arguments = _arguments,
                    CallContent = new FunctionCallContent(callId, _function.Name, _arguments)
                },
                CancellationToken.None);
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
