using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Agents.Cli.Session;
using SelfClaw.Infrastructure.Data.Sqlite;
using SelfClaw.Infrastructure.Data.Sqlite.Repositories;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Tests.Infrastructure.Agents.Cli.Session;

public sealed class SqliteCliAgentSessionStoreTests : IDisposable
{
    private readonly string _rootPath;

    public SqliteCliAgentSessionStoreTests()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public async Task TrackCumulativeCostAsync_records_only_the_turn_delta_of_a_resumed_session()
    {
        var (store, conversationId) = await CreateStoreAsync();

        // A fresh session reports its own total.
        var first = await store.TrackCumulativeCostAsync(conversationId, CliAgentKind.Claude, "session", 2_500_000);
        // A resumed process starts from the transcript's running total, so only the difference belongs to this turn.
        var second = await store.TrackCumulativeCostAsync(conversationId, CliAgentKind.Claude, "session", 3_000_000);

        first.Should().Be(2_500_000);
        second.Should().Be(500_000);
    }

    [Fact]
    public async Task TrackCumulativeCostAsync_treats_a_value_below_the_baseline_as_a_session_reset()
    {
        var (store, conversationId) = await CreateStoreAsync();
        await store.TrackCumulativeCostAsync(conversationId, CliAgentKind.Claude, "session", 2_500_000);

        var afterReset = await store.TrackCumulativeCostAsync(conversationId, CliAgentKind.Claude, "session", 700_000);

        afterReset.Should().Be(700_000);
    }

    [Fact]
    public async Task TrackCumulativeCostAsync_keeps_the_baseline_when_the_cli_reports_zero()
    {
        var (store, conversationId) = await CreateStoreAsync();
        await store.TrackCumulativeCostAsync(conversationId, CliAgentKind.Claude, "session", 2_500_000);

        var zeroed = await store.TrackCumulativeCostAsync(conversationId, CliAgentKind.Claude, "session", 0);
        var resumed = await store.TrackCumulativeCostAsync(conversationId, CliAgentKind.Claude, "session", 2_800_000);

        zeroed.Should().Be(0);
        resumed.Should().Be(300_000);
    }

    [Fact]
    public async Task SetSessionIdAsync_resets_the_baseline_when_the_session_changes()
    {
        var (store, conversationId) = await CreateStoreAsync();
        await store.SetSessionIdAsync(conversationId, CliAgentKind.Claude, "first-session");
        await store.TrackCumulativeCostAsync(conversationId, CliAgentKind.Claude, "first-session", 2_500_000);

        // Keeping the same session id preserves the baseline.
        await store.SetSessionIdAsync(conversationId, CliAgentKind.Claude, "first-session");
        (await store.TrackCumulativeCostAsync(conversationId, CliAgentKind.Claude, "first-session", 2_600_000))
            .Should().Be(100_000);

        // A new session id starts from zero again.
        await store.SetSessionIdAsync(conversationId, CliAgentKind.Claude, "second-session");
        (await store.TrackCumulativeCostAsync(conversationId, CliAgentKind.Claude, "second-session", 400_000))
            .Should().Be(400_000);
    }

    [Fact]
    public async Task TrackCumulativeCostAsync_rejects_a_negative_total()
    {
        var (store, conversationId) = await CreateStoreAsync();

        var act = () => store.TrackCumulativeCostAsync(conversationId, CliAgentKind.Claude, "session", -1);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    private async Task<(SqliteCliAgentSessionStore Store, Guid ConversationId)> CreateStoreAsync()
    {
        var storagePaths = StoragePathDefaults.Create(
            _rootPath,
            Path.Combine(_rootPath, "selfclaw.db"),
            Path.Combine(_rootPath, "secrets"));
        var database = new SqliteDatabase(storagePaths);
        var conversationRepository = new SqliteConversationRepository(database);
        await conversationRepository.InitializeAsync();

        var now = DateTimeOffset.UtcNow;
        var conversationId = Guid.NewGuid();
        await conversationRepository.UpsertConversationAsync(new ConversationRecord(
            conversationId,
            "Usage",
            WorkspaceRootId: null,
            ConversationMode.Programming,
            ToolPermissionMode.RequireApproval,
            "build",
            now,
            now));

        return (new SqliteCliAgentSessionStore(database), conversationId);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_rootPath))
        {
            return;
        }

        try
        {
            Directory.Delete(_rootPath, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
