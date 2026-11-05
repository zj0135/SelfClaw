using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Desktop.Services.Runtime;
using SelfClaw.Tests.TestDoubles;

namespace SelfClaw.Tests.Desktop.Services.Runtime;

public sealed class ConversationTitleServiceTests
{
    [Fact]
    public async Task Writes_the_generated_title_and_keeps_updated_at_unchanged()
    {
        using var fixture = new ConversationPersistenceFixture();
        await fixture.Conversations.InitializeAsync();
        var conversation = await fixture.CreateConversationAsync("prompt");
        var generator = new FakeTitleGenerator { Title = "Fix login redirect" };
        await using var service = CreateService(fixture, generator);
        var changed = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.TitleChanged += id => changed.TrySetResult(id);

        service.TryStart(new ConversationTitleStart(conversation.Id, "prompt", "the login keeps redirecting", null));

        (await changed.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(conversation.Id);
        generator.Calls.Should().ContainSingle().Which.Prompt.Should().Be("the login keeps redirecting");
        var reloaded = await fixture.Conversations.GetConversationAsync(conversation.Id);
        reloaded.Should().NotBeNull();
        reloaded!.Title.Should().Be("Fix login redirect");
        reloaded.UpdatedAtUtc.Should().Be(conversation.UpdatedAtUtc,
            "a title change is not conversation activity; sidebar ordering and the archive cutoff must not move");
    }

    [Fact]
    public async Task Skips_the_write_when_the_title_was_renamed_meanwhile()
    {
        using var fixture = new ConversationPersistenceFixture();
        await fixture.Conversations.InitializeAsync();
        var conversation = await fixture.CreateConversationAsync("prompt");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var generator = new FakeTitleGenerator
        {
            OnGenerate = async (_, _, token) =>
            {
                entered.SetResult();
                await release.Task.WaitAsync(token);
                return "Generated title";
            }
        };
        await using var service = CreateService(fixture, generator);
        var changed = false;
        service.TitleChanged += _ => changed = true;

        service.TryStart(new ConversationTitleStart(conversation.Id, "prompt", "prompt", null));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Conversations.UpsertConversationAsync(conversation with { Title = "Manual title" });
        release.SetResult();
        await service.WhenIdleAsync();

        changed.Should().BeFalse();
        (await fixture.Conversations.GetConversationAsync(conversation.Id))!.Title.Should().Be("Manual title");
    }

    [Fact]
    public async Task Skips_the_write_when_the_conversation_was_deleted_meanwhile()
    {
        using var fixture = new ConversationPersistenceFixture();
        await fixture.Conversations.InitializeAsync();
        var conversation = await fixture.CreateConversationAsync("prompt");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var generator = new FakeTitleGenerator
        {
            OnGenerate = async (_, _, token) =>
            {
                entered.SetResult();
                await release.Task.WaitAsync(token);
                return "Generated title";
            }
        };
        await using var service = CreateService(fixture, generator);
        var changed = false;
        service.TitleChanged += _ => changed = true;

        service.TryStart(new ConversationTitleStart(conversation.Id, "prompt", "prompt", null));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Conversations.DeleteConversationAsync(conversation.Id);
        release.SetResult();
        await service.WhenIdleAsync();

        changed.Should().BeFalse();
        (await fixture.Conversations.GetConversationAsync(conversation.Id)).Should().BeNull("a late title must not resurrect a deleted conversation");
        (await fixture.Conversations.ListConversationsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Keeps_quiet_when_generation_fails_or_returns_nothing()
    {
        using var fixture = new ConversationPersistenceFixture();
        await fixture.Conversations.InitializeAsync();
        var failing = await fixture.CreateConversationAsync("prompt");
        var blank = await fixture.CreateConversationAsync("blank prompt");
        await using var service = CreateService(fixture, new FakeTitleGenerator
        {
            OnGenerate = (prompt, _, _) => prompt == "prompt"
                ? Task.FromException<string?>(new InvalidOperationException("provider exploded"))
                : Task.FromResult<string?>("   ")
        });
        var changed = false;
        service.TitleChanged += _ => changed = true;

        service.TryStart(new ConversationTitleStart(failing.Id, "prompt", "prompt", null));
        service.TryStart(new ConversationTitleStart(blank.Id, "blank prompt", "prompt", null));
        await service.WhenIdleAsync();

        changed.Should().BeFalse();
        (await fixture.Conversations.GetConversationAsync(failing.Id))!.Title.Should().Be("prompt");
        (await fixture.Conversations.GetConversationAsync(blank.Id))!.Title.Should().Be("blank prompt");
    }

    [Fact]
    public async Task Dispose_cancels_an_in_flight_generation_and_waits_for_it()
    {
        using var fixture = new ConversationPersistenceFixture();
        await fixture.Conversations.InitializeAsync();
        var conversation = await fixture.CreateConversationAsync("prompt");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var generator = new FakeTitleGenerator
        {
            OnGenerate = async (_, _, token) =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return "never";
            }
        };
        var service = CreateService(fixture, generator);

        service.TryStart(new ConversationTitleStart(conversation.Id, "prompt", "prompt", null));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        (await fixture.Conversations.GetConversationAsync(conversation.Id))!.Title.Should().Be("prompt");

        service.TryStart(new ConversationTitleStart(conversation.Id, "prompt", "prompt", null));
        await service.WhenIdleAsync();
        generator.Calls.Should().ContainSingle("a disposed service must not accept new work");
    }

    private static ConversationTitleService CreateService(ConversationPersistenceFixture fixture, IConversationTitleGenerator generator)
        => new(generator, fixture.Conversations, NullLogger<ConversationTitleService>.Instance);

    private sealed class FakeTitleGenerator : IConversationTitleGenerator
    {
        public string? Title { get; init; } = "Titled";

        public Func<string, Guid?, CancellationToken, Task<string?>>? OnGenerate { get; init; }

        public List<(string Prompt, Guid? ModelProfileId)> Calls { get; } = [];

        public Task<string?> GenerateTitleAsync(string prompt, Guid? modelProfileId, CancellationToken cancellationToken = default)
        {
            Calls.Add((prompt, modelProfileId));
            return OnGenerate?.Invoke(prompt, modelProfileId, cancellationToken)
                ?? Task.FromResult(Title);
        }
    }
}
