using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services.Runtime;

namespace SelfClaw.Tests.Desktop.Services.Runtime;

public sealed class DesktopTurnFinalizerTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 22, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task FinalizeAsync_persists_cancelled_message_and_pending_tools_together()
    {
        var repository = new RecordingRepository();
        var finalizer = CreateFinalizer(repository);
        var request = CreateRequest(TurnFinalizationKind.Cancelled, "Generation stopped.");

        var finalization = await finalizer.FinalizeAsync(request);

        finalization.Should().NotBeNull();
        finalization!.AssistantMessage.Status.Should().Be(MessageStatus.Cancelled);
        finalization.AssistantMessage.MarkdownContent.Should().Be("partial");
        finalization.AssistantMessage.ErrorMessage.Should().Be("Generation stopped.");
        finalization.AssistantMessage.DurationMs.Should().Be(3000);
        finalization.ToolExecutions.Should().ContainSingle()
            .Which.Status.Should().Be(ToolExecutionStatus.Cancelled);
        repository.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task FinalizeAsync_marks_pending_tools_failed_for_failed_turn()
    {
        var repository = new RecordingRepository();
        var finalizer = CreateFinalizer(repository);

        var finalization = await finalizer.FinalizeAsync(
            CreateRequest(TurnFinalizationKind.Failed, "provider failed"));

        finalization!.AssistantMessage.Status.Should().Be(MessageStatus.Failed);
        finalization.ToolExecutions.Should().ContainSingle()
            .Which.Status.Should().Be(ToolExecutionStatus.Failed);
    }

    [Fact]
    public async Task FinalizeAsync_leaves_repeated_finalization_to_the_atomic_repository_guard()
    {
        var repository = new RecordingRepository([true, false]);
        var finalizer = CreateFinalizer(repository);
        var request = CreateRequest(TurnFinalizationKind.Succeeded, null, "final");

        var first = await finalizer.FinalizeAsync(request);
        var second = await finalizer.FinalizeAsync(request with
        {
            Kind = TurnFinalizationKind.Failed,
            ErrorMessage = "late failure"
        });

        first!.AssistantMessage.Status.Should().Be(MessageStatus.Completed);
        second.Should().BeNull();
        repository.Calls.Should().HaveCount(2);
    }

    private static DesktopTurnFinalizer CreateFinalizer(ITurnFinalizationRepository repository)
        => new(repository, new FixedTimeProvider(Now), NullLogger<DesktopTurnFinalizer>.Instance);

    private static DesktopTurnFinalizationRequest CreateRequest(
        TurnFinalizationKind kind,
        string? errorMessage,
        string? finalText = null)
    {
        var conversationId = Guid.NewGuid();
        var assistant = new MessageRecord(
            Guid.NewGuid(),
            conversationId,
            MessageRole.Assistant,
            "partial",
            MessageStatus.Streaming,
            Now.AddSeconds(-3),
            Now.AddSeconds(-1));
        var tool = new ToolExecutionRecord(
            Guid.NewGuid(),
            conversationId,
            "read_file",
            "{}",
            ToolExecutionStatus.Running,
            ResultSummary: null,
            CorrelationId: "call-1",
            DurationMs: null,
            Now.AddSeconds(-2),
            Now.AddSeconds(-2),
            MessageId: assistant.Id);

        return new DesktopTurnFinalizationRequest(
            assistant,
            [tool],
            kind,
            finalText,
            errorMessage,
            InputTokens: 12,
            OutputTokens: 4,
            StartedAtUtc: Now.AddSeconds(-3));
    }

    private sealed class RecordingRepository : ITurnFinalizationRepository
    {
        private readonly Queue<bool> _results;

        public RecordingRepository(IEnumerable<bool>? results = null)
        {
            _results = new Queue<bool>(results ?? [true]);
        }

        public List<TurnFinalization> Calls { get; } = [];

        public Task<bool> TryFinalizeTurnAsync(
            TurnFinalization finalization,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(finalization);
            return Task.FromResult(_results.Count == 0 || _results.Dequeue());
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTimeProvider(DateTimeOffset now)
        {
            _now = now;
        }

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
