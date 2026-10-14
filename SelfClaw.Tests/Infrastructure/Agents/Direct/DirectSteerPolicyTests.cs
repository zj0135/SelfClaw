using FluentAssertions;
using Microsoft.Extensions.AI;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Agents.Direct;
using SelfClaw.Infrastructure.Agents.Direct.Abstractions;
using SelfClaw.Infrastructure.Agents.Direct.Capabilities;

namespace SelfClaw.Tests.Infrastructure.Agents.Direct;

/// <summary>
/// P4.6: the runtime publishes the steer policy before the request starts, so a steer can never
/// re-run an explicit Skill activation.
/// </summary>
public sealed class DirectSteerPolicyTests
{
    [Fact]
    public async Task Explicit_skill_activation_blocks_steering()
    {
        var steering = new RecordingSteering();
        var lease = new DirectTurnCapabilityLease([], [], new Dictionary<Guid, string>(), [], explicitSkillIds: ["pdf"]);
        var provider = new OneShotChatClient();
        var factory = new DirectAgentChatRuntimeTests.FakeChatClientFactory(provider);
        var runtime = DirectAgentChatRuntimeTests.CreateRuntime(factory, new FixedResolver(lease));

        var terminal = await RunAsync(runtime, factory.Profile.Id, steering);

        terminal.Status.Should().Be(RunCompletionStatus.Succeeded);
        steering.Policies.Should().Equal(ConversationInputReason.SkillRequiresNewTurn);
    }

    [Fact]
    public async Task A_plain_turn_publishes_an_allowing_policy()
    {
        var steering = new RecordingSteering();
        var lease = new DirectTurnCapabilityLease([], [], new Dictionary<Guid, string>(), []);
        var provider = new OneShotChatClient();
        var factory = new DirectAgentChatRuntimeTests.FakeChatClientFactory(provider);
        var runtime = DirectAgentChatRuntimeTests.CreateRuntime(factory, new FixedResolver(lease));

        var terminal = await RunAsync(runtime, factory.Profile.Id, steering);

        terminal.Status.Should().Be(RunCompletionStatus.Succeeded);
        steering.Policies.Should().ContainSingle().Which.Should().BeNull();
    }

    [Fact]
    public async Task A_non_interactive_turn_cannot_carry_steering()
    {
        var steering = new RecordingSteering();
        var lease = new DirectTurnCapabilityLease([], [], new Dictionary<Guid, string>(), []);
        var provider = new OneShotChatClient();
        var factory = new DirectAgentChatRuntimeTests.FakeChatClientFactory(provider);
        var runtime = DirectAgentChatRuntimeTests.CreateRuntime(factory, new FixedResolver(lease));
        var request = (DirectChatTurnRequest)DirectAgentChatRuntimeTests.CreateRequest(factory.Profile.Id) with
        {
            ExecutionContext = new DirectTurnExecutionContext(DirectTurnOrigin.Continuation, null, null),
            Steering = steering
        };

        RunCompletedEvent? terminal = null;
        await foreach (var item in runtime.StreamTurnAsync(request))
        {
            if (item is RunCompletedEvent completed) terminal = completed;
        }

        terminal.Should().NotBeNull();
        terminal!.Status.Should().Be(RunCompletionStatus.Failed);
        terminal.ErrorMessage.Should().Contain("Interactive");
        steering.Policies.Should().BeEmpty();
    }

    private static async Task<RunCompletedEvent> RunAsync(DirectAgentChatRuntime runtime, Guid modelProfileId,
        RecordingSteering steering)
    {
        var request = (DirectChatTurnRequest)DirectAgentChatRuntimeTests.CreateRequest(modelProfileId) with
        {
            Steering = steering
        };
        RunCompletedEvent? terminal = null;
        await foreach (var item in runtime.StreamTurnAsync(request))
        {
            if (item is RunCompletedEvent completed) terminal = completed;
        }

        return terminal ?? throw new InvalidOperationException("Missing terminal event.");
    }

    private sealed class FixedResolver(DirectTurnCapabilityLease lease) : IDirectTurnCapabilityResolver
    {
        public Task<DirectTurnCapabilityLease> ResolveAsync(DirectChatTurnRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(lease);
    }

    private sealed class RecordingSteering : IDirectTurnSteering
    {
        public List<string?> Policies { get; } = [];
        public string? SteerBlockReason => Policies.LastOrDefault();
        public bool HasSupersedingSteer => false;

        public Task<bool> TryBeginToolExecutionAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task WaitForSteerPulseAsync(CancellationToken cancellationToken = default)
            => Task.Delay(Timeout.Infinite, cancellationToken);

        public Task SetSteerPolicyAsync(string? blockReason, CancellationToken cancellationToken = default)
        {
            lock (Policies) Policies.Add(blockReason);
            return Task.CompletedTask;
        }
    }

    private sealed class OneShotChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("done")])
            { FinishReason = ChatFinishReason.Stop };
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
