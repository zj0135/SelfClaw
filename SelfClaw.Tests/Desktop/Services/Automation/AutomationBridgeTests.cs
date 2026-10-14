using System.Text.Json;
using System.Windows.Threading;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Desktop.Services.Automation;
using SelfClaw.Desktop.Services.Settings;
using SelfClaw.Infrastructure.Automation;
using SelfClaw.Infrastructure.Data.Sqlite;
using SelfClaw.Infrastructure.Data.Sqlite.Repositories;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Tests.Desktop.Services.Automation;

public sealed class AutomationBridgeTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task List_calendar_projects_due_todos_and_finished_runs()
    {
        var harness = await CreateHarnessAsync();
        var now = DateTimeOffset.UtcNow;
        var daily = CreateTask(AutomationTaskKind.Daily, new AutomationScheduleSpec(Time: "09:00"), "Daily review");
        var todo = CreateTask(AutomationTaskKind.None, null, "Ship the report") with
        {
            Prompt = null,
            DueAtUtc = now.AddHours(2)
        };
        await harness.TaskStore.SaveAsync(daily);
        await harness.TaskStore.SaveAsync(todo);
        await harness.RunStore.TryInsertAsync(CreateRun(daily.Id, AutomationRunStatus.Completed, now.AddMinutes(-3)), false);

        var response = await InvokeAsync(harness.Bridge, "automation/list-calendar",
            $$"""{"fromUtc":"{{now.AddDays(-1):O}}","toUtc":"{{now.AddDays(1):O}}"}""");

        response.GetProperty("ok").GetBoolean().Should().BeTrue();
        response.GetProperty("truncated").GetBoolean().Should().BeFalse();
        var kinds = response.GetProperty("events").EnumerateArray()
            .Select(element => element.GetProperty("kind").GetString()).ToArray();
        kinds.Should().Contain("projected").And.Contain("todo").And.Contain("run");
        response.GetProperty("events").EnumerateArray()
            .First(element => element.GetProperty("kind").GetString() == "run")
            .GetProperty("status").GetString().Should().Be("completed");
    }

    [Fact]
    public async Task Save_task_rejects_a_missing_title_a_trigger_without_prompt_and_a_short_interval()
    {
        var harness = await CreateHarnessAsync();

        var noTitle = await InvokeAsync(harness.Bridge, "automation/save-task", """{"task":{"title":"  ","kind":"none"}}""");
        noTitle.GetProperty("ok").GetBoolean().Should().BeFalse();
        noTitle.GetProperty("errorCode").GetString().Should().Be(AutomationErrorCodes.InvalidTitle);

        var noPrompt = await InvokeAsync(harness.Bridge, "automation/save-task",
            """{"task":{"title":"Daily","kind":"daily","schedule":{"version":1,"time":"09:00"},"prompt":null}}""");
        noPrompt.GetProperty("ok").GetBoolean().Should().BeFalse();
        noPrompt.GetProperty("errorCode").GetString().Should().Be(AutomationErrorCodes.TriggerRequiresPrompt);

        var tooShort = await InvokeAsync(harness.Bridge, "automation/save-task",
            """{"task":{"title":"Every minute","kind":"cron","schedule":{"version":1,"cron":"* * * * *"},"prompt":"run"}}""");
        tooShort.GetProperty("ok").GetBoolean().Should().BeFalse();
        tooShort.GetProperty("errorCode").GetString().Should().Be(AutomationErrorCodes.IntervalTooShort);
    }

    [Fact]
    public async Task Save_task_persists_a_recurring_task_and_computes_the_next_trigger()
    {
        var harness = await CreateHarnessAsync();

        var response = await InvokeAsync(harness.Bridge, "automation/save-task",
            """{"task":{"title":"Daily","kind":"daily","schedule":{"version":1,"time":"09:00"},"prompt":"run","isEnabled":true}}""");

        response.GetProperty("ok").GetBoolean().Should().BeTrue();
        var taskId = Guid.Parse(response.GetProperty("task").GetProperty("id").GetString()!);
        response.GetProperty("nextRunAtUtc").GetString().Should().NotBeNullOrEmpty();
        var persisted = await harness.TaskStore.GetAsync(taskId);
        persisted!.ToolPermissionMode.Should().Be(ToolPermissionMode.FullAccess);
        persisted.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task Preview_schedule_reports_the_host_interval_rule()
    {
        var harness = await CreateHarnessAsync();

        var invalid = await InvokeAsync(harness.Bridge, "automation/preview-schedule",
            """{"kind":"cron","schedule":{"version":1,"cron":"*/1 * * * *"}}""");
        invalid.GetProperty("ok").GetBoolean().Should().BeFalse();
        invalid.GetProperty("errorCode").GetString().Should().Be(AutomationErrorCodes.IntervalTooShort);

        var valid = await InvokeAsync(harness.Bridge, "automation/preview-schedule",
            """{"kind":"daily","schedule":{"version":1,"time":"09:00"}}""");
        valid.GetProperty("ok").GetBoolean().Should().BeTrue();
        valid.GetProperty("nextRunAtUtc").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Run_task_now_reports_a_conflict_when_the_task_already_has_an_active_run()
    {
        var harness = await CreateHarnessAsync();
        var now = DateTimeOffset.UtcNow;
        var task = CreateTask(AutomationTaskKind.Daily, new AutomationScheduleSpec(Time: "09:00"), "Daily review");
        await harness.TaskStore.SaveAsync(task);
        await harness.RunStore.TryInsertAsync(CreateRun(task.Id, AutomationRunStatus.Running, now.AddMinutes(-1)), false);

        var response = await InvokeAsync(harness.Bridge, "automation/run-task-now",
            $$"""{"taskId":"{{task.Id:D}}"}""");

        response.GetProperty("ok").GetBoolean().Should().BeFalse();
        response.GetProperty("errorCode").GetString().Should().Be(AutomationErrorCodes.RunConflict);
    }

    [Fact]
    public async Task List_tasks_surfaces_per_task_and_total_history_bytes()
    {
        var harness = await CreateHarnessAsync();
        var task = CreateTask(AutomationTaskKind.Daily, new AutomationScheduleSpec(Time: "09:00"), "Daily review");
        await harness.TaskStore.SaveAsync(task);
        var terminal = await SeedTerminalRunAsync(harness, task, "1234567890");

        var response = await InvokeAsync(harness.Bridge, "automation/list-tasks", "{}");

        response.GetProperty("historyBytes").GetInt64().Should().Be(terminal.PromptLength);
        var taskJson = response.GetProperty("tasks").EnumerateArray().Single();
        taskJson.GetProperty("historyBytes").GetInt64().Should().Be(terminal.PromptLength);
    }

    [Fact]
    public async Task Delete_run_clear_task_history_and_clear_history_report_the_deleted_count()
    {
        var harness = await CreateHarnessAsync();
        var task = CreateTask(AutomationTaskKind.Daily, new AutomationScheduleSpec(Time: "09:00"), "Daily review");
        var other = CreateTask(AutomationTaskKind.Daily, new AutomationScheduleSpec(Time: "10:00"), "Other review");
        await harness.TaskStore.SaveAsync(task);
        await harness.TaskStore.SaveAsync(other);
        var first = await SeedTerminalRunAsync(harness, task, "first");
        await SeedTerminalRunAsync(harness, task, "second");
        await SeedTerminalRunAsync(harness, other, "third");

        var single = await InvokeAsync(harness.Bridge, "automation/delete-run", $$"""{"runId":"{{first.RunId:D}}"}""");
        single.GetProperty("ok").GetBoolean().Should().BeTrue();
        single.GetProperty("deleted").GetInt32().Should().Be(1);

        var taskHistory = await InvokeAsync(harness.Bridge, "automation/clear-task-history", $$"""{"taskId":"{{task.Id:D}}"}""");
        taskHistory.GetProperty("ok").GetBoolean().Should().BeTrue();
        taskHistory.GetProperty("deleted").GetInt32().Should().Be(1);
        (await harness.TaskStore.GetAsync(task.Id)).Should().NotBeNull();

        var all = await InvokeAsync(harness.Bridge, "automation/clear-history", "{}");
        all.GetProperty("ok").GetBoolean().Should().BeTrue();
        all.GetProperty("deleted").GetInt32().Should().Be(1);
        (await harness.RunStore.ListAsync(null, 10)).Should().BeEmpty();
    }

    [Fact]
    public async Task Delete_run_rejects_an_active_run_and_delete_task_removes_the_definition()
    {
        var harness = await CreateHarnessAsync();
        var task = CreateTask(AutomationTaskKind.Daily, new AutomationScheduleSpec(Time: "09:00"), "Daily review");
        await harness.TaskStore.SaveAsync(task);
        var active = CreateRun(task.Id, AutomationRunStatus.Running, DateTimeOffset.UtcNow.AddMinutes(-1));
        await harness.RunStore.TryInsertAsync(active, skipWhenTaskActive: false);

        var conflict = await InvokeAsync(harness.Bridge, "automation/delete-run", $$"""{"runId":"{{active.Id:D}}"}""");
        conflict.GetProperty("ok").GetBoolean().Should().BeFalse();
        conflict.GetProperty("errorCode").GetString().Should().Be(AutomationErrorCodes.RunConflict);

        var deletable = CreateTask(AutomationTaskKind.Daily, new AutomationScheduleSpec(Time: "10:00"), "Deletable");
        await harness.TaskStore.SaveAsync(deletable);
        await SeedTerminalRunAsync(harness, deletable, "terminal");
        var deleted = await InvokeAsync(harness.Bridge, "automation/delete-task", $$"""{"taskId":"{{deletable.Id:D}}"}""");
        deleted.GetProperty("ok").GetBoolean().Should().BeTrue();
        (await harness.TaskStore.GetAsync(deletable.Id)).Should().BeNull();
        (await harness.TaskStore.GetAsync(task.Id)).Should().NotBeNull();
        (await harness.RunStore.GetAsync(active.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task Get_run_returns_the_transcript_items_projected_for_the_automation_conversation()
    {
        var harness = await CreateHarnessAsync();
        var now = DateTimeOffset.UtcNow;
        var task = CreateTask(AutomationTaskKind.Daily, new AutomationScheduleSpec(Time: "09:00"), "Daily review");
        await harness.TaskStore.SaveAsync(task);

        var conversation = new ConversationRecord(Guid.NewGuid(), task.Title, null, ConversationMode.Programming,
            ToolPermissionMode.FullAccess, $"automation:{task.Id:D}", now, now, Kind: ConversationKind.Automation);
        var turn = new ConversationTurnRecord(Guid.NewGuid(), conversation.Id, AgentExecutionMode.Direct,
            DirectTurnOrigin.Automation, ConversationTurnStatus.Running, now);
        await harness.Turns.StartTurnAsync(new ConversationTurnStart(conversation, turn, "review the nightly diff", Guid.NewGuid()));
        var run = CreateRun(task.Id, AutomationRunStatus.Completed, now.AddMinutes(-1)) with { ConversationId = conversation.Id };
        await harness.RunStore.TryInsertAsync(run, false);

        var response = await InvokeAsync(harness.Bridge, "automation/get-run", $$"""{"runId":"{{run.Id:D}}"}""");

        response.GetProperty("ok").GetBoolean().Should().BeTrue();
        response.GetProperty("run").GetProperty("status").GetString().Should().Be("completed");
        var items = response.GetProperty("transcript").GetProperty("items").EnumerateArray().ToArray();
        items.Should().NotBeEmpty();
        items.Should().Contain(item => item.GetProperty("role").GetString() == "user");
    }

    [Fact]
    public async Task Settings_round_trip_through_the_desktop_settings_node()
    {
        var harness = await CreateHarnessAsync();

        var initial = await InvokeAsync(harness.Bridge, "automation/get-settings", "{}");
        initial.GetProperty("settings").GetProperty("notifyOnSuccess").GetBoolean().Should().BeFalse();

        var updated = await InvokeAsync(harness.Bridge, "automation/set-settings", """{"notifyOnSuccess":true}""");
        updated.GetProperty("settings").GetProperty("notifyOnSuccess").GetBoolean().Should().BeTrue();

        var reloaded = await InvokeAsync(harness.Bridge, "automation/get-settings", "{}");
        reloaded.GetProperty("settings").GetProperty("notifyOnSuccess").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Task_mutations_notify_the_shell_so_the_automation_view_can_refetch()
    {
        var harness = await CreateHarnessAsync();
        var reasons = new List<string>();
        harness.Notifier.Changed += (reason, _, _) => reasons.Add(reason);

        await InvokeAsync(harness.Bridge, "automation/save-task",
            """{"task":{"title":"Daily","kind":"daily","schedule":{"version":1,"time":"09:00"},"prompt":"run"}}""");

        reasons.Should().Contain("task-saved");
    }

    public void Dispose()
    {
        if (!Directory.Exists(_rootPath)) return;
        try
        {
            Directory.Delete(_rootPath, true);
        }
        catch (IOException)
        {
        }
    }

    private async Task<Harness> CreateHarnessAsync()
    {
        var paths = StoragePathDefaults.Create(_rootPath, Path.Combine(_rootPath, "selfclaw.db"), Path.Combine(_rootPath, "secrets"));
        var database = new SqliteDatabase(paths);
        await database.EnsureInitializedAsync();
        var taskStore = new SqliteAutomationTaskRepository(database);
        var runStore = new SqliteAutomationRunRepository(database);
        var notifier = new AutomationChangeNotifier();
        var calculator = new AutomationScheduleCalculator(TimeZoneInfo.Local);
        var cancellations = new AutomationRunCancellationRegistry();
        var runNotifier = new RecordingRunNotifier();
        var host = new AutomationSchedulerHost(
            taskStore,
            runStore,
            calculator,
            new NoopRecovery(),
            new CompletingExecutor(runStore),
            new NoopRegistrar(),
            cancellations,
            notifier,
            runNotifier,
            TimeProvider.System,
            NullLogger<AutomationSchedulerHost>.Instance);
        await host.StartSchedulingAsync(CancellationToken.None);
        var conversations = new SqliteConversationRepository(database);
        var turns = new SqliteConversationTurnRepository(database, new SqliteTurnCommitGate());
        var taskService = new AutomationTaskService(
            taskStore,
            runStore,
            calculator,
            host,
            notifier,
            cancellations,
            NullLogger<AutomationTaskService>.Instance);
        var bridge = new AutomationBridge(
            taskStore,
            runStore,
            taskService,
            calculator,
            host,
            notifier,
            cancellations,
            conversations,
            turns,
            new SqliteUsageStatisticsReader(database),
            new DesktopSettingsJsonStore(paths),
            paths,
            new SelfClaw.Desktop.Services.WebView.WebViewHostChannel(),
            Dispatcher.CurrentDispatcher,
            NullLogger<AutomationBridge>.Instance);
        return new Harness(taskStore, runStore, turns, notifier, bridge);
    }

    private static async Task<(Guid RunId, Guid ConversationId, long PromptLength)> SeedTerminalRunAsync(
        Harness harness,
        AutomationTaskRecord task,
        string prompt)
    {
        var now = DateTimeOffset.UtcNow;
        var conversation = new ConversationRecord(Guid.NewGuid(), task.Title, null, ConversationMode.Programming,
            ToolPermissionMode.FullAccess, $"automation:{task.Id:D}", now, now, Kind: ConversationKind.Automation);
        var turn = new ConversationTurnRecord(Guid.NewGuid(), conversation.Id, AgentExecutionMode.Direct,
            DirectTurnOrigin.Automation, ConversationTurnStatus.Running, now);
        await harness.Turns.StartTurnAsync(new ConversationTurnStart(conversation, turn, prompt, Guid.NewGuid()));
        var run = CreateRun(task.Id, AutomationRunStatus.Completed, now.AddMinutes(-1)) with { ConversationId = conversation.Id };
        await harness.RunStore.TryInsertAsync(run, false);
        return (run.Id, conversation.Id, prompt.Length);
    }

    private static async Task<JsonElement> InvokeAsync(AutomationBridge bridge, string type, string json)
    {
        using var document = JsonDocument.Parse(json);
        var response = await bridge.TryHandleAsync(type, document.RootElement, CancellationToken.None);
        return JsonSerializer.SerializeToElement(response, WebJsonOptions);
    }

    // WebViewHostChannel 以 camelCase 推给前端，测试必须用同一形状断言。
    private static readonly JsonSerializerOptions WebJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static AutomationTaskRecord CreateTask(AutomationTaskKind kind, AutomationScheduleSpec? schedule, string title)
    {
        var now = DateTimeOffset.UtcNow;
        return new AutomationTaskRecord(Guid.NewGuid(), title, string.Empty, kind, schedule,
            schedule?.RunAtUtc, "review the nightly diff", string.Empty, "system", [], [], [], null, null,
            ToolPermissionMode.FullAccess, AutomationTaskStatus.Open, null, null, true, null, null, now, now);
    }

    private static AutomationRunRecord CreateRun(Guid taskId, AutomationRunStatus status, DateTimeOffset at)
        => new(Guid.NewGuid(), taskId, null, AutomationRunTrigger.Scheduled, at, status, null, 1, null, null, null, null,
            status is AutomationRunStatus.Running ? at : at.AddMinutes(-1),
            status is AutomationRunStatus.Completed ? at.AddMinutes(1) : null, null, null, at);

    private sealed record Harness(
        IAutomationTaskStore TaskStore,
        IAutomationRunStore RunStore,
        IConversationTurnRepository Turns,
        AutomationChangeNotifier Notifier,
        AutomationBridge Bridge);

    private sealed class NoopRecovery : IAutomationStartupRecovery
    {
        public Task<IReadOnlyList<AutomationRunRecord>> RecoverAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AutomationRunRecord>>([]);
    }

    private sealed class NoopRegistrar : IAutomationScheduleRegistrar
    {
        public Task UpsertCronAsync(Guid taskId, string cronExpression, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task UpsertOnceAsync(Guid taskId, DateTimeOffset scheduledForUtc, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RemoveAsync(Guid taskId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingRunNotifier : IAutomationRunNotifier
    {
        public List<(Guid TaskId, Guid RunId, AutomationRunStatus Status)> Runs { get; } = [];

        public Task NotifyAsync(AutomationTaskRecord task, AutomationRunRecord run, CancellationToken cancellationToken = default)
        {
            Runs.Add((task.Id, run.Id, run.Status));
            return Task.CompletedTask;
        }
    }

    private sealed class CompletingExecutor : IAutomationRunExecutor
    {
        private readonly IAutomationRunStore _runStore;

        public CompletingExecutor(IAutomationRunStore runStore) => _runStore = runStore;

        public async Task ExecuteAsync(AutomationRunRecord run, AutomationTaskRecord task, CancellationToken cancellationToken)
            => await _runStore.TryCompleteAsync(run.Id, run with
            {
                Status = AutomationRunStatus.Completed,
                CompletedAtUtc = DateTimeOffset.UtcNow
            }, cancellationToken);
    }
}
