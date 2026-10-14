using System.Globalization;
using NCrontab;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;

namespace SelfClaw.Infrastructure.Automation;

/// <summary>
/// The single implementation of "when does this task fire". It converts local calendar occurrences to UTC
/// and therefore owns the DST rules: a local time that does not exist is skipped, and a repeated local
/// time is emitted once (the first, pre-transition instant).
/// </summary>
public sealed class AutomationScheduleCalculator
{
    private const int MaxProjectionSearchIterations = 100_000;
    private readonly TimeZoneInfo _timeZone;

    public AutomationScheduleCalculator(TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        _timeZone = timeZone;
    }

    public static AutomationMissedRunDisposition ClassifyMissed(DateTimeOffset lastDueUtc, DateTimeOffset nowUtc)
        => nowUtc - lastDueUtc <= AutomationDefaults.MissedRunGrace
            ? AutomationMissedRunDisposition.CatchUp
            : AutomationMissedRunDisposition.Stale;

    public bool TryComputeNextRun(
        AutomationTaskKind kind,
        AutomationScheduleSpec? schedule,
        DateTimeOffset fromUtc,
        out DateTimeOffset? nextRunAtUtc,
        out string? errorCode)
    {
        nextRunAtUtc = null;
        errorCode = null;
        if (!TryCreateSchedule(kind, schedule, out var cron, out errorCode))
        {
            return false;
        }

        if (kind == AutomationTaskKind.Once)
        {
            nextRunAtUtc = schedule is { RunAtUtc: DateTimeOffset runAt } && runAt > fromUtc ? runAt : null;
            return true;
        }

        if (cron is null)
        {
            return true;
        }

        if (!IsIntervalAllowed(kind, cron, fromUtc))
        {
            errorCode = AutomationErrorCodes.IntervalTooShort;
            return false;
        }

        nextRunAtUtc = FindNextOccurrenceUtc(cron, fromUtc);
        return true;
    }

    public bool TryBuildCronExpression(
        AutomationTaskKind kind,
        AutomationScheduleSpec? schedule,
        out string expression,
        out string? errorCode)
    {
        expression = string.Empty;
        errorCode = null;
        switch (kind)
        {
            case AutomationTaskKind.Daily:
            case AutomationTaskKind.Weekly:
            case AutomationTaskKind.Monthly:
                if (schedule is null || !TryReadTimeOfDay(schedule.Time, out var time))
                {
                    errorCode = AutomationErrorCodes.InvalidSchedule;
                    return false;
                }

                if (kind == AutomationTaskKind.Daily)
                {
                    expression = $"{time.Minute} {time.Hour} * * *";
                    return true;
                }

                if (kind == AutomationTaskKind.Weekly)
                {
                    if (schedule.Weekdays is not { Count: > 0 } weekdays)
                    {
                        errorCode = AutomationErrorCodes.InvalidSchedule;
                        return false;
                    }

                    if (weekdays.Any(day => day is < 0 or > 6))
                    {
                        errorCode = AutomationErrorCodes.InvalidSchedule;
                        return false;
                    }

                    expression = $"{time.Minute} {time.Hour} * * {string.Join(',', weekdays)}";
                    return true;
                }

                if (schedule.DayOfMonth is not int dayOfMonth || dayOfMonth is < 1 or > 31)
                {
                    errorCode = AutomationErrorCodes.InvalidSchedule;
                    return false;
                }

                expression = $"{time.Minute} {time.Hour} {dayOfMonth} * *";
                return true;
            case AutomationTaskKind.Cron:
                if (schedule is null || string.IsNullOrWhiteSpace(schedule.Cron) ||
                    CrontabSchedule.TryParse(schedule.Cron) is null)
                {
                    errorCode = AutomationErrorCodes.InvalidSchedule;
                    return false;
                }

                expression = schedule.Cron.Trim();
                return true;
            default:
                errorCode = AutomationErrorCodes.InvalidSchedule;
                return false;
        }
    }

    public IReadOnlyList<DateTimeOffset> ProjectOccurrences(
        AutomationTaskKind kind,
        AutomationScheduleSpec? schedule,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int limit)
    {
        var occurrences = new List<DateTimeOffset>();
        if (limit <= 0 || toUtc <= fromUtc)
        {
            return occurrences;
        }

        if (kind == AutomationTaskKind.None)
        {
            return occurrences;
        }

        if (kind == AutomationTaskKind.Once)
        {
            if (schedule?.RunAtUtc is DateTimeOffset runAt && runAt >= fromUtc && runAt <= toUtc)
            {
                occurrences.Add(runAt);
            }

            return occurrences;
        }

        if (!TryBuildCronExpression(kind, schedule, out var expression, out _) ||
            CrontabSchedule.TryParse(expression) is not { } cron)
        {
            return occurrences;
        }

        var localTo = TimeZoneInfo.ConvertTimeFromUtc(toUtc.UtcDateTime, _timeZone);
        var candidate = TimeZoneInfo.ConvertTimeFromUtc(fromUtc.UtcDateTime, _timeZone).AddTicks(-1);
        try
        {
            for (var iteration = 0; iteration < MaxProjectionSearchIterations && occurrences.Count < limit; iteration++)
            {
                var next = cron.GetNextOccurrence(candidate);
                if (next == DateTime.MaxValue || next > localTo)
                {
                    break;
                }

                candidate = next;
                if (!TryConvertLocalToUtc(next, out var utc) || utc < fromUtc)
                {
                    continue;
                }

                occurrences.Add(utc);
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            // NCrontab walks the calendar to year 10000 for a date that never occurs (for example Feb 30).
        }

        return occurrences;
    }

    private bool TryCreateSchedule(
        AutomationTaskKind kind,
        AutomationScheduleSpec? schedule,
        out CrontabSchedule? cron,
        out string? errorCode)
    {
        cron = null;
        errorCode = null;
        if (kind == AutomationTaskKind.None)
        {
            return true;
        }

        if (schedule is null || schedule.Version != 1)
        {
            errorCode = AutomationErrorCodes.InvalidSchedule;
            return false;
        }

        if (kind == AutomationTaskKind.Once)
        {
            if (schedule.RunAtUtc is null)
            {
                errorCode = AutomationErrorCodes.InvalidSchedule;
                return false;
            }

            return true;
        }

        if (!TryBuildCronExpression(kind, schedule, out var expression, out errorCode))
        {
            return false;
        }

        if (CrontabSchedule.TryParse(expression) is not { } parsed)
        {
            errorCode = AutomationErrorCodes.InvalidSchedule;
            return false;
        }

        cron = parsed;
        return true;
    }

    private bool IsIntervalAllowed(AutomationTaskKind kind, CrontabSchedule cron, DateTimeOffset fromUtc)
    {
        if (kind != AutomationTaskKind.Cron)
        {
            return true;
        }

        var first = FindNextOccurrenceUtc(cron, fromUtc);
        if (first is null)
        {
            return true;
        }

        var second = FindNextOccurrenceUtc(cron, first.Value.AddTicks(1));
        return second is null || second.Value - first.Value >= AutomationDefaults.MinimumInterval;
    }

    private DateTimeOffset? FindNextOccurrenceUtc(CrontabSchedule cron, DateTimeOffset fromUtc)
    {
        var localFrom = TimeZoneInfo.ConvertTimeFromUtc(fromUtc.UtcDateTime, _timeZone);
        var localLimit = localFrom.AddYears(5);
        var candidate = localFrom;
        try
        {
            for (var iteration = 0; iteration < MaxProjectionSearchIterations; iteration++)
            {
                var next = cron.GetNextOccurrence(candidate);
                if (next == DateTime.MaxValue || next > localLimit)
                {
                    return null;
                }

                candidate = next;
                if (TryConvertLocalToUtc(next, out var utc) && utc > fromUtc)
                {
                    return utc;
                }
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            // NCrontab walks the calendar to year 10000 for a date that never occurs (for example Feb 30).
        }

        return null;
    }

    private bool TryConvertLocalToUtc(DateTime local, out DateTimeOffset utc)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (_timeZone.IsInvalidTime(unspecified))
        {
            utc = default;
            return false;
        }

        if (_timeZone.IsAmbiguousTime(unspecified))
        {
            var offsets = _timeZone.GetAmbiguousTimeOffsets(unspecified);
            utc = new DateTimeOffset(unspecified, offsets.Max());
            return true;
        }

        utc = new DateTimeOffset(unspecified, _timeZone.GetUtcOffset(unspecified));
        return true;
    }

    private static bool TryReadTimeOfDay(string? value, out TimeOnly time)
        => TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
}
