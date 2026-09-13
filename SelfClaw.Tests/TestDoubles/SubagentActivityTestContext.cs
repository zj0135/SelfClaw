using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services;
using SelfClaw.Desktop.Services.Runtime;
using SelfClaw.Desktop.Services.Subagents;
using SelfClaw.Desktop.Services.Subagents.Models;
using SelfClaw.Infrastructure.Agents.Subagents.Persistence;
using SelfClaw.Infrastructure.Agents.Subagents.Runtime;
using SelfClaw.Infrastructure.Data.Sqlite;
using SelfClaw.Infrastructure.Data.Sqlite.Repositories;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Tests.TestDoubles;

internal sealed class SubagentActivityTestContext : IDisposable
{
    private readonly string _rootPath;
    private readonly bool _ownsRoot;

    internal SubagentActivityTestContext(TimeSpan? approvalTimeout = null, string? rootPath = null)
    {
        _rootPath = rootPath ?? Path.Combine(Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));
        _ownsRoot = rootPath is null;
        Database = new SqliteDatabase(new StoragePaths(_rootPath, Path.Combine(_rootPath, "activity.db"), Path.Combine(_rootPath, "secrets")));
        Changes = new SubagentStateChangeNotifier(NullLogger<SubagentStateChangeNotifier>.Instance);
        Conversations = new SqliteConversationRepository(Database);
        Tasks = new SqliteSubagentTaskRepository(Database, new SubagentCompletionEnvelopeFactory(), Changes);
        Store = new InterceptingSubagentExecutionStore(Tasks);
        Registry = new SubagentActivityRegistry(Changes);
        Reader = new CountingSubagentActivityReader(new SqliteSubagentActivityReader(Database));
        Approvals = approvalTimeout is TimeSpan timeout ? new DesktopToolApprovalHandler(timeout) : new DesktopToolApprovalHandler();
        Service = new SubagentActivityService(Reader, Registry, Changes, Approvals, NullLogger<SubagentActivityService>.Instance);
        Recorder = new ConversationTurnRecorder(Conversations, NullLogger<ConversationTurnRecorder>.Instance);
    }

    internal SqliteDatabase Database { get; }
    internal SubagentStateChangeNotifier Changes { get; }
    internal SqliteConversationRepository Conversations { get; }
    internal SqliteSubagentTaskRepository Tasks { get; }
    internal InterceptingSubagentExecutionStore Store { get; }
    internal SubagentActivityRegistry Registry { get; }
    internal CountingSubagentActivityReader Reader { get; }
    internal DesktopToolApprovalHandler Approvals { get; }
    internal SubagentActivityService Service { get; }
    internal ConversationTurnRecorder Recorder { get; }
    internal SubagentTaskExecutionRegistry Executions { get; } = new();

    internal async Task<SubagentTaskRecord> CreateTaskAsync(ConversationRecord? parent = null, bool claim = true)
    {
        await Tasks.InitializeAsync();
        var task = await SubagentTaskTestData.CreateQueuedTaskAsync(Conversations, Tasks, parent);
        return claim
            ? await Tasks.TryClaimNextAsync(DateTimeOffset.UtcNow) ?? throw new InvalidOperationException("Could not claim task.")
            : task;
    }

    internal SubagentTaskExecutor CreateExecutor(SubagentTaskRecord task, ControlledSubagentRuntime runtime, TimeProvider? timeProvider = null)
    {
        var modelId = task.ResolvedModelProfileId ?? throw new InvalidOperationException("Missing model.");
        var settings = new StubAiModelCatalog(modelId)
        {
            EnabledModels = [new EnabledModelView(modelId, "Test", "test", "Fixture")]
        };
        return new SubagentTaskExecutor(Conversations, Store, runtime, Recorder, Approvals,
            new SubagentTaskSnapshotSerializer(), new SubagentTaskPreflight(settings,
                new EmptyExtensionPackageRepository(), new EmptyMcpServerRepository()), Executions, timeProvider ?? TimeProvider.System,
            NullLogger<SubagentTaskExecutor>.Instance, Registry);
    }

    internal async Task<SubagentExecutionSession> RegisterSessionAsync(SubagentTaskRecord task)
    {
        var conversation = await Conversations.GetConversationAsync(task.ChildConversationId)
            ?? throw new InvalidOperationException("Missing child.");
        var input = new SubagentExecutionInput(conversation,
            await Conversations.ListMessagesAsync(task.ChildConversationId), await Conversations.ListToolExecutionsAsync(task.ChildConversationId));
        return await Registry.RegisterAsync(task, input, Recorder, Store, TimeProvider.System);
    }

    internal SubagentTaskCoordinator CreateCoordinator()
        => new(Tasks, new SubagentDefinitionCatalog(StoragePaths.CreateDefault()), new SubagentTaskSnapshotSerializer(),
            new SubagentTaskPreflight(new StubAiModelCatalog(Guid.NewGuid()), new EmptyExtensionPackageRepository(), new EmptyMcpServerRepository()),
            new SubagentTaskWakeSignal(), Executions);

    public void Dispose()
    {
        Service.Dispose();
        Approvals.RejectAll();
        if (_ownsRoot && Directory.Exists(_rootPath))
        {
            try { Directory.Delete(_rootPath, recursive: true); }
            catch (IOException) { }
        }
    }
}
