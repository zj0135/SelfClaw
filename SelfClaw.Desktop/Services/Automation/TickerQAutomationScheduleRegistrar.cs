using TickerQ.Utilities;
using TickerQ.Utilities.Entities;
using TickerQ.Utilities.Interfaces.Managers;

namespace SelfClaw.Desktop.Services.Automation;

internal sealed class TickerQAutomationScheduleRegistrar : IAutomationScheduleRegistrar
{
    internal const string FunctionName = "automation.run";
    private readonly ITimeTickerManager<TimeTickerEntity> _timeTickerManager;
    private readonly ICronTickerManager<CronTickerEntity> _cronTickerManager;

    public TickerQAutomationScheduleRegistrar(
        ITimeTickerManager<TimeTickerEntity> timeTickerManager,
        ICronTickerManager<CronTickerEntity> cronTickerManager)
    {
        _timeTickerManager = timeTickerManager;
        _cronTickerManager = cronTickerManager;
    }

    public async Task UpsertCronAsync(Guid taskId, string cronExpression, CancellationToken cancellationToken)
    {
        await _cronTickerManager.DeleteAsync(taskId, cancellationToken).ConfigureAwait(false);
        var entity = new CronTickerEntity
        {
            Id = taskId,
            Function = FunctionName,
            // TickerQ's cron parser requires the leading seconds field; the user-facing schedule stays 5-field.
            Expression = $"0 {cronExpression}",
            Request = TickerHelper.CreateTickerRequest(new AutomationTickerPayload(taskId, null)),
            Retries = 0,
            IsEnabled = true
        };
        await _cronTickerManager.AddAsync(entity, cancellationToken).ConfigureAwait(false);
    }

    public async Task UpsertOnceAsync(Guid taskId, DateTimeOffset scheduledForUtc, CancellationToken cancellationToken)
    {
        await _timeTickerManager.DeleteAsync(taskId, cancellationToken).ConfigureAwait(false);
        var entity = new TimeTickerEntity
        {
            Id = taskId,
            Function = FunctionName,
            ExecutionTime = scheduledForUtc.UtcDateTime,
            Request = TickerHelper.CreateTickerRequest(new AutomationTickerPayload(taskId, scheduledForUtc)),
            Retries = 0
        };
        await _timeTickerManager.AddAsync(entity, cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveAsync(Guid taskId, CancellationToken cancellationToken)
    {
        await _cronTickerManager.DeleteAsync(taskId, cancellationToken).ConfigureAwait(false);
        await _timeTickerManager.DeleteAsync(taskId, cancellationToken).ConfigureAwait(false);
    }
}
