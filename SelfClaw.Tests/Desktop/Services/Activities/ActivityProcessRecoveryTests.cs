using System.Diagnostics;
using System.Windows;
using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Tests.TestDoubles;
using Xunit.Abstractions;

namespace SelfClaw.Tests.Desktop.Services.Activities;

public sealed class ActivityProcessRecoveryTests(ITestOutputHelper output)
{
    [DesktopSmokeFact]
    public Task Forced_process_exit_recovers_only_persisted_history_as_interrupted() => WpfDispatcherTest.RunAsync(async () =>
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../SelfClaw.Tests/TestResults/activity-crash", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        await TerminateFixtureAsync(root);
        using var activity = new SubagentActivityTestContext(rootPath: root);
        var task = (await activity.Tasks.ListByStatusAsync(SubagentTaskStatus.Running)).Should().ContainSingle().Subject;
        var runtime = new ControlledSubagentRuntime();
        await activity.CreateExecutor(task, runtime).RecoverInterruptedAsync(task, CancellationToken.None);
        runtime.Started.Task.IsCompleted.Should().BeFalse();
        using var panel = new ActivityPanelTestContext(activity, task.ParentConversationId);
        var subscription = Guid.NewGuid();
        await panel.Publisher.SubscribeAsync(subscription, task.ParentConversationId, "restart", CancellationToken.None);
        panel.AcknowledgeLatest();
        await panel.Publisher.SelectDetailAsync(subscription, Guid.NewGuid(), task.Id, null, null, "detail", CancellationToken.None);
        var section = panel.LatestState.GetProperty("sections")[0];
        section.GetProperty("counts").GetProperty("interrupted").GetInt32().Should().Be(1);
        var detail = section.GetProperty("detail");
        detail.GetProperty("historyCompleteness").GetString().Should().Be("partial");
        detail.GetProperty("unplacedTools").GetArrayLength().Should().Be(1);
        detail.ToString().Should().NotContain("uncommitted text").And.NotContain("uncommitted thinking");
        output.WriteLine("Parent={0}; Task={1}; Recovery=Interrupted; History=Partial; Artifacts={2}", task.ParentConversationId, task.Id, root);
    }, 60);

    [ActivityCrashFixtureFact]
    public Task Child_process_waits_with_uncommitted_text_and_recorded_tool() => WpfDispatcherTest.RunAsync(async () =>
    {
        var root = Environment.GetEnvironmentVariable("SELFCLAW_ACTIVITY_CRASH_ROOT") ?? throw new InvalidOperationException();
        using var activity = new SubagentActivityTestContext(rootPath: root);
        var task = await activity.CreateTaskAsync();
        var runtime = new ControlledSubagentRuntime();
        var execution = activity.CreateExecutor(task, runtime).ExecuteAsync(task, CancellationToken.None);
        await runtime.Started.Task;
        var window = new Window { Width = 320, Height = 180, ShowInTaskbar = false, ShowActivated = false, Title = "SelfClaw recovery fixture" };
        window.Show();
        await runtime.EmitAsync(new AssistantThinkingDeltaEvent("thinking", "uncommitted thinking"));
        await runtime.EmitAsync(new AssistantTextDeltaEvent("text", "uncommitted text"));
        await runtime.EmitAsync(new ToolCallStartedEvent("call", "read_file", "{}", ToolCallKind.Read, ToolSourceKind.BuiltIn));
        await File.WriteAllTextAsync(Path.Combine(root, "ready"), $"{task.ParentConversationId:D}\n{task.Id:D}");
        await execution;
    }, 120);

    private static async Task TerminateFixtureAsync(string root)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("vstest");
        start.ArgumentList.Add(typeof(ActivityProcessRecoveryTests).Assembly.Location);
        start.ArgumentList.Add("/Tests:Child_process_waits_with_uncommitted_text_and_recorded_tool");
        start.Environment["SELFCLAW_ACTIVITY_CRASH_ROOT"] = root;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start recovery fixture.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (!File.Exists(Path.Combine(root, "ready")))
            {
                if (process.HasExited) throw new InvalidOperationException($"Recovery fixture exited: {await stdout} {await stderr}");
                await Task.Delay(50, timeout.Token);
            }
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
        }
    }
}
