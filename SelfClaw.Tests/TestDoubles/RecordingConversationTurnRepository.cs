using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;

namespace SelfClaw.Tests.TestDoubles;

internal sealed class RecordingConversationTurnRepository(IConversationRepository? conversations = null) : IConversationTurnRepository
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, ConversationTurnRecord> _turns = [];
    private readonly Dictionary<Guid, long> _sequences = [];
    public List<ConversationTurnStart> Starts { get; } = [];
    public List<ConversationTurnCommit> Progress { get; } = [];
    public List<ConversationTurnCommit> Finalizations { get; } = [];
    public Func<ConversationTurnStart, CancellationToken, Task>? BeforeStart { get; set; }
    public Func<ConversationTurnCommit, CancellationToken, Task>? BeforeFinalize { get; set; }
    public Exception? FinalizationFailure { get; set; }
    public int FinalizationAttempts { get; private set; }

    public async Task<ConversationTurnCommit> StartTurnAsync(ConversationTurnStart start, CancellationToken cancellationToken = default)
    {
        if (BeforeStart is { } before) await before(start, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (conversations is not null) await conversations.UpsertConversationAsync(start.Conversation, cancellationToken);
        var sequence = await ReserveMessageSequenceAsync(start.Conversation.Id, cancellationToken);
        lock (_gate)
        {
            Starts.Add(start);
            _turns.Add(start.Turn.Id, start.Turn);
        }
        return new(start.Turn, [new MessageRecord(start.UserMessageId, start.Conversation.Id, start.Turn.Id,
            sequence, MessageRole.User, start.Prompt, MessageStatus.Sealed, start.Turn.StartedAtUtc, start.Turn.StartedAtUtc,
            Attachments: start.Attachments)], []);
    }

    public Task<long> ReserveMessageSequenceAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var sequence = _sequences.GetValueOrDefault(conversationId) + 1;
            _sequences[conversationId] = sequence;
            return Task.FromResult(sequence);
        }
    }

    public Task<IReadOnlyList<ConversationTurnRecord>> ListTurnsAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<ConversationTurnRecord>>(_turns.Values.Where(turn => turn.ConversationId == conversationId).ToArray());
    }

    public Task CommitProgressAsync(ConversationTurnCommit progress, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            Progress.Add(progress);
            _turns[progress.Turn.Id] = progress.Turn;
        }
        return Task.CompletedTask;
    }

    public async Task<bool> TryFinalizeTurnAsync(ConversationTurnCommit commit, CancellationToken cancellationToken = default)
    {
        if (BeforeFinalize is { } before) await before(commit, cancellationToken);
        lock (_gate)
        {
            FinalizationAttempts++;
            if (FinalizationFailure is { } failure) throw failure;
            if (_turns.TryGetValue(commit.Turn.Id, out var stored) && stored.Status != ConversationTurnStatus.Running)
                return false;
            _turns[commit.Turn.Id] = commit.Turn;
            Finalizations.Add(commit);
            return true;
        }
    }
}
