using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.AI;
using SelfClaw.Infrastructure.AiProviders;

namespace SelfClaw.Tests.Infrastructure.AiProviders;

public sealed class ProviderStreamIdleGuardTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_healthy_stream_passes_through_unchanged()
    {
        var client = new StallingClient(updatesBeforeStall: 3, stall: false);

        var updates = await CollectAsync(client, Generous, CancellationToken.None);

        updates.Select(update => update.Text).Should().Equal("0", "1", "2");
    }

    [Fact]
    public async Task A_stream_that_goes_silent_fails_with_a_timeout_instead_of_hanging()
    {
        var client = new StallingClient(updatesBeforeStall: 1, stall: true);

        var action = () => CollectAsync(client, TimeSpan.FromMilliseconds(50), CancellationToken.None);

        // A timeout, never a cancellation: the turn must end as failed, not as stopped by the user.
        (await action.Should().ThrowAsync<TimeoutException>()).Which.Message.Should().Contain("no data");
        client.ObservedCancellation.Should().BeTrue("the abandoned request has to be torn down");
    }

    [Fact]
    public async Task The_callers_own_cancellation_stays_a_cancellation()
    {
        var client = new StallingClient(updatesBeforeStall: 1, stall: true);
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var action = () => CollectAsync(client, Generous, stop.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Time_the_consumer_spends_between_updates_is_not_provider_silence()
    {
        var client = new StallingClient(updatesBeforeStall: 2, stall: false);
        var seen = 0;

        await foreach (var _ in ProviderStreamIdleGuard.StreamAsync(
                           client, [], null, TimeSpan.FromMilliseconds(100), CancellationToken.None))
        {
            seen++;
            await Task.Delay(TimeSpan.FromMilliseconds(300));
        }

        seen.Should().Be(2);
    }

    private static async Task<List<ChatResponseUpdate>> CollectAsync(
        IChatClient client, TimeSpan idleTimeout, CancellationToken cancellationToken)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in ProviderStreamIdleGuard.StreamAsync(client, [], null, idleTimeout, cancellationToken))
        {
            updates.Add(update);
        }

        return updates;
    }

    private sealed class StallingClient(int updatesBeforeStall, bool stall) : IChatClient
    {
        internal bool ObservedCancellation { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            for (var index = 0; index < updatesBeforeStall; index++)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, index.ToString());
            }

            if (!stall)
            {
                yield break;
            }

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                ObservedCancellation = cancellationToken.IsCancellationRequested;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
