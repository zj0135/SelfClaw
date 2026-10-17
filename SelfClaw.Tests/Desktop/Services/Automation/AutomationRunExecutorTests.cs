using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Desktop.Services.Automation;
using SelfClaw.Desktop.Services.Runtime;
using SelfClaw.Infrastructure.Data.Sqlite;
using SelfClaw.Infrastructure.Data.Sqlite.Repositories;
using SelfClaw.Infrastructure.Options;
using SelfClaw.Tests.TestDoubles;

namespace SelfClaw.Tests.Desktop.Services.Automation;

public sealed class AutomationRunExecutorTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(),
        "SelfClawTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Provider_failure_with_zero_tool_evidence_retries_three_times()
    {
        var runtime = new StubRuntime((_, _) => Failed("provider connection reset by peer"));
        var harness = await CreateHarnessAsync(runtime);

        var terminal = await ExecuteAsync(harness);

        runtime.RequestCount.Should().Be(3);
        terminal.Status.Should().Be(AutomationRunStatus.Failed);
        terminal.ErrorCode.Should().Be(AutomationErrorCodes.ProviderFailed);
        terminal.Attempt.Should().Be(3);
        var turns = await harness.Turns.ListTurnsAsync(terminal.ConversationId!.Value);
        turns.Should().HaveCount(3);
        turns.Should().OnlyContain(turn => turn.Status == ConversationTurnStatus.Failed);
    }

    [Fact]
    public async Task Provider_failure_after_a_tool_executed_is_not_retried()
    {
        var runtime = new StubRuntime((_, _) => ToolThenFailed("provider connection reset by peer"));
        var harness = await CreateHarnessAsync(runtime);

        var terminal = await ExecuteAsync(harness);

        runtime.RequestCount.Should().Be(1);
        terminal.Status.Should().Be(AutomationRunStatus.Failed);
        terminal.Attempt.Should().Be(1);
        var tools = await harness.Conversations.ListToolExecutionsAsync(terminal.ConversationId!.Value);
        tools.Should().ContainSingle().Which.Status.Should().Be(ToolExecutionStatus.Completed);
    }

    [Fact]
    public async Task Configuration_failures_are_not_retried()
    {
        var runtime = new StubRuntime((_, _) => Failed("Required Skill 'review' could not be loaded."));
        var harness = await CreateHarnessAsync(runtime);

        var terminal = await ExecuteAsync(harness);

        runtime.RequestCount.Should().Be(1);
        terminal.Status.Should().Be(AutomationRunStatus.Failed);
        terminal.ErrorCode.Should().Be(AutomationErrorCodes.ProviderFailed);
    }

    [Fact]
    public async Task Successful_run_records_usage_snapshot_and_workspace()
    {
        var runtime = new StubRuntime((_, _) => Succeeded());
        var workspaceRoot = new WorkspaceRoot(Guid.NewGuid(), "Workspace", _rootPath, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var harness = await CreateHarnessAsync(runtime, workspaceRoot: workspaceRoot);

        var terminal = await ExecuteAsync(harness);

        terminal.Status.Should().Be(AutomationRunStatus.Completed);
        terminal.ModelProfileId.Should().Be(harness.DefaultModelProfileId);
        terminal.CapabilitySnapshotJson.Should().NotBeNull();
        terminal.ConversationId.Should().NotBeNull();
        terminal.WorkspaceRootId.Should().Be(workspaceRoot.Id);
        terminal.WorkspacePathSnapshot.Should().Be(workspaceRoot.RootPath);
        var snapshot = new AutomationRunSnapshotSerializer().Deserialize(terminal.CapabilitySnapshotJson!);
        snapshot.ToolPolicy.Should().Be(AgentRuntimeDefinition.SystemToolPolicy);
        snapshot.Instructions.Should().Be("Follow the review checklist.");
        var turns = await harness.Turns.ListTurnsAsync(terminal.ConversationId!.Value);
        var turn = turns.Should().ContainSingle().Which;
        turn.Origin.Should().Be(DirectTurnOrigin.Automation);
        turn.Status.Should().Be(ConversationTurnStatus.Succeeded);
        turn.Usage!.InputTokens.Should().Be(21);
        turn.Usage.OutputTokens.Should().Be(8);
        var conversation = await harness.Conversations.GetConversationAsync(terminal.ConversationId.Value);
        conversation!.Kind.Should().Be(ConversationKind.Automation);
        conversation.ParentConversationId.Should().BeNull();
    }

    [Fact]
    public async Task Run_snapshot_does_not_change_when_the_task_is_edited_afterwards()
    {
        var runtime = new StubRuntime((_, _) => Succeeded());
        var harness = await CreateHarnessAsync(runtime);

        var terminal = await ExecuteAsync(harness);
        var snapshotJson = terminal.CapabilitySnapshotJson!;

        var edited = harness.Task with
        {
            Title = "Edited",
            ToolPolicy = AgentRuntimeDefinition.NoneToolPolicy,
            SkillIds = ["other"],
            McpServerIds = ["other-mcp"],
            ModelProfileId = Guid.NewGuid()
        };
        await harness.Tasks.SaveAsync(edited);

        var reloaded = await harness.RunStore.GetAsync(terminal.Id);
        reloaded!.CapabilitySnapshotJson.Should().Be(snapshotJson);
        reloaded.ModelProfileId.Should().Be(harness.DefaultModelProfileId);
        var snapshot = new AutomationRunSnapshotSerializer().Deserialize(reloaded.CapabilitySnapshotJson!);
        snapshot.ToolPolicy.Should().Be(AgentRuntimeDefinition.SystemToolPolicy);
        snapshot.SkillIds.Should().BeEquivalentTo(harness.Task.SkillIds);
    }

    [Fact]
    public async Task Disabled_or_deleted_model_fails_without_falling_back_to_the_default()
    {
        var runtime = new StubRuntime((_, _) => Succeeded());
        var harness = await CreateHarnessAsync(runtime, modelProfileId: Guid.NewGuid(), enableModels: false);

        var terminal = await ExecuteAsync(harness);

        runtime.RequestCount.Should().Be(0);
        terminal.Status.Should().Be(AutomationRunStatus.Failed);
        terminal.ErrorCode.Should().Be(AutomationErrorCodes.ModelUnavailable);
        terminal.ConversationId.Should().BeNull();
    }

    [Fact]
    public async Task User_cancellation_becomes_cancelled_by_user_and_finalizes_the_transcript()
    {
        var runtime = new StubRuntime((_, cancellationToken) => Blocked(cancellationToken));
        var harness = await CreateHarnessAsync(runtime);

        var execution = harness.Executor.ExecuteAsync(harness.Run, harness.Task, harness.Cancellations.BeginRun(harness.Run.Id));
        await runtime.Started.WaitAsync(TimeSpan.FromSeconds(5));
        harness.Cancellations.CancelRun(harness.Run.Id);
        var action = () => execution;

        await action.Should().ThrowAsync<OperationCanceledException>();

        var terminal = await harness.RunStore.GetAsync(harness.Run.Id);
        terminal!.Status.Should().Be(AutomationRunStatus.Cancelled);
        terminal.ErrorCode.Should().Be(AutomationErrorCodes.CancelledByUser);
        var turns = await harness.Turns.ListTurnsAsync(terminal.ConversationId!.Value);
        turns.Should().ContainSingle().Which.Status.Should().Be(ConversationTurnStatus.Cancelled);
    }

    [Fact]
    public async Task Application_stopping_becomes_cancelled_application_stopping()
    {
        var runtime = new StubRuntime((_, cancellationToken) => Blocked(cancellationToken));
        var harness = await CreateHarnessAsync(runtime);

        var execution = harness.Executor.ExecuteAsync(harness.Run, harness.Task, harness.Cancellations.BeginRun(harness.Run.Id));
        await runtime.Started.WaitAsync(TimeSpan.FromSeconds(5));
        harness.Cancellations.CancelAll();
        var action = () => execution;

        await action.Should().ThrowAsync<OperationCanceledException>();

        var terminal = await harness.RunStore.GetAsync(harness.Run.Id);
        terminal!.Status.Should().Be(AutomationRunStatus.Cancelled);
        terminal.ErrorCode.Should().Be(AutomationErrorCodes.ApplicationStopping);
        var turns = await harness.Turns.ListTurnsAsync(terminal.ConversationId!.Value);
        turns.Should().ContainSingle().Which.Status.Should().Be(ConversationTurnStatus.Cancelled);
    }

    [Fact]
    public async Task Wall_clock_timeout_becomes_failed_timed_out()
    {
        var runtime = new StubRuntime((_, cancellationToken) => Blocked(cancellationToken));
        var harness = await CreateHarnessAsync(runtime);

        var execution = harness.Executor.ExecuteAsync(harness.Run, harness.Task, harness.Cancellations.BeginRun(harness.Run.Id));
        await runtime.Started.WaitAsync(TimeSpan.FromSeconds(5));
        harness.TimeProvider.FireManualTimers();
        var action = () => execution;

        await action.Should().ThrowAsync<OperationCanceledException>();

        var terminal = await harness.RunStore.GetAsync(harness.Run.Id);
        terminal!.Status.Should().Be(AutomationRunStatus.Failed);
        terminal.ErrorCode.Should().Be(AutomationErrorCodes.TimedOut);
        var turns = await harness.Turns.ListTurnsAsync(terminal.ConversationId!.Value);
        turns.Should().ContainSingle().Which.Status.Should().Be(ConversationTurnStatus.Failed);
    }

    [Fact]
    public async Task Automation_turn_runs_with_its_own_capabilities_and_full_access()
    {
        var runtime = new StubRuntime((_, _) => Succeeded());
        var harness = await CreateHarnessAsync(runtime);

        await ExecuteAsync(harness);

        runtime.Requests.Should().ContainSingle();
        var request = runtime.Requests[0].Should().BeOfType<DirectChatTurnRequest>().Subject;
        request.ExecutionContext.Origin.Should().Be(DirectTurnOrigin.Automation);
        request.ExecutionContext.CapabilityCeiling.Should().BeNull();
        request.ToolPermissionMode.Should().Be(ToolPermissionMode.FullAccess);
        request.Agent.Id.Should().Be($"automation:{harness.Task.Id:D}");
        request.Agent.SubagentIds.Should().BeEmpty();
    }

    public void Dispose()
    {
        if (!Directory.Exists(_rootPath))
        {
            return;
        }

        try
        {
            Directory.Delete(_rootPath, true);
        }
        catch (IOException)
        {
        }
    }

    private static async Task<AutomationRunRecord> ExecuteAsync(Harness harness)
    {
        await harness.Executor.ExecuteAsync(harness.Run, harness.Task, harness.Cancellations.BeginRun(harness.Run.Id));
        return await harness.RunStore.GetAsync(harness.Run.Id)
            ?? throw new InvalidOperationException("The fixture run disappeared.");
    }

    private async Task<Harness> CreateHarnessAsync(
        StubRuntime runtime,
        Guid? modelProfileId = null,
        WorkspaceRoot? workspaceRoot = null,
        bool enableModels = true)
    {
        var database = new SqliteDatabase(StoragePathDefaults.Create(
            _rootPath,
            Path.Combine(_rootPath, $"{Guid.NewGuid():N}.db"),
            Path.Combine(_rootPath, "secrets")));
        await database.EnsureInitializedAsync();
        var conversations = new SqliteConversationRepository(database);
        var turns = new SqliteConversationTurnRepository(database);
        var inputs = new SqliteConversationInputRepository(database);
        var workspaces = new SqliteWorkspaceRepository(database);
        if (workspaceRoot is not null)
        {
            await workspaces.UpsertWorkspaceRootAsync(workspaceRoot);
        }

        var taskStore = new SqliteAutomationTaskRepository(database);
        var runStore = new SqliteAutomationRunRepository(database);
        var resolvedModelId = modelProfileId ?? Guid.NewGuid();
        var models = new StubAiModelCatalog(resolvedModelId)
        {
            EnabledModels = enableModels
                ? [new EnabledModelView(resolvedModelId, "Model", "model", "Provider")]
                : []
        };
        var task = new AutomationTaskRecord(
            Guid.NewGuid(),
            "Nightly review",
            string.Empty,
            AutomationTaskKind.Once,
            new AutomationScheduleSpec(RunAtUtc: DateTimeOffset.UtcNow),
            DateTimeOffset.UtcNow,
            "Review the repository.",
            "Follow the review checklist.",
            AgentRuntimeDefinition.SystemToolPolicy,
            [],
            ["review"],
            [],
            modelProfileId,
            workspaceRoot?.Id,
            ToolPermissionMode.FullAccess,
            AutomationTaskStatus.Open,
            null,
            null,
            true,
            null,
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        await taskStore.SaveAsync(task);
        var queued = new AutomationRunRecord(
            Guid.NewGuid(),
            task.Id,
            null,
            AutomationRunTrigger.Manual,
            null,
            AutomationRunStatus.Queued,
            null,
            1,
            task.ModelProfileId,
            null,
            task.WorkspaceRootId,
            null,
            null,
            null,
            null,
            null,
            DateTimeOffset.UtcNow);
        await runStore.TryInsertAsync(queued, skipWhenTaskActive: false);
        var run = await runStore.TryClaimAsync(queued.Id, DateTimeOffset.UtcNow)
            ?? throw new InvalidOperationException("The fixture run could not be claimed.");
        var timeProvider = new AutomationTimeProvider();
        var cancellations = new AutomationRunCancellationRegistry();
        var executor = new AutomationRunExecutor(
            conversations,
            turns,
            new SqliteConversationCompactionRepository(database),
            runStore,
            runStore,
            workspaces,
            models,
            runtime,
            new ConversationTurnRecorder(conversations, turns, inputs, NullLogger<ConversationTurnRecorder>.Instance),
            new DesktopTurnFinalizer(turns, NullLogger<DesktopTurnFinalizer>.Instance),
            new AutomationRunSnapshotSerializer(),
            new AutomationToolApprovalHandler(NullLogger<AutomationToolApprovalHandler>.Instance),
            cancellations,
            timeProvider,
            NullLogger<AutomationRunExecutor>.Instance);
        return new Harness(executor, conversations, turns, taskStore, runStore, task, run, cancellations, timeProvider, resolvedModelId);
    }

    private static async IAsyncEnumerable<AgentStreamEvent> Failed(string message)
    {
        yield return new RunStartedEvent(null, "fixture", null);
        await Task.Yield();
        yield return new RunCompletedEvent(RunCompletionStatus.Failed, FinalText: null, ErrorMessage: message);
    }

    private static async IAsyncEnumerable<AgentStreamEvent> ToolThenFailed(string message)
    {
        yield return new RunStartedEvent(null, "fixture", null);
        yield return new ToolCallStartedEvent("call-1", "read_file", "{}", ToolCallKind.Read, ToolSourceKind.BuiltIn);
        yield return new ToolCallCompletedEvent("call-1", ToolCallStatus.Completed, "read", "content");
        await Task.Yield();
        yield return new RunCompletedEvent(RunCompletionStatus.Failed, FinalText: null, ErrorMessage: message);
    }

    private static async IAsyncEnumerable<AgentStreamEvent> Succeeded()
    {
        yield return new RunStartedEvent(null, "fixture", null);
        yield return new AssistantTextDeltaEvent("text", "final answer");
        yield return new UsageReportedEvent(new TurnUsage(InputTokens: 21, OutputTokens: 8));
        await Task.Yield();
        yield return new RunCompletedEvent(RunCompletionStatus.Succeeded, "final answer");
    }

    private static async IAsyncEnumerable<AgentStreamEvent> Blocked([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return new RunStartedEvent(null, "fixture", null);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private sealed class StubRuntime : IAgentChatRuntime
    {
        private readonly Func<int, CancellationToken, IAsyncEnumerable<AgentStreamEvent>> _script;
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _requests;

        internal StubRuntime(Func<int, CancellationToken, IAsyncEnumerable<AgentStreamEvent>> script)
        {
            _script = script;
        }

        internal int RequestCount => Volatile.Read(ref _requests);

        internal List<ChatTurnRequest> Requests { get; } = [];

        internal Task Started => _started.Task;

        public IAsyncEnumerable<AgentStreamEvent> StreamTurnAsync(
            ChatTurnRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var index = Interlocked.Increment(ref _requests);
            _started.TrySetResult();
            return _script(index, cancellationToken);
        }
    }

    /// <summary>
    /// The retry backoff is short and must not slow the suite down, so any timer under a minute completes
    /// immediately. The run's 30 minute wall clock stays manual so a test decides when it fires.
    /// </summary>
    private sealed class AutomationTimeProvider : TimeProvider
    {
        private readonly List<(TimerCallback Callback, object? State)> _manualTimers = [];
        private readonly object _gate = new();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ArgumentNullException.ThrowIfNull(callback);
            if (dueTime > TimeSpan.Zero && dueTime <= TimeSpan.FromMinutes(1))
            {
                return new ImmediateTimer(callback, state);
            }

            lock (_gate)
            {
                _manualTimers.Add((callback, state));
            }

            return new ManualTimer();
        }

        internal void FireManualTimers()
        {
            (TimerCallback Callback, object? State)[] timers;
            lock (_gate)
            {
                timers = _manualTimers.ToArray();
            }

            foreach (var (callback, state) in timers)
            {
                callback(state);
            }
        }
    }

    private sealed class ImmediateTimer : ITimer
    {
        private volatile bool _disposed;

        internal ImmediateTimer(TimerCallback callback, object? state)
            => ThreadPool.QueueUserWorkItem(_ =>
            {
                if (!_disposed)
                {
                    callback(state);
                }
            });

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose() => _disposed = true;

        public ValueTask DisposeAsync()
        {
            _disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ManualTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed record Harness(
        AutomationRunExecutor Executor,
        IConversationRepository Conversations,
        IConversationTurnRepository Turns,
        IAutomationTaskStore Tasks,
        IAutomationRunStore RunStore,
        AutomationTaskRecord Task,
        AutomationRunRecord Run,
        AutomationRunCancellationRegistry Cancellations,
        AutomationTimeProvider TimeProvider,
        Guid DefaultModelProfileId);
}
