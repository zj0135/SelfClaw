namespace SelfClaw.Desktop.Services.Automation;

/// <summary>
/// TickerQ request payload. A cron ticker carries only the task id and lets TickerQ supply the occurrence
/// time; a one-shot ticker pins <see cref="ScheduledForUtc"/> because it is registered for one exact moment.
/// </summary>
internal sealed record AutomationTickerPayload(Guid TaskId, DateTimeOffset? ScheduledForUtc);
