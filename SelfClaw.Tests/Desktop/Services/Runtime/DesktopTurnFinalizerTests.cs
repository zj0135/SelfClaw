using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Desktop.Services.Runtime;

namespace SelfClaw.Tests.Desktop.Services.Runtime;

public sealed class DesktopTurnFinalizerTests
{
    [Fact]
    public async Task TryCommitAsync_persists_the_recorded_finalization()
    {
        var repository = new RecordingRepository();
        var finalization = CreateFinalization();
        (await CommitAsync(CreateFinalizer(repository), finalization)).Should().BeTrue();
        repository.Calls.Should().ContainSingle().Which.Should().BeSameAs(finalization);
    }

    [Fact]
    public async Task TryCommitAsync_retries_one_transient_failure()
    {
        var repository = new RecordingRepository(failuresBeforeSuccess: 1);
        (await CommitAsync(CreateFinalizer(repository), CreateFinalization())).Should().BeTrue();
        repository.Attempts.Should().Be(2);
    }

    [Fact]
    public async Task TryCommitAsync_leaves_repeated_finalization_to_the_atomic_repository_guard()
    {
        var repository = new RecordingRepository(results: [true, false]);
        var finalizer = CreateFinalizer(repository);
        var finalization = CreateFinalization();
        (await CommitAsync(finalizer, finalization)).Should().BeTrue();
        (await CommitAsync(finalizer, finalization)).Should().BeFalse();
        repository.Calls.Should().HaveCount(2);
    }

    [Fact]
    public async Task TryCommitAsync_propagates_cancellation_without_retrying_it_as_a_transient_failure()
    {
        var repository = new RecordingRepository(cancel: true);
        await FluentActions.Awaiting(() => CommitAsync(CreateFinalizer(repository), CreateFinalization()))
            .Should().ThrowAsync<OperationCanceledException>();
        repository.Attempts.Should().Be(1);
    }

    private static DesktopTurnFinalizer CreateFinalizer(IConversationTurnRepository repository)
        => new(repository, NullLogger<DesktopTurnFinalizer>.Instance);

    private static Task<bool> CommitAsync(DesktopTurnFinalizer finalizer, ConversationTurnCommit finalization)
        => finalizer.TryCommitAsync(new RecordedTurnCommit(finalization, TurnFinalizationKind.Succeeded,
            finalization.Messages.LastOrDefault()?.MarkdownContent, null));

    private static ConversationTurnCommit CreateFinalization()
    {
        var now = DateTimeOffset.UtcNow;
        var conversationId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var assistant = new MessageRecord(messageId, conversationId, turnId, 2, MessageRole.Assistant,
            "done", MessageStatus.Sealed, now, now,
            Segments: [new MessageSegmentRecord(messageId, 0, MessageSegmentKind.Text, "done", null)]);
        return new ConversationTurnCommit(new ConversationTurnRecord(turnId, conversationId, AgentExecutionMode.Direct,
            DirectTurnOrigin.Interactive, ConversationTurnStatus.Succeeded, now, now), [assistant], []);
    }

    private sealed class RecordingRepository : IConversationTurnRepository
    {
        private readonly Queue<bool> _results;
        private readonly bool _cancel;
        private int _failuresBeforeSuccess;

        public RecordingRepository(IEnumerable<bool>? results = null, int failuresBeforeSuccess = 0, bool cancel = false)
        {
            _results = new Queue<bool>(results ?? [true]);
            _failuresBeforeSuccess = failuresBeforeSuccess;
            _cancel = cancel;
        }

        public int Attempts { get; private set; }
        public List<ConversationTurnCommit> Calls { get; } = [];

        public Task<bool> TryFinalizeTurnAsync(ConversationTurnCommit finalization, CancellationToken cancellationToken = default)
        {
            Attempts++;
            if (_cancel) return Task.FromException<bool>(new OperationCanceledException());
            if (_failuresBeforeSuccess-- > 0)
                return Task.FromException<bool>(new InvalidOperationException("transient"));
            Calls.Add(finalization);
            return Task.FromResult(_results.Count == 0 || _results.Dequeue());
        }

        public Task<ConversationTurnCommit> StartTurnAsync(ConversationTurnStart start, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<long> ReserveMessageSequenceAsync(Guid conversationId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<ConversationTurnRecord>> ListTurnsAsync(Guid conversationId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task CommitProgressAsync(ConversationTurnCommit progress, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
