using System.Diagnostics;
using System.Text;
using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Tests.TestDoubles;
using Xunit.Abstractions;

namespace SelfClaw.Tests.Desktop.Services.Activities;

public sealed class ActivityPanelPerformanceTests(ITestOutputHelper output)
{
    [Fact]
    public Task Thousand_history_tasks_and_four_live_children_have_bounded_payload_and_latency() => WpfDispatcherTest.RunAsync(async () =>
    {
        using var activity = new SubagentActivityTestContext();
        var first = await activity.CreateTaskAsync();
        var parent = await activity.Conversations.GetConversationAsync(first.ParentConversationId) ?? throw new InvalidOperationException();
        var tasks = new[] { first, await activity.CreateTaskAsync(parent), await activity.CreateTaskAsync(parent), await activity.CreateTaskAsync() };
        for (var index = 0; index < 1000; index++)
        {
            var historical = await activity.CreateTaskAsync(parent, claim: false);
            var now = DateTimeOffset.UtcNow;
            var message = new MessageRecord(historical.ChildTurnId, historical.ChildConversationId, MessageRole.Assistant,
                "History result", MessageStatus.Completed, now, now,
                Segments: [new MessageSegmentRecord(historical.ChildTurnId, 0, MessageSegmentKind.Text, "History result", null)]);
            await activity.Tasks.TryCompleteAsync(historical.Id, SubagentTaskStatus.Queued,
                new SubagentTaskCompletion(SubagentTaskStatus.Succeeded, new TurnFinalization(message, []), "History result", null, null, now));
        }
        var runtimes = tasks.Select(_ => new ControlledSubagentRuntime()).ToArray();
        var executions = tasks.Select((task, index) => activity.CreateExecutor(task, runtimes[index]).ExecuteAsync(task, CancellationToken.None)).ToArray();
        await Task.WhenAll(runtimes.Select(runtime => runtime.Started.Task));
        try
        {
            await MeasureAsync(activity, tasks, runtimes);
        }
        finally
        {
            foreach (var runtime in runtimes) await runtime.EmitAsync(new RunCompletedEvent(RunCompletionStatus.Succeeded, "done"));
            await Task.WhenAll(executions);
        }
    }, 120);

    private async Task MeasureAsync(SubagentActivityTestContext activity, SubagentTaskRecord[] tasks, ControlledSubagentRuntime[] runtimes)
    {
        var initialMemory = GC.GetTotalMemory(true);
        using var panel = new ActivityPanelTestContext(activity, tasks[0].ParentConversationId);
        var timer = Stopwatch.StartNew();
        var subscription = Guid.NewGuid();
        await panel.Publisher.SubscribeAsync(subscription, tasks[0].ParentConversationId, "initial", CancellationToken.None);
        var firstFrameMs = timer.Elapsed.TotalMilliseconds;
        panel.LatestState.GetProperty("sections")[0].GetProperty("counts").GetProperty("total").GetInt32().Should().Be(1003);
        panel.AcknowledgeLatest();
        await panel.Publisher.SelectDetailAsync(subscription, Guid.NewGuid(), tasks[0].Id, null, null, "detail", CancellationToken.None);
        panel.AcknowledgeLatest();
        foreach (var runtime in runtimes)
        {
            await runtime.EmitAsync(new ToolCallStartedEvent("same", "read_file", "{}", ToolCallKind.Read, ToolSourceKind.BuiltIn));
            await runtime.EmitAsync(new ToolCallCompletedEvent("same", ToolCallStatus.Completed, "read", new string('\u4e2d', 60000)));
        }
        var durations = new List<double>();
        for (var sample = 0; sample < 30; sample++)
        {
            var marker = $"sample-{sample:D3}";
            timer.Restart();
            foreach (var runtime in runtimes) await runtime.EmitAsync(new AssistantTextDeltaEvent("text", marker));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!panel.LatestState.ToString().Contains(marker, StringComparison.Ordinal))
            {
                panel.AcknowledgeLatest();
                await Task.Delay(5, timeout.Token);
            }
            durations.Add(timer.Elapsed.TotalMilliseconds);
            panel.AcknowledgeLatest();
        }
        var maximumBytes = panel.Messages.Where(message => message.GetProperty("type").GetString() == "activity-panel/state")
            .Max(message => Encoding.UTF8.GetByteCount(message.GetRawText()));
        var extraMemory = GC.GetTotalMemory(true) - initialMemory;
        var p95 = durations.Order().ElementAt((int)Math.Ceiling(durations.Count * .95) - 1);
        output.WriteLine("Tasks=1003; Live=3+1; Samples={0}; FirstFrameMs={1:F1}; TextP95Ms={2:F1}; MaxPayloadBytes={3}; ExtraManagedBytes={4}; Parent={5}",
            durations.Count, firstFrameMs, p95, maximumBytes, extraMemory, tasks[0].ParentConversationId);
        maximumBytes.Should().BeLessThanOrEqualTo(256 * 1024);
        p95.Should().BeLessThan(500);
        var seen = new HashSet<Guid>();
        string? cursor = null;
        do
        {
            var page = await activity.Service.ListAsync(new SubagentActivityQuery(tasks[0].ParentConversationId, Cursor: cursor));
            foreach (var task in page.Page.Tasks) seen.Add(task.TaskId).Should().BeTrue();
            cursor = page.Page.NextCursor;
        } while (cursor is not null);
        seen.Should().HaveCount(1003);
    }
}
