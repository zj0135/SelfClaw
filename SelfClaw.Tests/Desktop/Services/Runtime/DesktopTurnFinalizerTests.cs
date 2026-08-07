using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services.Runtime;

namespace SelfClaw.Tests.Desktop.Services.Runtime;

public sealed class DesktopTurnFinalizerTests
{
    [Fact]
    public async Task TryCommitAsync_persists_the_recorded_finalization()
    {
        var repository = new RecordingRepository();
        var finalizer = CreateFinalizer(repository);
        var finalization = CreateFinalization();

        var written = await finalizer.TryCommitAsync(finalization);

        written.Should().BeTrue();
        repository.Calls.Should().ContainSingle().Which.Should().BeSameAs(finalization);
    }

    [Fact]
    public async Task TryCommitAsync_retries_one_transient_failure()
    {
        var repository = new RecordingRepository(failuresBeforeSuccess: 1);
        var finalizer = CreateFinalizer(repository);

        var written = await finalizer.TryCommitAsync(CreateFinalization());

        written.Should().BeTrue();
        repository.Attempts.Should().Be(2);
    }

    [Fact]
    public async Task TryCommitAsync_leaves_repeated_finalization_to_the_atomic_repository_guard()
    {
        var repository = new RecordingRepository(results: [true, false]);
        var finalizer = CreateFinalizer(repository);
        var finalization = CreateFinalization();

        var first = await finalizer.TryCommitAsync(finalization);
        var second = await finalizer.TryCommitAsync(finalization);

        first.Should().BeTrue();
        second.Should().BeFalse();
        repository.Calls.Should().HaveCount(2);
    }

    private static DesktopTurnFinalizer CreateFinalizer(ITurnFinalizationRepository repository)
        => new(repository, NullLogger<DesktopTurnFinalizer>.Instance);

    private static TurnFinalization CreateFinalization()
    {
        var now = DateTimeOffset.UtcNow;
        var conversationId = Guid.NewGuid();
        var assistant = new MessageRecord(
            Guid.NewGuid(),
            conversationId,
            MessageRole.Assistant,
            "done",
            MessageStatus.Completed,
            now,
            now);
        var tool = new ToolExecutionRecord(
            Guid.NewGuid(),
            conversationId,
            "read_file",
            "{}",
            ToolExecutionStatus.Completed,
            ResultSummary: "done",
            CorrelationId: "call-1",
            DurationMs: 1,
            now,
            now,
            MessageId: assistant.Id);
        return new TurnFinalization(assistant, [tool]);
    }

    private sealed class RecordingRepository : ITurnFinalizationRepository
    {
        private readonly Queue<bool> _results;
        private int _failuresBeforeSuccess;

        public RecordingRepository(
            IEnumerable<bool>? results = null,
            int failuresBeforeSuccess = 0)
        {
            _results = new Queue<bool>(results ?? [true]);
            _failuresBeforeSuccess = failuresBeforeSuccess;
        }

        public int Attempts { get; private set; }

        public List<TurnFinalization> Calls { get; } = [];

        public Task<bool> TryFinalizeTurnAsync(
            TurnFinalization finalization,
            CancellationToken cancellationToken = default)
        {
            Attempts++;
            if (_failuresBeforeSuccess > 0)
            {
                _failuresBeforeSuccess--;
                return Task.FromException<bool>(new InvalidOperationException("transient"));
            }

            Calls.Add(finalization);
            return Task.FromResult(_results.Count == 0 || _results.Dequeue());
        }
    }
}
