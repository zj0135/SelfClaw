using SelfClaw.Core.Runtime;

namespace SelfClaw.Core.Interfaces;

public interface IUserQuestionHandler
{
    Task<UserQuestionAnswer> RequestAnswerAsync(
        UserQuestionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically takes a still-pending batch. Returns false when the request was already answered or
    /// cancelled, so a late click can never overwrite an answer the model has already seen.
    /// </summary>
    bool TryAnswer(Guid requestId, UserQuestionAnswer answer);
}
