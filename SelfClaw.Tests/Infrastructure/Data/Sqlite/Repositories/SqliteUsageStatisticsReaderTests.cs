using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Infrastructure.Agents.Subagents.Persistence;
using SelfClaw.Infrastructure.Agents.Subagents.Runtime;
using SelfClaw.Infrastructure.Data.Sqlite.Repositories;
using SelfClaw.Tests.TestDoubles;

namespace SelfClaw.Tests.Infrastructure.Data.Sqlite.Repositories;

public sealed class SqliteUsageStatisticsReaderTests
{
    [Fact]
    public async Task GetConversationUsageAsync_groups_turn_usage_without_needing_assistant_messages()
    {
        using var fixture = new ConversationPersistenceFixture();
        var start = await fixture.StartAsync();
        await fixture.Turns.TryFinalizeTurnAsync(new(start.Turn with
        {
            Status = ConversationTurnStatus.Failed, CompletedAtUtc = DateTimeOffset.UtcNow,
            Usage = new TurnUsage(Model: "model-a", InputTokens: 100, UncachedInputTokens: 60, CachedInputTokens: 40,
                OutputTokens: 10, ProviderCalls: 2, CostUsdMicros: 1_500, CostSource: TurnUsageCostSource.ProviderReported)
        }, [], []));
        var conversation = await fixture.Conversations.GetConversationAsync(start.Turn.ConversationId)
            ?? throw new InvalidOperationException("Missing conversation.");
        var next = start.Turn with { Id = Guid.NewGuid(), StartedAtUtc = DateTimeOffset.UtcNow };
        await fixture.Turns.StartTurnAsync(new(conversation, next, "next", Guid.NewGuid()));
        await fixture.Turns.TryFinalizeTurnAsync(new(next with
        {
            Status = ConversationTurnStatus.Succeeded, CompletedAtUtc = DateTimeOffset.UtcNow,
            Usage = new TurnUsage(Model: "model-b", InputTokens: 50, UncachedInputTokens: 50, OutputTokens: 5, ProviderCalls: 1)
        }, [], []));

        var report = await new SqliteUsageStatisticsReader(fixture.Database).GetConversationUsageAsync(conversation.Id);
        report.Models.Should().HaveCount(2);
        var modelA = report.Models.Single(model => model.Model == "model-a");
        modelA.Totals.InputTokens.Should().Be(100);
        modelA.Totals.CachedInputTokens.Should().Be(40);
        modelA.Totals.CostUsdMicros.Should().Be(1_500);
        modelA.Totals.TurnCount.Should().Be(1);
        modelA.Totals.PricedTurnCount.Should().Be(1);
        modelA.CacheHitRate.Should().BeApproximately(0.4, 0.0001);
        report.Models.Single(model => model.Model == "model-b").Totals.PricedTurnCount.Should().Be(0);
        report.Totals.InputTokens.Should().Be(150);
        report.Totals.ProviderCalls.Should().Be(3);
        report.Totals.TurnCount.Should().Be(2);
        report.Totals.PricedTurnCount.Should().Be(1);
        report.Totals.CostUsdMicros.Should().Be(1_500);
        report.CacheHitRate.Should().BeApproximately(40d / 150d, 0.0001);
        (await fixture.Conversations.ListMessagesAsync(conversation.Id)).Should().OnlyContain(message => message.Role == MessageRole.User);
    }

    [Fact]
    public async Task GetSubagentUsageAsync_joins_child_turn_identity_and_keeps_parent_usage_separate()
    {
        using var fixture = new ConversationPersistenceFixture();
        var tasks = new SqliteSubagentTaskRepository(fixture.Database, new SubagentCompletionEnvelopeFactory());
        var task = await SubagentTaskTestData.CreateRunningTaskAsync(fixture.Conversations, tasks);
        var childTurn = (await fixture.Turns.ListTurnsAsync(task.ChildConversationId)).Single();
        await tasks.TryCompleteAsync(task.Id, SubagentTaskStatus.Running, new(SubagentTaskStatus.Failed,
            new(childTurn with
            {
                Status = ConversationTurnStatus.Failed, CompletedAtUtc = DateTimeOffset.UtcNow,
                Usage = new TurnUsage(Model: "child-model", InputTokens: 20, UncachedInputTokens: 20, OutputTokens: 2)
            }, [], []), null, "provider", "failed", DateTimeOffset.UtcNow));
        var parent = await fixture.Conversations.GetConversationAsync(task.ParentConversationId)
            ?? throw new InvalidOperationException("Missing parent.");
        var parentTurn = new ConversationTurnRecord(Guid.NewGuid(), parent.Id, AgentExecutionMode.Direct,
            DirectTurnOrigin.Interactive, ConversationTurnStatus.Running, DateTimeOffset.UtcNow);
        await fixture.Turns.StartTurnAsync(new(parent, parentTurn, "parent", Guid.NewGuid()));
        await fixture.Turns.TryFinalizeTurnAsync(new(parentTurn with
        {
            Status = ConversationTurnStatus.Succeeded, CompletedAtUtc = DateTimeOffset.UtcNow,
            Usage = new TurnUsage(Model: "parent-model", InputTokens: 100, UncachedInputTokens: 100, OutputTokens: 10)
        }, [], []));

        var reader = new SqliteUsageStatisticsReader(fixture.Database);
        var parentReport = await reader.GetConversationUsageAsync(parent.Id);
        var subagentReport = await reader.GetSubagentUsageAsync(parent.Id);
        parentReport.Models.Should().ContainSingle().Which.Model.Should().Be("parent-model");
        parentReport.Totals.InputTokens.Should().Be(100);
        subagentReport.Models.Should().ContainSingle().Which.Model.Should().Be("child-model");
        subagentReport.Totals.InputTokens.Should().Be(20);
        subagentReport.Totals.TurnCount.Should().Be(1);
    }
}
