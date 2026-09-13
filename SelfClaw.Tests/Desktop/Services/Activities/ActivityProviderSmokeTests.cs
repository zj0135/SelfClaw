using System.Diagnostics;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure;
using SelfClaw.Infrastructure.Agents.Runtime;
using SelfClaw.Infrastructure.AiProviders.Abstractions;
using SelfClaw.Infrastructure.Options;
using SelfClaw.Tests.TestDoubles;
using Xunit.Abstractions;

namespace SelfClaw.Tests.Desktop.Services.Activities;

public sealed class ActivityProviderSmokeTests(ITestOutputHelper output)
{
    [ProviderSmokeFact]
    public Task Configured_provider_reasoning_reaches_the_live_activity_channel() => WpfDispatcherTest.RunAsync(async () =>
    {
        var source = StoragePaths.CreateDefault();
        var root = Path.Combine(Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var paths = new StoragePaths(root, Path.Combine(root, "provider.db"), source.SecretsDirectory);
        using (var original = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = source.DatabasePath, Mode = SqliteOpenMode.ReadOnly }.ToString()))
        using (var copy = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = paths.DatabasePath }.ToString()))
        {
            await original.OpenAsync();
            await copy.OpenAsync();
            original.BackupDatabase(copy);
        }
        await using var services = new ServiceCollection().AddLogging().AddSelfClawInfrastructure(paths).BuildServiceProvider();
        var repository = services.GetRequiredService<IAiProviderRepository>();
        await repository.InitializeAsync();
        var profiles = await repository.ListEnabledModelProfilesAsync();
        foreach (var candidate in profiles)
            output.WriteLine("Profile={0}; Model={1}; Reasoning={2}", candidate.Id, candidate.Model, candidate.Configuration?.ReasoningEffort ?? "default");
        var requested = Environment.GetEnvironmentVariable("SELFCLAW_PROVIDER_MODEL");
        var profile = profiles.FirstOrDefault(candidate => candidate.Id.ToString("D") == requested)
            ?? profiles.FirstOrDefault(candidate => candidate.Configuration?.ReasoningEffort is string effort && effort != "none")
            ?? profiles.FirstOrDefault()
            ?? throw new InvalidOperationException("No enabled provider model is configured.");
        using var activity = new SubagentActivityTestContext();
        var task = await activity.CreateTaskAsync();
        await using var session = await activity.RegisterSessionAsync(task);
        await session.BeginAsync();
        using var panel = new ActivityPanelTestContext(activity, task.ParentConversationId);
        var subscription = Guid.NewGuid();
        await panel.Publisher.SubscribeAsync(subscription, task.ParentConversationId, "initial", CancellationToken.None);
        panel.AcknowledgeLatest();
        await panel.Publisher.SelectDetailAsync(subscription, Guid.NewGuid(), task.Id, null, null, "detail", CancellationToken.None);
        panel.AcknowledgeLatest();
        var now = DateTimeOffset.UtcNow;
        var request = new DirectChatTurnRequest(task.ChildTurnId, task.ChildConversationId, null,
            new AgentRuntimeDefinition("verification", "Verification", "", AgentExecutionMode.Direct, "none", [], [], [], [], "Solve the supplied arithmetic problem. Give a concise answer."),
            [new MessageRecord(Guid.NewGuid(), task.ChildConversationId, MessageRole.User, "What is the remainder when 7^123 is divided by 13? Verify the result.", MessageStatus.Completed, now, now)],
            profile.Id, ToolPermissionMode.RequireApproval, null,
            new DirectTurnExecutionContext(DirectTurnOrigin.Subagent, new DirectCapabilityCeiling("none", [], [], [], []), null));
        var runtime = services.GetRequiredService<DirectAgentChatRuntime>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var timer = Stopwatch.StartNew();
        var reasoningEvents = 0;
        var textEvents = 0;
        var visibleBeforeTerminal = false;
        await foreach (var streamEvent in runtime.StreamTurnAsync(request, timeout.Token))
        {
            if (streamEvent is AssistantThinkingDeltaEvent) reasoningEvents++;
            if (streamEvent is AssistantTextDeltaEvent) textEvents++;
            await session.ApplyEventAsync(streamEvent, timeout.Token);
            if (streamEvent is AssistantThinkingDeltaEvent && !visibleBeforeTerminal)
            {
                await panel.Publisher.GetStateAsync(subscription, null, "reasoning", timeout.Token);
                panel.AcknowledgeLatest();
                visibleBeforeTerminal = panel.LatestState.GetProperty("sections")[0].GetProperty("detail")
                    .GetProperty("message").GetProperty("segments").EnumerateArray()
                    .Any(segment => segment.GetProperty("kind").GetString() == "thinking");
            }
            panel.AcknowledgeLatest();
        }
        output.WriteLine("SelectedProfile={0}; Parent={1}; Task={2}; ReasoningEvents={3}; TextEvents={4}; LiveThinkingVisible={5}; DurationMs={6}",
            profile.Id, task.ParentConversationId, task.Id, reasoningEvents, textEvents, visibleBeforeTerminal, timer.ElapsedMilliseconds);
        var detail = await activity.Service.GetDetailAsync(task.ParentConversationId, task.Id);
        detail?.Activity.Task.Status.Should().Be(SubagentTaskStatus.Succeeded, "{0}: {1}", detail?.Activity.Task.ErrorCode, detail?.Activity.Task.ErrorMessage);
        reasoningEvents.Should().BeGreaterThan(0, "this smoke requires a provider that actually emits reasoning");
        visibleBeforeTerminal.Should().BeTrue();
    }, 150);
}
