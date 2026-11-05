using Microsoft.Extensions.Logging;
using SelfClaw.Core.Interfaces;
using SelfClaw.Desktop.Services.Runtime.Abstractions;

namespace SelfClaw.Desktop.Services.Runtime;

/// <summary>
/// Turns the first user message of a Direct conversation into a summarized title. The generation runs
/// detached from the turn: it never blocks, throws, or outlives shutdown, and it only ever replaces the
/// prompt-preview title it was started with, so a manual rename is never overwritten and a deleted
/// conversation is never resurrected.
/// </summary>
internal sealed class ConversationTitleService : IConversationTitleStarter, IAsyncDisposable
{
    /// <summary>Bounds the extra provider call so a stalled endpoint cannot keep the task (or shutdown) alive.</summary>
    private static readonly TimeSpan GenerationTimeout = TimeSpan.FromSeconds(20);

    private readonly IConversationTitleGenerator _generator;
    private readonly IConversationRepository _conversations;
    private readonly ILogger<ConversationTitleService> _logger;
    private readonly object _gate = new();
    private readonly HashSet<Task> _running = [];
    private readonly CancellationTokenSource _shutdown = new();
    private int _disposed;

    public ConversationTitleService(
        IConversationTitleGenerator generator,
        IConversationRepository conversations,
        ILogger<ConversationTitleService> logger)
    {
        _generator = generator;
        _conversations = conversations;
        _logger = logger;
    }

    /// <summary>Raised after a generated title was durably written.</summary>
    public event Action<Guid>? TitleChanged;

    public void TryStart(ConversationTitleStart start)
    {
        ArgumentNullException.ThrowIfNull(start);
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            var task = Task.Run(() => GenerateAsync(start));
            _running.Add(task);
            _ = task.ContinueWith(ObserveCompletion, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    /// <summary>Waits for the in-flight generations. Disposal cancels them first, so this settles promptly.</summary>
    internal Task WhenIdleAsync()
    {
        Task[] tasks;
        lock (_gate) tasks = _running.ToArray();
        return Task.WhenAll(tasks);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _shutdown.CancelAsync().ConfigureAwait(false);
        await WhenIdleAsync().ConfigureAwait(false);
        _shutdown.Dispose();
    }

    private async Task GenerateAsync(ConversationTitleStart start)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            timeout.CancelAfter(GenerationTimeout);
            var title = await _generator.GenerateTitleAsync(start.Prompt, start.ModelProfileId, timeout.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(title)) return;
            var current = await _conversations.GetConversationAsync(start.ConversationId).ConfigureAwait(false);
            // The write must not resurrect a deleted conversation, and it must not clobber a manual rename:
            // the compare-and-swap only passes while the title is still the prompt preview this run read.
            if (current is null || !string.Equals(current.Title, start.ExpectedTitle, StringComparison.Ordinal)) return;
            // Title-only write: UpdatedAtUtc rides along unchanged, because sidebar ordering and the archive
            // cutoff describe the conversation's real last activity, not this title change.
            await _conversations.UpsertConversationAsync(current with { Title = title }).ConfigureAwait(false);
            TitleChanged?.Invoke(start.ConversationId);
        }
        catch (OperationCanceledException)
        {
            // The timeout or shutdown cancelled the call: keeping the preview title is the answer.
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Conversation title generation failed for {ConversationId}; keeping the existing title.",
                start.ConversationId);
        }
    }

    private void ObserveCompletion(Task task)
    {
        // GenerateAsync absorbs every failure, so a fault here is a service bug; surface it anyway.
        if (task.Exception is { } error) _logger.LogError(error, "Conversation title task failed unexpectedly.");
        lock (_gate) _running.Remove(task);
    }
}
