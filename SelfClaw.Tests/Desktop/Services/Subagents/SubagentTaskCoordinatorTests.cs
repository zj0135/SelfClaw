using FluentAssertions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Desktop.Services;
using SelfClaw.Desktop.Services.Subagents;
using SelfClaw.Infrastructure.Agents.Subagents.Persistence;
using SelfClaw.Infrastructure.Agents.Subagents.Runtime;
using SelfClaw.Infrastructure.AiProviders.Models.Views;
using SelfClaw.Infrastructure.Data.Sqlite;
using SelfClaw.Infrastructure.Data.Sqlite.Repositories;
using SelfClaw.Infrastructure.Options;
using SelfClaw.Tests.TestDoubles;

namespace SelfClaw.Tests.Desktop.Services.Subagents;

public sealed class SubagentTaskCoordinatorTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(),
        "SelfClawTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task StartAsync_atomically_returns_failed_when_allowlisted_definition_is_missing()
    {
        var context = await CreateContextAsync(createDefinition: false);

        var view = await context.Coordinator.StartAsync(CreateRequest(context));

        view.Status.Should().Be(SubagentTaskStatus.Failed);
        view.ErrorCode.Should().Be(SubagentErrorCodes.DefinitionMissing);
        (await context.Tasks.GetDeliveryAsync(context.Parent.Id, view.TaskId))
            .Should().NotBeNull();
        (await context.Tasks.TryClaimNextAsync(DateTimeOffset.UtcNow)).Should().BeNull();
    }

    [Fact]
    public async Task StartAsync_freezes_definition_and_creates_only_the_exact_child_task_message()
    {
        var context = await CreateContextAsync(createDefinition: true);
        var request = CreateRequest(context) with { Task = "Review only this explicit task." };

        var view = await context.Coordinator.StartAsync(request);
        File.WriteAllText(context.DefinitionPath, ValidDefinition("Changed instructions."));

        view.Status.Should().Be(SubagentTaskStatus.Queued);
        var stored = await context.Tasks.GetAsync(context.Parent.Id, view.TaskId);
        stored!.DefinitionSnapshotJson.Should().Contain("Original instructions.").And.NotContain("Changed instructions.");
        (await context.Conversations.ListMessagesAsync(view.ChildConversationId))
            .Should().ContainSingle()
            .Which.MarkdownContent.Should().Be(request.Task);
    }

    [Fact]
    public async Task StartAsync_rejects_outside_allowlist_without_creating_a_task()
    {
        var context = await CreateContextAsync(createDefinition: true);
        var request = CreateRequest(context) with { SubagentId = "other" };

        var action = () => context.Coordinator.StartAsync(request);

        await action.Should().ThrowAsync<UnauthorizedAccessException>();
        (await context.Tasks.ListAsync(context.Parent.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task Cancel_and_retry_keep_the_old_terminal_attempt_and_copy_frozen_snapshots()
    {
        var context = await CreateContextAsync(createDefinition: true);
        var queued = await context.Coordinator.StartAsync(CreateRequest(context));

        var cancelled = await context.Coordinator.CancelAsync(
            new SubagentTaskCommand(context.Parent.Id, queued.TaskId));
        var retried = await context.Coordinator.RetryAsync(
            new SubagentTaskRetryRequest(context.Parent.Id, Guid.NewGuid(), queued.TaskId));

        cancelled.Status.Should().Be(SubagentTaskStatus.Cancelled);
        retried.Status.Should().Be(SubagentTaskStatus.Queued);
        retried.Attempt.Should().Be(2);
        retried.RetryOfTaskId.Should().Be(queued.TaskId);
        var originalRecord = await context.Tasks.GetAsync(context.Parent.Id, queued.TaskId);
        var retryRecord = await context.Tasks.GetAsync(context.Parent.Id, retried.TaskId);
        retryRecord!.DefinitionSnapshotJson.Should().Be(originalRecord!.DefinitionSnapshotJson);
        retryRecord.ParentExecutionSnapshotJson.Should().Be(originalRecord.ParentExecutionSnapshotJson);
    }

    [Fact]
    public async Task CancelAndWaitAsync_times_out_until_a_running_child_reaches_terminal_state()
    {
        var context = await CreateContextAsync(createDefinition: true);
        var queued = await context.Coordinator.StartAsync(CreateRequest(context));
        _ = await context.Tasks.TryClaimNextAsync(DateTimeOffset.UtcNow);

        var action = () => context.Coordinator.CancelAndWaitAsync(
            context.Parent.Id,
            TimeSpan.FromMilliseconds(50));

        await action.Should().ThrowAsync<TimeoutException>();
        var running = await context.Tasks.GetAsync(context.Parent.Id, queued.TaskId);
        running!.Status.Should().Be(SubagentTaskStatus.Running);
        running.CancelRequestedAtUtc.Should().NotBeNull();
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

    private async Task<TestContext> CreateContextAsync(bool createDefinition)
    {
        var storagePaths = new StoragePaths(
            _rootPath,
            Path.Combine(_rootPath, "selfclaw.db"),
            Path.Combine(_rootPath, "secrets"));
        var database = new SqliteDatabase(storagePaths);
        var conversations = new SqliteConversationRepository(database);
        var tasks = new SqliteSubagentTaskRepository(database, new SubagentCompletionEnvelopeFactory());
        await tasks.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var parent = new ConversationRecord(
            Guid.NewGuid(),
            "Parent",
            WorkspaceRootId: null,
            ConversationMode.Programming,
            ToolPermissionMode.RequireApproval,
            "build",
            now,
            now);
        await conversations.UpsertConversationAsync(parent);
        var catalog = new SubagentDefinitionCatalog(storagePaths);
        var definitionPath = Path.Combine(catalog.SubagentsDirectory, "reviewer.md");
        if (createDefinition)
        {
            Directory.CreateDirectory(catalog.SubagentsDirectory);
            File.WriteAllText(definitionPath, ValidDefinition("Original instructions."));
        }

        var modelProfileId = Guid.NewGuid();
        var settings = new StubAiProviderSettingsService(modelProfileId)
        {
            EnabledModels = [new EnabledModelView(modelProfileId, "Test", "test", "Fixture")]
        };
        var preflight = new SubagentTaskPreflight(
            settings,
            new EmptyExtensionPackageRepository(),
            new EmptyMcpServerRepository());
        var coordinator = new SubagentTaskCoordinator(
            tasks,
            catalog,
            new SubagentTaskSnapshotSerializer(),
            preflight,
            new SubagentTaskWakeSignal(),
            new SubagentTaskExecutionRegistry());
        return new TestContext(
            conversations,
            tasks,
            coordinator,
            parent,
            modelProfileId,
            definitionPath);
    }

    private static SubagentTaskStartRequest CreateRequest(TestContext context)
    {
        var agent = new AgentRuntimeDefinition(
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
        return new SubagentTaskStartRequest(
            context.Parent.Id,
            Guid.NewGuid(),
            "reviewer",
            "Review the implementation.",
            agent,
            context.ModelProfileId,
            WorkspaceRoot: null,
            ToolPermissionMode.RequireApproval,
            new DirectCapabilityCeiling(
                AgentRuntimeDefinition.SystemToolPolicy,
                [],
                [],
                [],
                ["reviewer"]));
    }

    private static string ValidDefinition(string instructions)
        => $$"""
            ---
            name: Reviewer
            description: Reviews code
            tools: read-only
            maxRunSeconds: 900
            ---
            {{instructions}}
            """;

    private sealed record TestContext(
        SqliteConversationRepository Conversations,
        SqliteSubagentTaskRepository Tasks,
        SubagentTaskCoordinator Coordinator,
        ConversationRecord Parent,
        Guid ModelProfileId,
        string DefinitionPath);
}
