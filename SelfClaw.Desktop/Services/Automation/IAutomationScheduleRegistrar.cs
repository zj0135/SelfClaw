namespace SelfClaw.Desktop.Services.Automation;

/// <summary>
/// Scheduling seam. TickerQ is rebuildable from automation_tasks, so the registrar only converges a
/// single ticker per task and never owns schedule state.
/// </summary>
internal interface IAutomationScheduleRegistrar
{
    Task UpsertCronAsync(Guid taskId, string cronExpression, CancellationToken cancellationToken);

    Task UpsertOnceAsync(Guid taskId, DateTimeOffset scheduledForUtc, CancellationToken cancellationToken);

    Task RemoveAsync(Guid taskId, CancellationToken cancellationToken);
}
