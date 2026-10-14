using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Desktop.Composition;
using SelfClaw.Desktop.Services.Automation;
using SelfClaw.Infrastructure.Automation;
using SelfClaw.Infrastructure.Data.Sqlite;
using SelfClaw.Infrastructure.Data.Sqlite.Repositories;
using SelfClaw.Infrastructure.Options;
using TickerQ.DependencyInjection;
using TickerQ.Utilities.Enums;

namespace SelfClaw.Tests.Desktop.Services.Automation;

public sealed class AutomationTickerQIntegrationTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(),
        "SelfClawTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Once_ticker_fires_through_tickerq_and_the_placeholder_executor_records_the_run()
    {
        var database = new SqliteDatabase(StoragePathDefaults.Create(
            _rootPath,
            Path.Combine(_rootPath, "selfclaw.db"),
            Path.Combine(_rootPath, "secrets")));
        await database.EnsureInitializedAsync();
        var taskStore = new SqliteAutomationTaskRepository(database);
        var runStore = new SqliteAutomationRunRepository(database);

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSingleton<IAutomationTaskStore>(taskStore);
        builder.Services.AddSingleton<IAutomationRunStore>(runStore);
        builder.Services.AddSingleton(new AutomationScheduleCalculator(TimeZoneInfo.Local));
        builder.Services.AddSingleton<IAutomationStartupRecovery>(new AutomationStartupRecovery(database));
        builder.Services.AddAutomationScheduler();
        var host = builder.Build();
        host.UseTickerQ(TickerQStartMode.Immediate);
        await host.StartAsync();
        try
        {
            var scheduler = host.Services.GetRequiredService<AutomationSchedulerHost>();
            var runAt = DateTimeOffset.UtcNow.AddSeconds(3);
            var task = CreateOnceTask(runAt);
            await taskStore.SaveAsync(task);
            await scheduler.RebuildScheduleAsync(CancellationToken.None);

            var recorded = await WaitForRunAsync(runStore, task.Id, TimeSpan.FromSeconds(30));

            recorded.Should().NotBeNull();
            recorded!.Status.Should().Be(AutomationRunStatus.Failed);
            recorded.ErrorCode.Should().Be(SelfClaw.Core.Runtime.AutomationErrorCodes.NotImplemented);
            recorded.Trigger.Should().Be(AutomationRunTrigger.Scheduled);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    public void Dispose()
    {
        if (!Directory.Exists(_rootPath))
        {
            return;
        }

        try
        {
            Directory.Delete(_rootPath, true);
        }
        catch (IOException)
        {
        }
    }

    private static async Task<AutomationRunRecord?> WaitForRunAsync(
        IAutomationRunStore runStore,
        Guid taskId,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var runs = await runStore.ListAsync(taskId, 1);
            if (runs.Count > 0 && runs[0].Status != AutomationRunStatus.Queued)
            {
                return runs[0];
            }

            await Task.Delay(250);
        }

        return null;
    }

    private static AutomationTaskRecord CreateOnceTask(DateTimeOffset runAtUtc)
    {
        var now = DateTimeOffset.UtcNow;
        return new AutomationTaskRecord(
            Guid.NewGuid(),
            "One-shot",
            string.Empty,
            AutomationTaskKind.Once,
            new AutomationScheduleSpec(RunAtUtc: runAtUtc),
            runAtUtc,
            "Do the thing",
            string.Empty,
            "system",
            [],
            [],
            [],
            null,
            null,
            ToolPermissionMode.FullAccess,
            AutomationTaskStatus.Open,
            null,
            null,
            true,
            null,
            null,
            now,
            now);
    }
}
