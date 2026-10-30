using System.Collections.Concurrent;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Runtime;
using SelfClaw.Desktop.Services.Questions.Models;

namespace SelfClaw.Desktop.Services.Questions;

public sealed class DesktopUserQuestionHandler : IUserQuestionHandler
{
    private readonly ConcurrentDictionary<Guid, PendingUserQuestion> _pendingQuestions = new();

    public event Action<UserQuestionRequest>? QuestionAsked;

    /// <summary>
    /// Raised whenever a pending question leaves the queue for any reason (answered, cancelled, or dismissed
    /// in bulk). Lets the UI drop a single dock entry without caring how the request was closed.
    /// </summary>
    public event Action<Guid>? QuestionClosed;

    internal UserQuestionRequest? GetPendingRequest(Guid requestId)
        => _pendingQuestions.TryGetValue(requestId, out var pending) ? pending.Request : null;

    public Task<UserQuestionAnswer> RequestAnswerAsync(
        UserQuestionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var completionSource = new TaskCompletionSource<UserQuestionAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = default;

        if (cancellationToken.CanBeCanceled)
        {
            registration = cancellationToken.Register(() =>
            {
                if (_pendingQuestions.TryRemove(request.RequestId, out var pending))
                {
                    pending.CompletionSource.TrySetCanceled(cancellationToken);
                    DisposeRegistrations(pending);
                    RaiseQuestionClosed(request.RequestId);
                }
            });
        }

        var pendingQuestion = new PendingUserQuestion(request, completionSource, registration);
        if (!_pendingQuestions.TryAdd(request.RequestId, pendingQuestion))
        {
            registration.Dispose();
            throw new InvalidOperationException($"A question for '{request.RequestId}' is already pending.");
        }

        if (cancellationToken.IsCancellationRequested &&
            _pendingQuestions.TryRemove(request.RequestId, out var canceledPending))
        {
            DisposeRegistrations(canceledPending);
            canceledPending.CompletionSource.TrySetCanceled(cancellationToken);
            return canceledPending.CompletionSource.Task;
        }

        try
        {
            QuestionAsked?.Invoke(request);
        }
        catch
        {
            // A presenter that cannot render the dock must not leave the turn blocked forever, so the batch
            // settles as skipped instead of throwing into a call that is already waiting.
            TryAnswer(request.RequestId, SkippedAnswer(request.RequestId));
        }

        return AwaitAnswerAsync(pendingQuestion);
    }

    public bool TryAnswer(Guid requestId, UserQuestionAnswer answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        if (!_pendingQuestions.TryRemove(requestId, out var pending))
        {
            return false;
        }

        DisposeRegistrations(pending);
        var resolved = pending.CompletionSource.TrySetResult(answer);
        RaiseQuestionClosed(requestId);
        return resolved;
    }

    public void DismissAll()
    {
        foreach (var requestId in _pendingQuestions.Keys)
        {
            TryAnswer(requestId, SkippedAnswer(requestId));
        }
    }

    private static UserQuestionAnswer SkippedAnswer(Guid requestId)
        => new(requestId, [], Skipped: true);

    private void RaiseQuestionClosed(Guid requestId)
    {
        try
        {
            QuestionClosed?.Invoke(requestId);
        }
        catch
        {
        }
    }

    private static async Task<UserQuestionAnswer> AwaitAnswerAsync(PendingUserQuestion pendingQuestion)
    {
        try
        {
            return await pendingQuestion.CompletionSource.Task;
        }
        finally
        {
            DisposeRegistrations(pendingQuestion);
        }
    }

    private static void DisposeRegistrations(PendingUserQuestion pending)
        => pending.CancellationRegistration.Dispose();
}
