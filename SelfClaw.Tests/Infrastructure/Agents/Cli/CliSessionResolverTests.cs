using FluentAssertions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Agents.Cli.Definitions;
using SelfClaw.Infrastructure.Agents.Cli.Session;

namespace SelfClaw.Tests.Infrastructure.Agents.Cli;

public sealed class CliSessionResolverTests
{
    private readonly FakeSessionStore _store = new();
    private readonly CliSessionResolver _resolver;

    public CliSessionResolverTests()
    {
        _resolver = new CliSessionResolver(_store);
    }

    [Fact]
    public async Task Specified_fresh_conversation_mints_id_without_persisting()
    {
        var conversationId = Guid.NewGuid();

        var plan = await _resolver.PrepareAsync(conversationId, ClaudeAgentDefinition.Create());

        plan.ResumeSessionId.Should().BeNull();
        plan.NewSessionId.Should().NotBeNullOrEmpty();
        // The id must not be stored until the stream confirms the session exists; otherwise a turn
        // that never starts leaves a dead id behind and every later turn resumes a missing session.
        _store.Sessions.Should().BeEmpty();
    }

    [Fact]
    public async Task Specified_resumes_stored_id_and_capture_updates_it()
    {
        var conversationId = Guid.NewGuid();
        var definition = ClaudeAgentDefinition.Create();
        _store.Sessions[(conversationId, CliAgentKind.Claude)] = "stored-id";

        var plan = await _resolver.PrepareAsync(conversationId, definition);
        plan.ResumeSessionId.Should().Be("stored-id");
        plan.NewSessionId.Should().BeNull();

        // A resumed run may fork to a new id; the captured id from the stream wins.
        await _resolver.CaptureAsync(conversationId, definition, "forked-id");
        _store.Sessions[(conversationId, CliAgentKind.Claude)].Should().Be("forked-id");
    }

    [Fact]
    public async Task CapturedFromStream_persists_only_via_capture()
    {
        var conversationId = Guid.NewGuid();
        var definition = OpenCodeAgentDefinition.Create();

        var plan = await _resolver.PrepareAsync(conversationId, definition);
        plan.ResumeSessionId.Should().BeNull();
        plan.NewSessionId.Should().BeNull();
        _store.Sessions.Should().BeEmpty();

        await _resolver.CaptureAsync(conversationId, definition, "ses_123");
        _store.Sessions[(conversationId, CliAgentKind.OpenCode)].Should().Be("ses_123");

        var next = await _resolver.PrepareAsync(conversationId, definition);
        next.ResumeSessionId.Should().Be("ses_123");
    }

    [Fact]
    public async Task Capture_ignores_blank_ids()
    {
        var conversationId = Guid.NewGuid();

        await _resolver.CaptureAsync(conversationId, ClaudeAgentDefinition.Create(), null);
        await _resolver.CaptureAsync(conversationId, OpenCodeAgentDefinition.Create(), "");

        _store.Sessions.Should().BeEmpty();
    }

    private sealed class FakeSessionStore : ICliAgentSessionStore
    {
        public Dictionary<(Guid ConversationId, CliAgentKind Kind), string> Sessions { get; } = new();

        public Task<string?> GetSessionIdAsync(
            Guid conversationId,
            CliAgentKind agentKind,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Sessions.TryGetValue((conversationId, agentKind), out var value) ? value : null);

        public Task SetSessionIdAsync(
            Guid conversationId,
            CliAgentKind agentKind,
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            Sessions[(conversationId, agentKind)] = sessionId;
            return Task.CompletedTask;
        }
    }
}
