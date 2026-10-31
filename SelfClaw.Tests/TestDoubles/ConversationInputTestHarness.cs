using System.Runtime.CompilerServices;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Desktop.Services.AgentActivity;
using SelfClaw.Desktop.Services.Agents;
using SelfClaw.Desktop.Services.Agents.Definitions;
using SelfClaw.Desktop.Services.ConversationInputs;
using SelfClaw.Desktop.Services.Notifications;
using SelfClaw.Desktop.Services.Questions;
using SelfClaw.Desktop.Services.Runtime;
using SelfClaw.Desktop.Services.Runtime.Abstractions;
using SelfClaw.Desktop.Services.Settings;
using SelfClaw.Desktop.Services.Tools;
using SelfClaw.Desktop.Services.Transcript;
using SelfClaw.Desktop.Services.WebView;
using SelfClaw.Infrastructure.Data.Sqlite;
using SelfClaw.Infrastructure.Data.Sqlite.Repositories;
using SelfClaw.Infrastructure.Extensions;

namespace SelfClaw.Tests.TestDoubles;

/// <summary>
/// The full real queue graph (SQLite store + run coordinator + dispatcher + service) over an
/// isolated temporary database. It is shared by dispatcher, host-bridge and composition tests so
/// they all exercise the same production wiring instead of a bespoke stub.
/// </summary>
internal sealed class ConversationInputTestHarness : IDisposable
{
    private readonly AgentActivityCoordinator _activity;
    private readonly TranscriptDelivery _delivery;
    private readonly TranscriptPublisher _publisher;
    private readonly ConversationSessionCoordinator _sessions;

    public ConversationInputTestHarness(IAgentChatRuntime? runtime = null,
        ConversationPersistenceFixture? fixture = null)
    {
        Fixture = fixture ?? new ConversationPersistenceFixture();
        Gated = runtime as GatedAgentChatRuntime;
        Runtime = runtime as ScriptedAgentChatRuntime ?? new ScriptedAgentChatRuntime();
        var activeRuntime = runtime ?? Runtime;
        Roots = new SqliteWorkspaceRepository(Fixture.Database);
        var settings = new DesktopSettingsJsonStore(Fixture.Paths);
        var approval = new DesktopToolApprovalHandler();
        var questions = new DesktopUserQuestionHandler();
        _activity = new AgentActivityCoordinator(approval, questions, NullLogger<AgentActivityCoordinator>.Instance);
        _delivery = new TranscriptDelivery(new WebViewHostChannel(), System.Windows.Threading.Dispatcher.CurrentDispatcher);
        _publisher = new TranscriptPublisher(new TranscriptProjection(Fixture.Paths), _delivery, System.Windows.Threading.Dispatcher.CurrentDispatcher);
        Agents = new AgentSettingsService(new DesktopAgentDefinitionService(Fixture.Paths),
            new SubagentDefinitionCatalog(Fixture.Paths), new EmptyExtensionSettingsService(),
            new ExtensionStateChangeNotifier());
        Changes = new ConversationInputChangeNotifier();
        Runs = new ConversationRunCoordinator(Fixture.Inputs, Fixture.Inputs, Changes, NullLogger<ConversationRunCoordinator>.Instance);
        _sessions = new ConversationSessionCoordinator(Fixture.Conversations, Fixture.Turns, new EmptyConversationCompactionRepository(), Runs, _publisher);
        Engine = new ConversationTurnEngine(Fixture.Turns,
            new DesktopTurnFinalizer(Fixture.Turns, NullLogger<DesktopTurnFinalizer>.Instance),
            new ConversationTurnRecorder(Fixture.Conversations, Fixture.Turns, Fixture.Inputs, NullLogger<ConversationTurnRecorder>.Instance),
            activeRuntime, _sessions, Runs, _activity, approval, questions, ProgrammingSettingsTestFactory.Create(settings),
            new ConversationCompletionNotifier(new DesktopNotificationService(NullLogger<DesktopNotificationService>.Instance)),
            NullLogger<ConversationTurnEngine>.Instance);
        var validator = new ConversationInputExecutionValidator(Roots, Models, Agents);
        var recovery = new SqliteStartupRecovery(Fixture.Database, Fixture.RecoveryBeforeCommit);
        Dispatcher = new ConversationInputDispatcher(Fixture.Inputs, Fixture.Inputs, Fixture.Conversations,
            Runs, Engine, validator, Changes, Fixture.Paths, NullLogger<ConversationInputDispatcher>.Instance, recovery.RecoverAsync);
        Queue = new ConversationInputQueueService(Fixture.Inputs, Fixture.Inputs, Fixture.Conversations, validator,
            Dispatcher, Runs, Agents, Changes);
        Service = new ConversationInputService(Fixture.Inputs, Fixture.Inputs, Queue, Dispatcher, Runs, Models, Agents, Changes,
            Fixture.Paths, NullLogger<ConversationInputService>.Instance);
    }

    public ConversationPersistenceFixture Fixture { get; }
    public GatedAgentChatRuntime? Gated { get; }
    public ScriptedAgentChatRuntime Runtime { get; }
    public SqliteWorkspaceRepository Roots { get; }
    public FakeModelCatalog Models { get; } = new();
    public AgentSettingsService Agents { get; }
    public ConversationRunCoordinator Runs { get; }
    public ConversationTurnEngine Engine { get; }
    public ConversationInputChangeNotifier Changes { get; }
    public ConversationInputDispatcher Dispatcher { get; }
    public ConversationInputQueueService Queue { get; }
    public ConversationInputService Service { get; }

    public async Task<Guid> CreateConversationAsync(bool recover = true)
    {
        if (recover) await Dispatcher.RecoverOnceAsync(CancellationToken.None);
        return (await Fixture.CreateConversationAsync()).Id;
    }

    public ConversationInputExecutionSnapshot Snapshot()
        => ConversationInputSnapshots.Capture(Agents, "build", Models.DefaultModel, null, null, ToolPermissionMode.RequireApproval);

    public ConversationInputSubmission Submission(Guid conversationId, string prompt)
        => new(conversationId, Guid.NewGuid().ToString("N"), prompt,
            new AgentRuntimeDefinition("build", "Builder", "test", AgentExecutionMode.Direct,
                AgentRuntimeDefinition.SystemToolPolicy, [], [], [], [], ""),
            Models.DefaultModel, null, ToolPermissionMode.RequireApproval, null);

    public Task<ConversationInputSubmitResult> SubmitAsync(Guid conversationId, string prompt)
        => Service.SubmitAsync(Submission(conversationId, prompt));

    public Task<ConversationInputStartAttempt> StartAsync(Guid conversationId)
        => Dispatcher.TryStartConversationAsync(conversationId, CancellationToken.None);

    /// <summary>Persists a Running turn with the reserved handle's identity so target validation can succeed.</summary>
    public async Task StartReservedTurnAsync(Guid conversationId, Guid turnId)
    {
        var conversation = await Fixture.Conversations.GetConversationAsync(conversationId)
            ?? throw new InvalidOperationException("The conversation must exist before its turn starts.");
        var turn = new ConversationTurnRecord(turnId, conversationId, AgentExecutionMode.Direct,
            DirectTurnOrigin.Interactive, ConversationTurnStatus.Running, DateTimeOffset.UtcNow);
        await Fixture.Turns.StartTurnAsync(new ConversationTurnStart(conversation, turn, "headless", Guid.NewGuid()));
    }

    public async Task WaitForAsync(Func<Task<bool>> predicate)
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            if (await predicate()) return;
            await Task.Delay(10);
        }

        throw new TimeoutException("The dispatched queue did not reach the expected state.");
    }

    public void Dispose()
    {
        Runs.StopAdmissions();
        var stopDispatcher = Dispatcher.StopAsync(CancellationToken.None);
        Runs.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        stopDispatcher.GetAwaiter().GetResult();
        _sessions.Dispose();
        _publisher.Dispose();
        _delivery.Dispose();
        _activity.Dispose();
        Fixture.Dispose();
    }
}

internal sealed class FakeModelCatalog : IAiModelCatalog
{
    public Guid DefaultModel { get; set; } = Guid.NewGuid();
    public Func<CancellationToken, Task<Guid?>>? OnDefaultRead { get; set; }
    public HashSet<Guid> Available { get; } = [];
    public HashSet<Guid> Multimodal { get; } = [];
    public FakeModelCatalog() => Available.Add(DefaultModel);
    public Task<Guid?> GetDefaultModelAsync(string scope, CancellationToken cancellationToken = default)
        => OnDefaultRead?.Invoke(cancellationToken) ?? Task.FromResult<Guid?>(DefaultModel);
    public Task<IReadOnlyList<EnabledModelView>> ListEnabledModelsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<EnabledModelView>>([]);
    public Task<bool> IsModelAvailableAsync(Guid modelProfileId, CancellationToken cancellationToken = default) => Task.FromResult(Available.Contains(modelProfileId));
    public Task<bool> IsModelMultimodalAsync(Guid modelProfileId, CancellationToken cancellationToken = default) => Task.FromResult(Multimodal.Contains(modelProfileId));
}

internal sealed class ScriptedAgentChatRuntime : IAgentChatRuntime
{
    public HashSet<string> FailPrompts { get; } = [];
    public Queue<RunCompletionStatus> Outcomes { get; } = new();
    public List<string> Prompts { get; } = [];

    public async IAsyncEnumerable<AgentStreamEvent> StreamTurnAsync(ChatTurnRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var prompt = request.Messages.Where(message => message.Role == MessageRole.User).LastOrDefault()?.MarkdownContent ?? string.Empty;
        lock (Prompts) Prompts.Add(prompt);
        var status = FailPrompts.Contains(prompt) ? RunCompletionStatus.Failed
            : Outcomes.Count > 0 ? Outcomes.Dequeue() : RunCompletionStatus.Succeeded;
        await Task.Yield();
        yield return status switch
        {
            RunCompletionStatus.Succeeded => new RunCompletedEvent(status, "done"),
            RunCompletionStatus.Truncated => new RunCompletedEvent(status, "partial", "length"),
            _ => new RunCompletedEvent(status, null, "provider failed"),
        };
    }
}

internal sealed class GatedAgentChatRuntime : IAgentChatRuntime
{
    private readonly object _gate = new();
    private readonly Dictionary<string, TaskCompletionSource> _gates = [];
    private readonly Dictionary<string, TaskCompletionSource> _entered = [];
    public List<string> Prompts { get; } = [];

    public void Release(string prompt)
    {
        lock (_gate) GateLocked(prompt).TrySetResult();
    }

    public Task EnteredAsync(string prompt)
    {
        lock (_gate)
        {
            if (!_entered.TryGetValue(prompt, out var entered))
            {
                entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _entered[prompt] = entered;
            }

            return entered.Task;
        }
    }

    public async IAsyncEnumerable<AgentStreamEvent> StreamTurnAsync(ChatTurnRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var prompt = request.Messages.Where(message => message.Role == MessageRole.User).LastOrDefault()?.MarkdownContent ?? string.Empty;
        TaskCompletionSource gate;
        lock (_gate)
        {
            Prompts.Add(prompt);
            gate = GateLocked(prompt);
            if (!_entered.TryGetValue(prompt, out var entered))
            {
                entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _entered[prompt] = entered;
            }

            entered.TrySetResult();
        }

        await gate.Task.WaitAsync(cancellationToken);
        yield return new RunCompletedEvent(RunCompletionStatus.Succeeded, "done");
    }

    private TaskCompletionSource GateLocked(string prompt)
    {
        if (!_gates.TryGetValue(prompt, out var gate))
        {
            gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _gates[prompt] = gate;
        }

        return gate;
    }
}
