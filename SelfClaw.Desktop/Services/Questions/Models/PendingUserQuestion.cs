using SelfClaw.Core.Runtime;

namespace SelfClaw.Desktop.Services.Questions.Models;

internal sealed record PendingUserQuestion(
    UserQuestionRequest Request,
    TaskCompletionSource<UserQuestionAnswer> CompletionSource,
    CancellationTokenRegistration CancellationRegistration);
