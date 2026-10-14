using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Infrastructure.Automation;

namespace SelfClaw.Tests.Infrastructure.Automation;

public sealed class AutomationScheduleCalculatorTests
{
    private static readonly TimeZoneInfo PlusEight = TimeZoneInfo.CreateCustomTimeZone(
        "test-plus-eight",
        TimeSpan.FromHours(8),
        "Test +8",
        "Test +8");
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    [Fact]
    public void Daily_schedule_returns_the_next_local_occurrence()
    {
        var schedule = new AutomationScheduleSpec(Time: "09:30");

        Next(AutomationTaskKind.Daily, schedule, Utc(2026, 1, 1, 0, 0), PlusEight)
            .Should().Be(Utc(2026, 1, 1, 1, 30));
        Next(AutomationTaskKind.Daily, schedule, Utc(2026, 1, 1, 2, 0), PlusEight)
            .Should().Be(Utc(2026, 1, 2, 1, 30));
    }

    [Fact]
    public void Weekly_schedule_uses_the_selected_weekdays()
    {
        var schedule = new AutomationScheduleSpec(Time: "09:00", Weekdays: [1, 3, 5]);

        // 2026-01-01 is a Thursday; the next Mon/Wed/Fri slot is Friday 2026-01-02, then Monday 2026-01-05.
        Next(AutomationTaskKind.Weekly, schedule, Utc(2026, 1, 1, 0, 0), PlusEight)
            .Should().Be(Utc(2026, 1, 2, 1, 0));
        Next(AutomationTaskKind.Weekly, schedule, Utc(2026, 1, 2, 2, 0), PlusEight)
            .Should().Be(Utc(2026, 1, 5, 1, 0));
    }

    [Fact]
    public void Monthly_schedule_returns_the_next_day_of_month()
    {
        var schedule = new AutomationScheduleSpec(Time: "10:00", DayOfMonth: 15);

        Next(AutomationTaskKind.Monthly, schedule, Utc(2026, 1, 16, 0, 0), PlusEight)
            .Should().Be(Utc(2026, 2, 15, 2, 0));
    }

    [Fact]
    public void Once_schedule_returns_run_at_until_it_passes()
    {
        var schedule = new AutomationScheduleSpec(RunAtUtc: Utc(2026, 3, 1, 0, 0));

        Next(AutomationTaskKind.Once, schedule, Utc(2026, 2, 1, 0, 0), PlusEight)
            .Should().Be(Utc(2026, 3, 1, 0, 0));
        Run(AutomationTaskKind.Once, schedule, Utc(2026, 4, 1, 0, 0), PlusEight)
            .Should().BeNull();
    }

    [Fact]
    public void Cron_schedule_returns_the_next_occurrence()
    {
        var schedule = new AutomationScheduleSpec(Cron: "*/15 * * * *");

        Next(AutomationTaskKind.Cron, schedule, Utc(2026, 1, 1, 0, 7), PlusEight)
            .Should().Be(Utc(2026, 1, 1, 0, 15));
    }

    [Fact]
    public void Spring_forward_gap_is_skipped()
    {
        var schedule = new AutomationScheduleSpec(Time: "02:30");

        // 2026-03-08 is the US spring-forward day: 02:30 local does not exist.
        Next(AutomationTaskKind.Daily, schedule, Utc(2026, 3, 8, 0, 0), NewYork)
            .Should().Be(Utc(2026, 3, 9, 6, 30));
    }

    [Fact]
    public void Fall_back_repeated_time_fires_once()
    {
        var schedule = new AutomationScheduleSpec(Time: "01:30");

        // 2026-11-01 01:30 local happens twice (EDT then EST); only the first UTC instant is used.
        Next(AutomationTaskKind.Daily, schedule, Utc(2026, 11, 1, 0, 0), NewYork)
            .Should().Be(Utc(2026, 11, 1, 5, 30));

        var occurrences = new AutomationScheduleCalculator(NewYork)
            .ProjectOccurrences(AutomationTaskKind.Daily, schedule, Utc(2026, 11, 1, 0, 0), Utc(2026, 11, 2, 0, 0), 10);
        occurrences.Should().ContainSingle().Which.Should().Be(Utc(2026, 11, 1, 5, 30));
    }

    [Fact]
    public void Short_month_is_skipped_without_throwing()
    {
        var schedule = new AutomationScheduleSpec(Time: "09:00", DayOfMonth: 31);

        Next(AutomationTaskKind.Monthly, schedule, Utc(2026, 1, 31, 5, 0), PlusEight)
            .Should().Be(Utc(2026, 3, 31, 1, 0));
    }

    [Theory]
    [InlineData("* * * * *")]
    [InlineData("*/4 * * * *")]
    public void Sub_five_minute_cron_is_rejected(string cron)
    {
        var calculator = new AutomationScheduleCalculator(PlusEight);

        var succeeded = calculator.TryComputeNextRun(
            AutomationTaskKind.Cron,
            new AutomationScheduleSpec(Cron: cron),
            Utc(2026, 1, 1, 0, 0),
            out var nextRunAtUtc,
            out var errorCode);

        succeeded.Should().BeFalse();
        nextRunAtUtc.Should().BeNull();
        errorCode.Should().Be(AutomationErrorCodes.IntervalTooShort);
    }

    [Fact]
    public void Five_minute_cron_is_allowed()
    {
        Next(AutomationTaskKind.Cron, new AutomationScheduleSpec(Cron: "*/5 * * * *"), Utc(2026, 1, 1, 0, 2), PlusEight)
            .Should().Be(Utc(2026, 1, 1, 0, 5));
    }

    [Fact]
    public void Invalid_cron_is_rejected()
    {
        var calculator = new AutomationScheduleCalculator(PlusEight);

        var succeeded = calculator.TryComputeNextRun(
            AutomationTaskKind.Cron,
            new AutomationScheduleSpec(Cron: "not a cron"),
            Utc(2026, 1, 1),
            out _,
            out var errorCode);

        succeeded.Should().BeFalse();
        errorCode.Should().Be(AutomationErrorCodes.InvalidSchedule);
    }

    [Fact]
    public void Classify_missed_uses_the_six_hour_grace_boundary()
    {
        var now = Utc(2026, 1, 1, 12, 0);

        AutomationScheduleCalculator.ClassifyMissed(now - TimeSpan.FromHours(5) - TimeSpan.FromMinutes(59), now)
            .Should().Be(AutomationMissedRunDisposition.CatchUp);
        AutomationScheduleCalculator.ClassifyMissed(now - TimeSpan.FromHours(6), now)
            .Should().Be(AutomationMissedRunDisposition.CatchUp);
        AutomationScheduleCalculator.ClassifyMissed(now - TimeSpan.FromHours(6) - TimeSpan.FromMinutes(1), now)
            .Should().Be(AutomationMissedRunDisposition.Stale);
    }

    [Fact]
    public void Projection_is_capped_at_the_limit()
    {
        var occurrences = new AutomationScheduleCalculator(PlusEight).ProjectOccurrences(
            AutomationTaskKind.Cron,
            new AutomationScheduleSpec(Cron: "*/5 * * * *"),
            Utc(2026, 1, 1, 0, 0),
            Utc(2026, 1, 31, 0, 0),
            3);

        occurrences.Should().HaveCount(3);
    }

    [Fact]
    public void Impossible_cron_date_does_not_throw()
    {
        var calculator = new AutomationScheduleCalculator(PlusEight);
        var schedule = new AutomationScheduleSpec(Cron: "0 0 30 2 *");

        var succeeded = calculator.TryComputeNextRun(AutomationTaskKind.Cron, schedule, Utc(2026, 1, 1), out var nextRunAtUtc, out _);

        succeeded.Should().BeTrue();
        nextRunAtUtc.Should().BeNull();
        calculator.ProjectOccurrences(AutomationTaskKind.Cron, schedule, Utc(2026, 1, 1), Utc(2027, 1, 1), 10)
            .Should().BeEmpty();
    }

    private static DateTimeOffset Next(
        AutomationTaskKind kind,
        AutomationScheduleSpec schedule,
        DateTimeOffset fromUtc,
        TimeZoneInfo timeZone)
    {
        var next = Run(kind, schedule, fromUtc, timeZone);
        next.Should().NotBeNull();
        return next!.Value;
    }

    private static DateTimeOffset? Run(
        AutomationTaskKind kind,
        AutomationScheduleSpec schedule,
        DateTimeOffset fromUtc,
        TimeZoneInfo timeZone)
    {
        var calculator = new AutomationScheduleCalculator(timeZone);
        var succeeded = calculator.TryComputeNextRun(kind, schedule, fromUtc, out var nextRunAtUtc, out var errorCode);
        succeeded.Should().BeTrue(errorCode);
        return nextRunAtUtc;
    }

    private static DateTimeOffset Utc(int year, int month, int day, int hour = 0, int minute = 0)
        => new(year, month, day, hour, minute, 0, TimeSpan.Zero);
}
