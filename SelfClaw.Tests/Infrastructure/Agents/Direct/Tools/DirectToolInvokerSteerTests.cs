using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Desktop.Services.Tools;
using SelfClaw.Infrastructure.Agents.Direct.Hooks;
using SelfClaw.Infrastructure.Agents.Direct.Hooks.Models;
using SelfClaw.Infrastructure.Extensions.Plugins.Models;
using SelfClaw.Infrastructure.Agents.Direct.Tools;
using SelfClaw.Infrastructure.Agents.Direct.Tools.Models;
using SelfClaw.Tests.Infrastructure.Agents.Direct.Hooks.TestDoubles;

namespace SelfClaw.Tests.Infrastructure.Agents.Direct.Tools;

/// <summary>
/// P4.3–P4.5/P4.8: the invoker owns the only tool execution-admission state machine, a claimed
/// steer wins every phase before Executing, and a superseded call is a model-visible Canceled
/// result that neither fakes hooks nor resets the consecutive-fault budget.
/// </summary>
public sealed class DirectToolInvokerSteerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_steer_claimed_before_the_call_starts_skips_it_without_running_any_hook()
    {
        var recorder = new HookRecorder();
        var steering = new FakeSteering { HasSupersedingSteer = true };
        var executions = new ExecutionCounter();
        var pipeline = CreatePipeline(steering, recorder, executions);

        var result = await pipeline.InvokeAsync();

        var tool = result.Should().BeOfType<DirectToolResult>().Subject;
        tool.Status.Should().Be(ToolCallStatus.Canceled);
        tool.Detail.Should().Be(DirectToolInvoker.SupersededBySteerReason);
        executions.Count.Should().Be(0);
        recorder.Payloads.Should().BeEmpty("no hook ran for a call that never left Pending");
        pipeline.Invoker.TryTakeOutcome("call-1").Should().BeNull();
    }

    [Fact]
    public async Task A_steer_claim_during_the_pre_hook_finishes_the_hook_then_skips_approval_and_execution()
    {
        var recorder = new HookRecorder { GateExecuting = true };
        var steering = new FakeSteering();
        var handler = new DesktopToolApprovalHandler();
        var executions = new ExecutionCounter();
        var pipeline = CreatePipeline(steering, recorder, executions, approvalHandler: handler,
            requiresApproval: true);
        var invoke = pipeline.InvokeAsync();
        await recorder.Entered.Task.WaitAsync(Timeout);

        // The batch is claimed while the unavoidable pre-hook is still running.
        steering.Admit = false;
        steering.Pulse();
        recorder.Release.TrySetResult();

        var result = await invoke.AsTask().WaitAsync(Timeout);

        result.Should().BeOfType<DirectToolResult>().Which.Status.Should().Be(ToolCallStatus.Canceled);
        executions.Count.Should().Be(0);
        handler.GetPendingRequests().Should().BeEmpty("no approval was ever requested after the steer claim");
        var executed = recorder.Payloads.Single(payload => payload.GetProperty("event").GetString() == "toolExecuted");
        executed.GetProperty("status").GetString().Should().Be("canceled");
        executed.GetProperty("deniedBy").GetString().Should().Be("steer");
    }

    [Fact]
    public async Task A_steer_claim_while_approval_waits_revokes_the_pending_approval_and_a_late_click_fails()
    {
        var recorder = new HookRecorder();
        var steering = new FakeSteering();
        var handler = new DesktopToolApprovalHandler();
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.ApprovalRequested += _ => requested.TrySetResult();
        var executions = new ExecutionCounter();
        var pipeline = CreatePipeline(steering, recorder, executions, approvalHandler: handler,
            requiresApproval: true);
        var invoke = pipeline.InvokeAsync();
        await requested.Task.WaitAsync(Timeout);
        var approvalId = handler.GetPendingRequests().Single().ToolExecutionId;

        // Claim first, then revoke: the durable claim is what authorises the supersede.
        steering.Admit = false;
        steering.Pulse();
        var result = await invoke.AsTask().WaitAsync(Timeout);

        result.Should().BeOfType<DirectToolResult>().Which.Status.Should().Be(ToolCallStatus.Canceled);
        executions.Count.Should().Be(0);
        handler.TryResolve(approvalId, ToolApprovalDecision.Approved).Should().BeFalse("the late allow click is stale");
        handler.GetPendingRequests().Should().BeEmpty();
    }

    [Fact]
    public async Task A_failed_steer_claim_does_not_close_the_pending_approval()
    {
        var recorder = new HookRecorder();
        var steering = new FakeSteering();
        var handler = new DesktopToolApprovalHandler();
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.ApprovalRequested += _ => requested.TrySetResult();
        var executions = new ExecutionCounter();
        var pipeline = CreatePipeline(steering, recorder, executions, approvalHandler: handler,
            requiresApproval: true);
        var invoke = pipeline.InvokeAsync();
        await requested.Task.WaitAsync(Timeout);

        // The pulse wakes the approval wait, but the claim returns nothing: execute stays possible.
        steering.Pulse();
        handler.GetPendingRequests().Should().ContainSingle("an unconfirmed input may not revoke an approval");
        var approvalId = handler.GetPendingRequests().Single().ToolExecutionId;
        handler.TryResolve(approvalId, ToolApprovalDecision.Approved).Should().BeTrue();

        var result = await invoke.AsTask().WaitAsync(Timeout);
        result.Should().Be("ok");
        executions.Count.Should().Be(1);
    }

    [Fact]
    public async Task An_approved_call_still_loses_to_a_steer_claim_at_the_execution_admission()
    {
        var recorder = new HookRecorder();
        var steering = new FakeSteering { Admit = false };
        var handler = new RecordingApprovalHandler(ToolApprovalDecision.Approved);
        var executions = new ExecutionCounter();
        var pipeline = CreatePipeline(steering, recorder, executions, approvalHandler: handler,
            requiresApproval: true);

        var result = await pipeline.InvokeAsync();

        result.Should().BeOfType<DirectToolResult>().Which.Status.Should().Be(ToolCallStatus.Canceled);
        executions.Count.Should().Be(0);
        steering.AdmitCalls.Should().Be(1);
    }

    [Fact]
    public async Task A_call_already_executing_is_not_revoked_by_a_later_steer_claim()
    {
        var recorder = new HookRecorder();
        var steering = new FakeSteering();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executions = new ExecutionCounter();
        var pipeline = CreatePipeline(steering, recorder, executions, tool: async () =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(Timeout);
            executions.Increment();
            return "done";
        });
        var invoke = pipeline.InvokeAsync();
        await entered.Task.WaitAsync(Timeout);

        steering.HasSupersedingSteer = true;
        steering.Pulse();
        release.TrySetResult();

        var result = await invoke.AsTask().WaitAsync(Timeout);
        result.Should().Be("done");
        executions.Count.Should().Be(1);
    }

    [Fact]
    public async Task A_superseded_call_neither_resets_nor_increments_the_consecutive_fault_budget()
    {
        var recorder = new HookRecorder();
        var steering = new FakeSteering();
        var executions = new ExecutionCounter();
        var pipeline = CreatePipeline(steering, recorder, executions, tool: () => throw new InvalidOperationException("broken"));
        var invoke = (string callId) => pipeline.Invoker
            .InvokeAsync(new FunctionCallContent(callId, "probe_tool", new AIFunctionArguments()), 0, CancellationToken.None)
            .AsTask();

        var first = await invoke("call-1");
        first.Should().BeOfType<DirectToolResult>().Which.Status.Should().Be(ToolCallStatus.Failed);
        steering.Admit = false;
        var skipped = await invoke("call-2");
        skipped.Should().BeOfType<DirectToolResult>().Which.Status.Should().Be(ToolCallStatus.Canceled);
        steering.Admit = true;
        var second = await invoke("call-3");
        second.Should().BeOfType<DirectToolResult>().Which.Status.Should().Be(ToolCallStatus.Failed);
        await FluentActions.Awaiting(() => invoke("call-4"))
            .Should().ThrowAsync<DirectToolFaultLimitException>();
    }

    [Fact]
    public async Task Denied_approval_still_returns_the_user_canceled_result_without_executing()
    {
        var recorder = new HookRecorder();
        var steering = new FakeSteering();
        var handler = new RecordingApprovalHandler(ToolApprovalDecision.Denied);
        var executions = new ExecutionCounter();
        var pipeline = CreatePipeline(steering, recorder, executions, approvalHandler: handler,
            requiresApproval: true);

        var result = await pipeline.InvokeAsync();

        var tool = result.Should().BeOfType<DirectToolResult>().Subject;
        tool.Status.Should().Be(ToolCallStatus.Canceled);
        tool.Summary.Should().Be(DirectToolInvoker.DeniedResult);
        executions.Count.Should().Be(0);
        var executed = recorder.Payloads.Single(payload => payload.GetProperty("event").GetString() == "toolExecuted");
        executed.GetProperty("deniedBy").GetString().Should().Be("user");
    }

    private static TestPipeline CreatePipeline(FakeSteering steering, HookRecorder recorder, ExecutionCounter executions,
        Func<ValueTask<object?>>? tool = null, IToolApprovalHandler? approvalHandler = null, bool requiresApproval = false)
    {
        var function = AIFunctionFactory.Create(async () =>
        {
            if (tool is not null) return await tool();
            executions.Increment();
            return "ok";
        }, new AIFunctionFactoryOptions
        {
            Name = "probe_tool",
            Description = "Synthetic tool used to observe steering admission.",
            ExcludeResultSchema = true,
            MarshalResult = (value, _, _) => ValueTask.FromResult(value)
        });
        var binding = new DirectToolBinding(function, new DirectToolDescriptor("probe_tool", ToolCallKind.Other),
            requiresApproval);
        var hooks = HookTestFactory.Create(
            HookTestFactory.CreateContext(),
            [
                HookTestFactory.CreateHook("steer-audit", "before", PluginHookEvent.ToolExecuting),
                HookTestFactory.CreateHook("steer-audit", "after", PluginHookEvent.ToolExecuted)
            ],
            runner: recorder.RunAsync);
        var request = CreateRequest(approvalHandler) with { Steering = steering };
        var invoker = new DirectToolInvoker(request,
            new Dictionary<string, DirectToolBinding>(StringComparer.Ordinal) { ["probe_tool"] = binding }, hooks);
        return new TestPipeline(invoker);
    }

    private static DirectChatTurnRequest CreateRequest(IToolApprovalHandler? approvalHandler)
        => new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            null,
            new AgentRuntimeDefinition("direct-test", "Direct", "test", AgentExecutionMode.Direct,
                AgentRuntimeDefinition.SystemToolPolicy, [], [], [], [], "Follow project instructions."),
            [],
            [],
            null,
            ToolPermissionMode.RequireApproval,
            approvalHandler,
            new DirectTurnExecutionContext(DirectTurnOrigin.Interactive, null, null));

    internal sealed class TestPipeline(DirectToolInvoker invoker)
    {
        internal DirectToolInvoker Invoker { get; } = invoker;

        internal ValueTask<object?> InvokeAsync(CancellationToken cancellationToken = default)
            => Invoker.InvokeAsync(new FunctionCallContent("call-1", "probe_tool", new AIFunctionArguments()), 0, cancellationToken);
    }

    internal sealed class FakeSteering : IDirectTurnSteering
    {
        private readonly object _gate = new();
        private TaskCompletionSource _pulse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _signaled;

        public string? SteerBlockReason => null;
        public bool HasSupersedingSteer { get; set; }
        public bool Admit { get; set; } = true;
        public int AdmitCalls { get; private set; }

        public Task<bool> TryBeginToolExecutionAsync(CancellationToken cancellationToken = default)
        {
            lock (_gate) AdmitCalls++;
            return Task.FromResult(Admit);
        }

        public Task WaitForSteerPulseAsync(CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (_signaled)
                {
                    _signaled = false;
                    return Task.CompletedTask;
                }

                return _pulse.Task.WaitAsync(cancellationToken);
            }
        }

        public Task SetSteerPolicyAsync(string? blockReason, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        internal void Pulse()
        {
            TaskCompletionSource pulse;
            lock (_gate)
            {
                _signaled = true;
                pulse = _pulse;
                _pulse = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            pulse.TrySetResult();
        }
    }

    internal sealed class HookRecorder
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal List<JsonElement> Payloads { get; } = [];
        internal bool GateExecuting { get; init; }

        internal async Task<HookProcessResult> RunAsync(HookProcessStart start, ReadOnlyMemory<byte> input, TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var payload = JsonDocument.Parse(input).RootElement.Clone();
            lock (Payloads) Payloads.Add(payload);
            if (GateExecuting && payload.GetProperty("event").GetString() == "toolExecuting")
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }

            return HookTestFactory.Success("");
        }
    }

    internal sealed class ExecutionCounter
    {
        private int _count;
        internal int Count => Volatile.Read(ref _count);
        internal void Increment() => Interlocked.Increment(ref _count);
    }

    private sealed class RecordingApprovalHandler(ToolApprovalDecision decision) : IToolApprovalHandler
    {
        public Task<ToolApprovalDecision> RequestApprovalAsync(ToolApprovalRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(decision);

        public bool TryResolve(Guid toolExecutionId, ToolApprovalDecision decision) => false;
    }
}
