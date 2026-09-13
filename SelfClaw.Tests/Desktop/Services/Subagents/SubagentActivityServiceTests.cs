using System.Threading.Channels;
using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Desktop.Services.Subagents;
using SelfClaw.Desktop.Services.Subagents.Models;
using SelfClaw.Infrastructure.Agents.Subagents.Runtime;
using SelfClaw.Tests.TestDoubles;

namespace SelfClaw.Tests.Desktop.Services.Subagents;

public sealed class SubagentActivityServiceTests
{
    [Fact]
    public async Task Live_content_is_visible_before_terminal_and_token_updates_do_not_query_metadata()
    {
        using var context = new SubagentActivityTestContext();
        var task = await context.CreateTaskAsync();
        var runtime = new ControlledSubagentRuntime();
        var execution = context.CreateExecutor(task, runtime).ExecuteAsync(task, CancellationToken.None);
        await runtime.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var initial = await DetailAsync(context, task);
        initial.Content.Message.Should().NotBeNull();
        initial.ContentOrigin.Should().Be("live");
        await context.Service.ListAsync(new SubagentActivityQuery(task.ParentConversationId));
        var listReads = context.Reader.ListReads;
        var detailReads = context.Reader.DetailReads;
        await runtime.EmitAsync(new RunStartedEvent("child", "actual model", null));
        await runtime.EmitAsync(new AssistantThinkingDeltaEvent("thinking", "analysis \n "));
        await runtime.EmitAsync(new AssistantTextDeltaEvent("text", "answer"));
        await runtime.EmitAsync(new ToolCallStartedEvent("call-1", "read_file", "{}", ToolCallKind.Read, ToolSourceKind.BuiltIn));
        var beforeTerminal = await DetailAsync(context, task);
        var beforeMessage = beforeTerminal.Content.Message ?? throw new InvalidOperationException("Live content is missing.");
        beforeMessage.Segments?.Select(segment => segment.Kind).Should().Equal(
            MessageSegmentKind.Thinking, MessageSegmentKind.Text, MessageSegmentKind.ToolCall);
        beforeMessage.Segments?.First().Text.Should().Be("analysis \n ");
        beforeMessage.MarkdownContent.Should().Be("answer");
        beforeTerminal.Content.ToolRuns.Should().ContainSingle().Which.Status.Should().Be(ToolExecutionStatus.Running);
        beforeTerminal.Activity.Phase.Should().Be("tool");
        beforeTerminal.Activity.Task.ModelDisplayName.Should().Be("actual model");
        (await context.Conversations.ListMessagesAsync(task.ChildConversationId)).Should().NotContain(message => message.Role == MessageRole.Assistant);
        await runtime.EmitAsync(new ToolCallCompletedEvent("call-1", ToolCallStatus.Completed, "read", "result"));
        await runtime.EmitAsync(new UsageReportedEvent(21, 8));
        for (var index = 0; index < 10; index++)
        {
            await runtime.EmitAsync(new AssistantTextDeltaEvent("text", "!"));
            await context.Service.ListAsync(new SubagentActivityQuery(task.ParentConversationId));
            await DetailAsync(context, task);
        }

        context.Reader.ListReads.Should().Be(listReads);
        context.Reader.DetailReads.Should().Be(detailReads);
        var live = await DetailAsync(context, task);
        live.Activity.Task.InputTokens.Should().Be(21);
        live.Activity.Task.OutputTokens.Should().Be(8);
        beforeTerminal.Content.Message?.MarkdownContent.Should().Be("answer");
        await runtime.EmitAsync(new RunCompletedEvent(RunCompletionStatus.Succeeded, "answer!!!!!!!!!!"));
        await execution;
        var terminal = await DetailAsync(context, task);
        terminal.ContentOrigin.Should().Be("persisted");
        terminal.Activity.Task.Status.Should().Be(SubagentTaskStatus.Succeeded);
        terminal.Activity.Task.DeliveryStatus.Should().Be(SubagentDeliveryStatus.Pending);
        terminal.Content.Message?.MarkdownContent.Should().Be(live.Content.Message?.MarkdownContent);
        terminal.Content.ToolRuns.Should().ContainSingle().Which.Status.Should().Be(ToolExecutionStatus.Completed);
        context.Registry.GetActivity(task.Id).Should().BeNull();
        var staleContent = () => context.Service.ReadContentAsync(new SubagentContentQuery(task.ParentConversationId, task.Id,
            live.Content.ContentVersion, "segment/0"));
        await staleContent.Should().ThrowAsync<SubagentActivityReadException>().WithMessage("content-changed");
    }

    [Fact]
    public async Task Last_delta_is_published_while_provider_is_paused_and_faulty_observers_do_not_stop_execution()
    {
        using var context = new SubagentActivityTestContext();
        context.Changes.Changed += _ => throw new InvalidOperationException("Observer failure.");
        context.Changes.Changed += _ => throw new OperationCanceledException();
        context.Service.Changed += _ => throw new InvalidOperationException("UI observer failure.");
        var task = await context.CreateTaskAsync();
        var runtime = new ControlledSubagentRuntime();
        var execution = context.CreateExecutor(task, runtime).ExecuteAsync(task, CancellationToken.None);
        await runtime.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await runtime.EmitAsync(new AssistantThinkingDeltaEvent("thinking", "first"));
        var changes = Channel.CreateUnbounded<Guid>();
        context.Service.Changed += parent => changes.Writer.TryWrite(parent);
        await runtime.EmitAsync(new AssistantThinkingDeltaEvent("thinking", " last"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (true)
        {
            await changes.Reader.ReadAsync(deadline.Token);
            var snapshot = await DetailAsync(context, task);
            if (snapshot.Content.Message?.Segments?.Any(segment => segment.Text == "first last") == true)
            {
                break;
            }
        }
        execution.IsCompleted.Should().BeFalse();
        await runtime.EmitAsync(new RunCompletedEvent(RunCompletionStatus.Succeeded, "done"));
        await execution;
        (await DetailAsync(context, task)).Activity.Task.Status.Should().Be(SubagentTaskStatus.Succeeded);
    }

    [Fact]
    public async Task Three_concurrent_children_keep_identical_call_ids_and_content_isolated()
    {
        using var context = new SubagentActivityTestContext();
        var first = await context.CreateTaskAsync();
        var parent = await context.Conversations.GetConversationAsync(first.ParentConversationId)
            ?? throw new InvalidOperationException("Missing parent.");
        var tasks = new[] { first, await context.CreateTaskAsync(parent), await context.CreateTaskAsync(parent) };
        var runtimes = tasks.Select(_ => new ControlledSubagentRuntime()).ToArray();
        var executions = tasks.Select((task, index) => context.CreateExecutor(task, runtimes[index]).ExecuteAsync(task, CancellationToken.None)).ToArray();
        await Task.WhenAll(runtimes.Select(runtime => runtime.Started.Task));
        await Task.WhenAll(runtimes.Select((runtime, index) => runtime.EmitAsync(new AssistantTextDeltaEvent("text", $"child {index}"))));
        await Task.WhenAll(runtimes.Select(runtime => runtime.EmitAsync(new ToolCallStartedEvent("same-call", "read_file", "{}", ToolCallKind.Read, ToolSourceKind.BuiltIn))));
        var snapshots = await Task.WhenAll(tasks.Select(task => DetailAsync(context, task)));
        snapshots.Select(snapshot => snapshot.Content.ToolRuns.Single().Id).Should().OnlyHaveUniqueItems();
        snapshots.Select(snapshot => snapshot.Content.Message?.MarkdownContent).Should().Equal("child 0", "child 1", "child 2");
        (await context.Service.ListAsync(new SubagentActivityQuery(parent.Id))).Counts.Running.Should().Be(3);
        await Task.WhenAll(runtimes.Select((runtime, index) => runtime.EmitAsync(new RunCompletedEvent(RunCompletionStatus.Succeeded, $"child {index}"))));
        await Task.WhenAll(executions);
        (await context.Service.ListAsync(new SubagentActivityQuery(parent.Id))).Counts.Succeeded.Should().Be(3);
    }

    [Fact]
    public async Task Terminal_commit_during_a_read_discards_the_older_running_snapshot()
    {
        using var context = new SubagentActivityTestContext();
        var task = await context.CreateTaskAsync();
        var runtime = new ControlledSubagentRuntime();
        var execution = context.CreateExecutor(task, runtime).ExecuteAsync(task, CancellationToken.None);
        await runtime.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var captured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Reader.AfterDetailReadAsync = async () =>
        {
            context.Reader.AfterDetailReadAsync = null;
            captured.SetResult();
            await release.Task;
        };
        var read = DetailAsync(context, task);
        await captured.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await runtime.EmitAsync(new RunCompletedEvent(RunCompletionStatus.Succeeded, "committed"));
        await execution;
        release.SetResult();
        var snapshot = await read;
        snapshot.ContentOrigin.Should().Be("persisted");
        snapshot.Activity.Task.Status.Should().Be(SubagentTaskStatus.Succeeded);
        snapshot.Content.Message?.MarkdownContent.Should().Be("committed");
        (await DetailAsync(context, task)).Activity.Task.Status.Should().Be(SubagentTaskStatus.Succeeded);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Approval_state_is_reconstructable_and_clears_on_resolution_or_timeout(bool expire)
    {
        using var context = new SubagentActivityTestContext(expire ? TimeSpan.FromMilliseconds(250) : null);
        var task = await context.CreateTaskAsync();
        var runtime = new ControlledSubagentRuntime();
        var execution = context.CreateExecutor(task, runtime).ExecuteAsync(task, CancellationToken.None);
        await runtime.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var request = new ToolApprovalRequest(Guid.NewGuid(), "write_file", "Write", "write", "{}", task.ChildConversationId);
        var approval = context.Approvals.RequestApprovalAsync(request);
        var waiting = await context.Service.GetDetailAsync(task.ParentConversationId, task.Id, refresh: true)
            ?? throw new InvalidOperationException("Missing waiting task.");
        waiting.Activity.Phase.Should().Be("waiting-approval");
        waiting.Activity.PendingApprovalCount.Should().Be(1);
        waiting.Content.ToolRuns.Should().BeEmpty();
        if (!expire)
        {
            context.Approvals.TryResolve(request.ToolExecutionId, approved: true).Should().BeTrue();
        }

        (await approval.WaitAsync(TimeSpan.FromSeconds(3))).Should().Be(!expire);
        var resumed = await DetailAsync(context, task);
        resumed.Activity.PendingApprovalCount.Should().Be(0);
        resumed.Activity.Phase.Should().NotBe("waiting-approval");
        await runtime.EmitAsync(new RunCompletedEvent(RunCompletionStatus.Succeeded, "done"));
        await execution;
    }

    [Fact]
    public async Task Closing_scope_rejects_reads_and_reopening_after_delete_timeout_reloads_without_cancelling_child()
    {
        using var context = new SubagentActivityTestContext();
        var task = await context.CreateTaskAsync();
        var runtime = new ControlledSubagentRuntime();
        var execution = context.CreateExecutor(task, runtime).ExecuteAsync(task, CancellationToken.None);
        await runtime.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await DetailAsync(context, task);
        var before = context.Reader.DetailReads;
        context.Service.SetScopeClosed(task.ParentConversationId, true);
        var read = () => DetailAsync(context, task);
        await read.Should().ThrowAsync<SubagentActivityReadException>().WithMessage("activity-scope-closed");
        execution.IsCompleted.Should().BeFalse();
        await runtime.EmitAsync(new AssistantTextDeltaEvent("text", "continued"));
        context.Service.SetScopeClosed(task.ParentConversationId, false);
        (await DetailAsync(context, task)).Content.Message?.MarkdownContent.Should().Be("continued");
        context.Reader.DetailReads.Should().BeGreaterThan(before);
        await runtime.EmitAsync(new RunCompletedEvent(RunCompletionStatus.Succeeded, "continued"));
        await execution;
    }

    [Fact]
    public async Task Persistence_failure_is_reported_separately_from_the_uncommitted_terminal_message()
    {
        using var context = new SubagentActivityTestContext();
        var task = await context.CreateTaskAsync();
        context.Store.BeforeCompleteAsync = () => throw new IOException("Terminal write unavailable.");
        var runtime = new ControlledSubagentRuntime();
        var execution = context.CreateExecutor(task, runtime).ExecuteAsync(task, CancellationToken.None);
        await runtime.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await runtime.EmitAsync(new AssistantTextDeltaEvent("text", "partial"));
        await runtime.EmitAsync(new RunCompletedEvent(RunCompletionStatus.Succeeded, "partial"));
        var awaitExecution = () => execution;
        await awaitExecution.Should().ThrowAsync<IOException>();
        var snapshot = await DetailAsync(context, task);
        snapshot.Activity.Task.Status.Should().Be(SubagentTaskStatus.Running);
        snapshot.Activity.Phase.Should().Be("recording-error");
        snapshot.Activity.RecordingError.Should().Contain("Terminal write unavailable");
        snapshot.Content.Message.Should().BeNull();
        context.Registry.GetActivity(task.Id).Should().BeNull();
    }

    private static async Task<SubagentActivitySnapshot> DetailAsync(SubagentActivityTestContext context, SubagentTaskRecord task)
        => await context.Service.GetDetailAsync(task.ParentConversationId, task.Id) ?? throw new InvalidOperationException("Missing activity detail.");
}
