using FluentAssertions;
using SelfClaw.Core.Runtime;
using SelfClaw.Desktop.Services.Questions;

namespace SelfClaw.Tests.Desktop.Services.Questions;

public sealed class DesktopUserQuestionHandlerTests
{
    [Fact]
    public async Task RequestAnswerAsync_completes_with_the_batch_answer_and_refuses_a_second_one()
    {
        var handler = new DesktopUserQuestionHandler();
        var request = CreateRequest();
        UserQuestionRequest? published = null;
        var closed = new List<Guid>();
        handler.QuestionAsked += value => published = value;
        handler.QuestionClosed += closed.Add;

        var pending = handler.RequestAnswerAsync(request);
        var answer = new UserQuestionAnswer(request.RequestId,
            [new UserQuestionPromptAnswer(request.Prompts[0].PromptId, ["PostgreSQL"], "Neon", Skipped: false)],
            Skipped: false);

        published.Should().Be(request);
        handler.TryAnswer(request.RequestId, answer).Should().BeTrue();

        (await pending).Should().Be(answer);
        handler.TryAnswer(request.RequestId, answer).Should().BeFalse();
        closed.Should().Equal(request.RequestId);
        handler.GetPendingRequest(request.RequestId).Should().BeNull();
    }

    [Fact]
    public async Task RequestAnswerAsync_cancellation_removes_the_pending_item()
    {
        var handler = new DesktopUserQuestionHandler();
        using var cancellationSource = new CancellationTokenSource();
        var request = CreateRequest();
        var closed = new List<Guid>();
        handler.QuestionClosed += closed.Add;
        var pending = handler.RequestAnswerAsync(request, cancellationSource.Token);

        await cancellationSource.CancelAsync();

        Func<Task> awaitPending = async () => await pending;
        await awaitPending.Should().ThrowAsync<OperationCanceledException>();
        handler.TryAnswer(request.RequestId, Skip(request.RequestId)).Should().BeFalse();
        handler.GetPendingRequest(request.RequestId).Should().BeNull();
        closed.Should().Equal(request.RequestId);
    }

    [Fact]
    public async Task DismissAll_settles_every_pending_batch_as_skipped()
    {
        var handler = new DesktopUserQuestionHandler();
        var first = CreateRequest();
        var second = CreateRequest();
        var firstPending = handler.RequestAnswerAsync(first);
        var secondPending = handler.RequestAnswerAsync(second);

        handler.DismissAll();

        (await firstPending).Skipped.Should().BeTrue();
        (await secondPending).Skipped.Should().BeTrue();
        (await firstPending).RequestId.Should().Be(first.RequestId);
        handler.GetPendingRequest(first.RequestId).Should().BeNull();
    }

    [Fact]
    public async Task RequestAnswerAsync_subscriber_failure_settles_as_skipped()
    {
        var handler = new DesktopUserQuestionHandler();
        var request = CreateRequest();
        handler.QuestionAsked += _ => throw new InvalidOperationException("dock failed");

        var answer = await handler.RequestAnswerAsync(request);

        answer.Skipped.Should().BeTrue();
        answer.RequestId.Should().Be(request.RequestId);
        handler.TryAnswer(request.RequestId, Skip(request.RequestId)).Should().BeFalse();
    }

    [Fact]
    public async Task RequestAnswerAsync_rejects_a_duplicate_request_id()
    {
        var handler = new DesktopUserQuestionHandler();
        var request = CreateRequest();
        var first = handler.RequestAnswerAsync(request);

        Action duplicate = () => handler.RequestAnswerAsync(request);
        duplicate.Should().Throw<InvalidOperationException>().WithMessage("*already pending*");

        handler.TryAnswer(request.RequestId, Skip(request.RequestId)).Should().BeTrue();
        (await first).Skipped.Should().BeTrue();
    }

    private static UserQuestionAnswer Skip(Guid requestId) => new(requestId, [], Skipped: true);

    private static UserQuestionRequest CreateRequest()
        => new(
            Guid.NewGuid(),
            [
                new UserQuestionPrompt(
                    Guid.NewGuid(),
                    "Which database should this service use?",
                    "Database",
                    [new UserQuestionOption("PostgreSQL", "Relational"), new UserQuestionOption("SQLite", "Embedded")],
                    MultiSelect: false)
            ],
            Guid.NewGuid(),
            Guid.NewGuid());
}
