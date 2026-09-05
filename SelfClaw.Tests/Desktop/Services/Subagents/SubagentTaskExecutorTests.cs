using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Desktop.Services;
using SelfClaw.Desktop.Services.Runtime;
using SelfClaw.Desktop.Services.Subagents;
using SelfClaw.Desktop.Services.Subagents.Models;
using SelfClaw.Infrastructure.Agents.Subagents.Persistence;
using SelfClaw.Infrastructure.Agents.Subagents.Runtime;
using SelfClaw.Infrastructure.AiProviders.Models.Views;
using SelfClaw.Infrastructure.Data.Sqlite;
using SelfClaw.Infrastructure.Data.Sqlite.Repositories;
using SelfClaw.Infrastructure.Options;
using SelfClaw.Tests.TestDoubles;

namespace SelfClaw.Tests.Desktop.Services.Subagents;

public sealed class SubagentTaskExecutorTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(),
        "SelfClawTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ExecuteAsync_sends_only_isolated_child_input_and_records_all_events()
    {
        var storagePaths = new StoragePaths(
            _rootPath,
            Path.Combine(_rootPath, "selfclaw.db"),
            Path.Combine(_rootPath, "secrets"));
        var database = new SqliteDatabase(storagePaths);
        var conversations = new SqliteConversationRepository(database);
        var tasks = new SqliteSubagentTaskRepository(database, new SubagentCompletionEnvelopeFactory());
        await tasks.InitializeAsync();
        var task = await CreateRunningTaskAsync(conversations, tasks);
        var modelProfileId = task.ResolvedModelProfileId
            ?? throw new InvalidOperationException("The fixture task has no model profile.");
        var settings = new StubAiProviderSettingsService(modelProfileId)
        {
            EnabledModels = [new EnabledModelView(modelProfileId, "Test", "test", "Fixture")]
        };
        var preflight = new SubagentTaskPreflight(
            settings,
            new EmptyExtensionPackageRepository(),
            new EmptyMcpServerRepository());
        var runtime = new RecordingRuntime();
        var executor = new SubagentTaskExecutor(
            conversations,
            tasks,
            runtime,
            new ConversationTurnRecorder(
                conversations,
                NullLogger<ConversationTurnRecorder>.Instance),
            new DesktopToolApprovalHandler(),
            new SubagentTaskSnapshotSerializer(),
            preflight,
            new SubagentTaskExecutionRegistry(),
            NullLogger<SubagentTaskExecutor>.Instance);

        await executor.ExecuteAsync(task, CancellationToken.None);

        var request = runtime.Requests.Should().ContainSingle().Which.Should().BeOfType<DirectChatTurnRequest>().Subject;
        request.ConversationId.Should().Be(task.ChildConversationId);
        request.TurnId.Should().Be(task.ChildTurnId);
        request.ExecutionContext.Origin.Should().Be(DirectTurnOrigin.Subagent);
        request.Agent.SubagentIds.Should().BeEmpty();
        request.Messages.Should().ContainSingle()
            .Which.MarkdownContent.Should().Be(task.TaskText);
        var terminal = await tasks.GetAsync(task.ParentConversationId, task.Id);
        terminal!.Status.Should().Be(SubagentTaskStatus.Succeeded);
        terminal.FinalText.Should().Be("final answer");
        terminal.InputTokens.Should().Be(21);
        terminal.OutputTokens.Should().Be(8);
        var assistant = (await conversations.ListMessagesAsync(task.ChildConversationId))
            .Single(message => message.Role == MessageRole.Assistant);
        assistant.MarkdownContent.Should().Be("final answer");
        assistant.Segments.Should().SatisfyRespectively(
            segment => segment.Kind.Should().Be(MessageSegmentKind.Thinking),
            segment => segment.Kind.Should().Be(MessageSegmentKind.Text),
            segment => segment.Kind.Should().Be(MessageSegmentKind.ToolCall));
        (await conversations.ListToolExecutionsAsync(task.ChildConversationId))
            .Should().ContainSingle()
            .Which.Status.Should().Be(ToolExecutionStatus.Completed);
        (await tasks.GetDeliveryAsync(task.ParentConversationId, task.Id))
            .Should().NotBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_classifies_a_running_parent_cancellation()
    {
        var storagePaths = new StoragePaths(
            _rootPath,
            Path.Combine(_rootPath, "cancellation.db"),
            Path.Combine(_rootPath, "cancellation-secrets"));
        var database = new SqliteDatabase(storagePaths);
        var conversations = new SqliteConversationRepository(database);
        var tasks = new SqliteSubagentTaskRepository(database, new SubagentCompletionEnvelopeFactory());
        await tasks.InitializeAsync();
        var task = await CreateRunningTaskAsync(conversations, tasks);
        var modelProfileId = task.ResolvedModelProfileId
            ?? throw new InvalidOperationException("The fixture task has no model profile.");
        var runtime = new CancellingRuntime();
        var registry = new SubagentTaskExecutionRegistry();
        var executor = new SubagentTaskExecutor(
            conversations,
            tasks,
            runtime,
            new ConversationTurnRecorder(
                conversations,
                NullLogger<ConversationTurnRecorder>.Instance),
            new DesktopToolApprovalHandler(),
            new SubagentTaskSnapshotSerializer(),
            new SubagentTaskPreflight(
                new StubAiProviderSettingsService(modelProfileId)
                {
                    EnabledModels = [new EnabledModelView(modelProfileId, "Test", "test", "Fixture")]
                },
                new EmptyExtensionPackageRepository(),
                new EmptyMcpServerRepository()),
            registry,
            NullLogger<SubagentTaskExecutor>.Instance);

        var execution = executor.ExecuteAsync(task, CancellationToken.None);
        await runtime.Started.WaitAsync(TimeSpan.FromSeconds(5));
        await tasks.RequestCancellationAsync(
            task.ParentConversationId,
            task.Id,
            DateTimeOffset.UtcNow);
        registry.RequestCancellation(task.Id);
        await execution;

        var terminal = await tasks.GetAsync(task.ParentConversationId, task.Id)
            ?? throw new InvalidOperationException("The cancelled fixture task is missing.");
        terminal.Status.Should().Be(SubagentTaskStatus.Cancelled);
        terminal.ErrorCode.Should().Be(SubagentErrorCodes.CancelledByParent);
        (await tasks.GetDeliveryAsync(task.ParentConversationId, task.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task RecoverInterruptedAsync_terminalizes_a_previous_running_task_without_replaying_runtime()
    {
        var storagePaths = new StoragePaths(
            _rootPath,
            Path.Combine(_rootPath, "recovery.db"),
            Path.Combine(_rootPath, "recovery-secrets"));
        var database = new SqliteDatabase(storagePaths);
        var conversations = new SqliteConversationRepository(database);
        var tasks = new SqliteSubagentTaskRepository(database, new SubagentCompletionEnvelopeFactory());
        await tasks.InitializeAsync();
        var task = await CreateRunningTaskAsync(conversations, tasks);
        var runtime = new RecordingRuntime();
        var executor = new SubagentTaskExecutor(
            conversations,
            tasks,
            runtime,
            new ConversationTurnRecorder(
                conversations,
                NullLogger<ConversationTurnRecorder>.Instance),
            new DesktopToolApprovalHandler(),
            new SubagentTaskSnapshotSerializer(),
            new SubagentTaskPreflight(
                new StubAiProviderSettingsService(),
                new EmptyExtensionPackageRepository(),
                new EmptyMcpServerRepository()),
            new SubagentTaskExecutionRegistry(),
            NullLogger<SubagentTaskExecutor>.Instance);

        await executor.RecoverInterruptedAsync(task, CancellationToken.None);

        runtime.Requests.Should().BeEmpty();
        var terminal = await tasks.GetAsync(task.ParentConversationId, task.Id);
        terminal!.Status.Should().Be(SubagentTaskStatus.Interrupted);
        terminal.ErrorCode.Should().Be(SubagentErrorCodes.ProcessInterrupted);
        (await tasks.GetDeliveryAsync(task.ParentConversationId, task.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task BackgroundHost_recovers_running_without_replay_and_executes_queued_tasks()
    {
        var storagePaths = new StoragePaths(
            _rootPath,
            Path.Combine(_rootPath, "host-recovery.db"),
            Path.Combine(_rootPath, "host-recovery-secrets"));
        var database = new SqliteDatabase(storagePaths);
        var conversations = new SqliteConversationRepository(database);
        var tasks = new SqliteSubagentTaskRepository(database, new SubagentCompletionEnvelopeFactory());
        await tasks.InitializeAsync();
        var interruptedCandidate = await CreateRunningTaskAsync(conversations, tasks);
        var queued = await CreateQueuedTaskAsync(conversations, tasks);
        var modelProfileId = queued.ResolvedModelProfileId
            ?? throw new InvalidOperationException("The queued fixture task has no model profile.");
        var runtime = new RecordingRuntime();
        var wakeSignal = new SubagentTaskWakeSignal();
        var executor = new SubagentTaskExecutor(
            conversations,
            tasks,
            runtime,
            new ConversationTurnRecorder(
                conversations,
                NullLogger<ConversationTurnRecorder>.Instance),
            new DesktopToolApprovalHandler(),
            new SubagentTaskSnapshotSerializer(),
            new SubagentTaskPreflight(
                new StubAiProviderSettingsService(modelProfileId)
                {
                    EnabledModels = [new EnabledModelView(modelProfileId, "Test", "test", "Fixture")]
                },
                new EmptyExtensionPackageRepository(),
                new EmptyMcpServerRepository()),
            new SubagentTaskExecutionRegistry(),
            NullLogger<SubagentTaskExecutor>.Instance);
        using var host = new SubagentTaskBackgroundHost(
            tasks,
            executor,
            wakeSignal,
            NullLogger<SubagentTaskBackgroundHost>.Instance);

        await host.StartAsync(CancellationToken.None);
        var recovered = await WaitForTerminalAsync(tasks, interruptedCandidate);
        var completed = await WaitForTerminalAsync(tasks, queued);
        await host.StopAsync(CancellationToken.None);

        recovered.Status.Should().Be(SubagentTaskStatus.Interrupted);
        completed.Status.Should().Be(SubagentTaskStatus.Succeeded);
        runtime.Requests.Should().ContainSingle()
            .Which.ConversationId.Should().Be(queued.ChildConversationId);
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            try
            {
                Directory.Delete(_rootPath, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task<SubagentTaskRecord> CreateRunningTaskAsync(
        SqliteConversationRepository conversations,
        SqliteSubagentTaskRepository tasks)
    {
        await CreateQueuedTaskAsync(conversations, tasks);
        return (await tasks.TryClaimNextAsync(DateTimeOffset.UtcNow))
            ?? throw new InvalidOperationException("The fixture task could not be claimed.");
    }

    private static async Task<SubagentTaskRecord> CreateQueuedTaskAsync(
        SqliteConversationRepository conversations,
        SqliteSubagentTaskRepository tasks)
    {
        var now = DateTimeOffset.UtcNow;
        var parent = new ConversationRecord(
            Guid.NewGuid(),
            "Parent",
            null,
            ConversationMode.Programming,
            ToolPermissionMode.RequireApproval,
            "build",
            now,
            now);
        await conversations.UpsertConversationAsync(parent);
        var childId = Guid.NewGuid();
        var childTurnId = Guid.NewGuid();
        var modelProfileId = Guid.NewGuid();
        var child = new ConversationRecord(
            childId,
            "Subagent: Reviewer",
            null,
            ConversationMode.Programming,
            ToolPermissionMode.RequireApproval,
            "reviewer",
            now,
            now,
            Kind: ConversationKind.Subagent,
            ParentConversationId: parent.Id);
        const string taskText = "Inspect the current implementation.";
        var message = new MessageRecord(
            Guid.NewGuid(),
            childId,
            MessageRole.User,
            taskText,
            MessageStatus.Completed,
            now,
            now);
        var serializer = new SubagentTaskSnapshotSerializer();
        var definition = new SubagentDefinitionSnapshot(
            1,
            "reviewer",
            "Reviewer",
            "Reviews code",
            null,
            "read-only",
            [],
            [],
            [],
            900,
            "Review only the supplied task.");
        var parentAgent = new AgentRuntimeDefinition(
            "build",
            "Build",
            string.Empty,
            AgentExecutionMode.Direct,
            AgentRuntimeDefinition.SystemToolPolicy,
            [],
            [],
            [],
            ["reviewer"],
            "Parent instructions");
        var parentSnapshot = new SubagentParentExecutionSnapshot(
            1,
            parentAgent,
            modelProfileId,
            null,
            ToolPermissionMode.RequireApproval,
            new DirectCapabilityCeiling(
                AgentRuntimeDefinition.SystemToolPolicy,
                [],
                [],
                [],
                ["reviewer"]));
        var task = new SubagentTaskRecord(
            Guid.NewGuid(),
            parent.Id,
            Guid.NewGuid(),
            childId,
            childTurnId,
            "reviewer",
            "Reviewer",
            taskText,
            SubagentTaskStatus.Queued,
            1,
            null,
            serializer.Serialize(definition),
            serializer.Serialize(parentSnapshot),
            modelProfileId,
            900,
            null,
            null,
            null,
            null,
            null,
            null,
            now,
            null,
            null,
            now,
            now);
        await tasks.CreateAsync(new SubagentTaskCreation(child, message, task));
        return task;
    }

    private static async Task<SubagentTaskRecord> WaitForTerminalAsync(
        SqliteSubagentTaskRepository tasks,
        SubagentTaskRecord task)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var current = await tasks.GetAsync(task.ParentConversationId, task.Id, timeout.Token)
                ?? throw new InvalidOperationException("The fixture task is missing.");
            if (current.Status is SubagentTaskStatus.Succeeded
                or SubagentTaskStatus.Failed
                or SubagentTaskStatus.Cancelled
                or SubagentTaskStatus.Interrupted)
            {
                return current;
            }

            await Task.Delay(20, timeout.Token);
        }
    }

    private sealed class RecordingRuntime : IAgentChatRuntime
    {
        public List<ChatTurnRequest> Requests { get; } = [];

        public async IAsyncEnumerable<AgentStreamEvent> StreamTurnAsync(
            ChatTurnRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            yield return new RunStartedEvent("child", "fixture", null);
            yield return new AssistantThinkingDeltaEvent("thinking", "analysis");
            yield return new AssistantTextDeltaEvent("text", "final answer");
            yield return new ToolCallStartedEvent(
                "call-1",
                "read_file",
                "{}",
                ToolCallKind.Read,
                ToolSourceKind.BuiltIn);
            yield return new ToolCallCompletedEvent(
                "call-1",
                ToolCallStatus.Completed,
                "read",
                "content");
            yield return new UsageReportedEvent(21, 8);
            await Task.Yield();
            yield return new RunCompletedEvent(RunCompletionStatus.Succeeded, "final answer");
        }
    }

    private sealed class CancellingRuntime : IAgentChatRuntime
    {
        private readonly TaskCompletionSource _started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public async IAsyncEnumerable<AgentStreamEvent> StreamTurnAsync(
            ChatTurnRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new RunStartedEvent("child", "fixture", null);
            _started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }
}
